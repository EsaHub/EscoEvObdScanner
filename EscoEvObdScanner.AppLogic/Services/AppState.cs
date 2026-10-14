using System.Collections.Concurrent;
using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.AppLogic.Services
{
    /// <summary>What every tab shares: settings, the chosen vehicle and car file, the adapter and the log.</summary>
    public sealed class AppState : ObservableObject
    {
        StreamWriter? _logFile;
        string? _logPath;
        bool _logUsed;
        readonly ConcurrentQueue<string> _pending = new();
        readonly IDisposable _flush;
        Catalog _catalog;
        string _catalogPath;
        bool _isDemoCar;
        Vehicle? _vehicle;

        public AppState()
        {
            Settings = Settings.Load();
            Loc.I.Choose(Settings.Language);

            // A car file that cannot be loaded must not stop the app: try the
            // shipped file of the same car, then every other car, and say what
            // was skipped. Only when no car file loads does the caller show the
            // error and exit.
            (_catalogPath, _catalog, StartupWarning) = LoadStartupCar(Settings.Car, _startupUpdates);
            // the demo of the remembered car only: after a fallback to another car, its real self
            _isDemoCar = Settings.DemoCar && _catalog.Platform.Demo != null
                         && string.Equals(CarFiles.NameOf(_catalogPath), Settings.Car, StringComparison.OrdinalIgnoreCase);
            // the remembered vehicle, if it is a vehicle of the car file in use
            VehicleList = Vehicles.Load();
            _vehicle = _isDemoCar ? null : VehicleList.FirstOrDefault(v =>
                string.Equals(v.Name, Settings.Vehicle, StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.Car, CarFiles.NameOf(_catalogPath), StringComparison.OrdinalIgnoreCase));
            Results.Car = CarKey(_catalogPath, _isDemoCar, _vehicle);
            // the remembered vehicle could not be used (its car file is gone): say so instead of quietly using another folder
            if (!_isDemoCar && _vehicle == null && !string.IsNullOrEmpty(Settings.Vehicle))
                StartupWarning = string.Join("; ", new[] { StartupWarning, Loc.I.Format("VehicleNotAvailable", Settings.Vehicle) }.Where(w => w != null));

            OpenLog();
            foreach (var r in _startupUpdates) ReportUpdate(r);

            // Lines are queued from any thread and handed to the UI in batches,
            // so adapter traffic during live polling does not flood the UI thread.
            _flush = AppHost.I.Every(TimeSpan.FromMilliseconds(200), Flush);

            Adapter.Traffic += line => { if (Settings.ShowTraffic) Log(line); };
        }

        public Settings Settings { get; }

        public AdapterService Adapter { get; } = new();

        public Catalog Catalog { get => _catalog; private set => Set(ref _catalog, value); }

        public string CatalogPath { get => _catalogPath; private set => Set(ref _catalogPath, value); }

        /// <summary>The simulated car built from the car file is chosen, not the real car.</summary>
        public bool IsDemoCar { get => _isDemoCar; private set => Set(ref _isDemoCar, value); }

        /// <summary>The user's vehicles; saved with <see cref="SaveVehicles"/>.</summary>
        public List<Vehicle> VehicleList { get; }

        public void SaveVehicles() => Vehicles.Save(VehicleList);

        /// <summary>The vehicle being measured; null for the demo car, or a car file used without one.</summary>
        public Vehicle? Vehicle { get => _vehicle; private set => Set(ref _vehicle, value); }

        /// <summary>The VIN the car on the adapter gave at the latest connection; kept until the next connection or another car.</summary>
        public string? Vin { get; set; }

        /// <summary>Whether the VIN was asked from the car: without it, a missing VIN means "not read" rather than "the car did not tell".</summary>
        public bool VinAsked { get; set; }

        /// <summary>The chosen car as the list and the reports name it: the vehicle's name with its platform.</summary>
        public string CarTitle => IsDemoCar ? Loc.I.Format("DemoCarTitle", Catalog.Name)
            : Vehicle != null ? Loc.I.Format("VehicleTitle", Vehicle.Name, Catalog.Name) : Catalog.Name;

        /// <summary>The chosen car's name for results, logs and drive history.</summary>
        public string CarId => CarKey(CatalogPath, IsDemoCar, Vehicle);

        /// <summary>
        /// The name results, logs and drive history go under: a vehicle's own folder, the demo car's
        /// apart from every real car's, and the car file's name for a car file used without a vehicle.
        /// </summary>
        static string CarKey(string path, bool demo, Vehicle? vehicle) =>
            demo ? CarFiles.DemoName(CarFiles.NameOf(path))
            : vehicle != null ? Vehicles.FolderName(vehicle.Name)
            : CarFiles.NameOf(path);

        /// <summary>
        /// The remembered car, else its shipped original, else the first car that
        /// loads at all; a warning says what was skipped. Throws only when no car
        /// file can be read, and the caller shows that and exits.
        /// </summary>
        static (string Path, Catalog Catalog, string? Warning) LoadStartupCar(string? name, List<CarFileUpdate.Result> updates)
        {
            var problems = new List<string>();
            var tries = new List<Func<string?>>
            {
                () => AppPaths.ResolveCar(name, updates.Add),
                () => name == null || AppPaths.Shipped(System.IO.Path.Combine(AppPaths.UserCarsDir, name + ".json")) is not string shipped
                      ? null : AppPaths.WorkingCopy(shipped, updates.Add)
            };
            // never the read-only shipped file itself: merge writes to the car file in use
            tries.AddRange(AppPaths.Cars().Select(c => (Func<string?>)(() => AppPaths.WorkingCopy(c, updates.Add))));

            foreach (var next in tries)
            {
                string? path = null;
                try
                {
                    path = next();
                    if (path == null) continue;
                    var catalog = Catalog.Load(path);
                    return (path, catalog, problems.Count > 0 ? string.Join("; ", problems.Distinct()) + $" → {path}" : null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or InvalidDataException or NotSupportedException)
                {
                    problems.Add($"{Loc.I["Catalog"]} {System.IO.Path.GetFileName(path ?? name ?? "")}: {ex.Message}");
                }
            }
            throw new InvalidDataException(string.Join("; ", problems.Distinct()));
        }

        /// <summary>Set when the remembered car file could not be loaded at startup, or the remembered vehicle could not be used.</summary>
        public string? StartupWarning { get; }

        /// <summary>Raised on the UI thread with the lines logged since the last batch.</summary>
        public event Action<IReadOnlyList<string>>? LogLines;

        /// <summary>Thread-safe: may be called from worker threads.</summary>
        public void Log(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss}  {message}";
            _pending.Enqueue(line);

            try
            {
                lock (_pending)                             // Log is called from worker threads too; the lock serialises the writer
                {
                    _logFile?.WriteLine(line);
                    _logUsed = true;
                }
            }
            catch
            {
                // a full disk or vanished folder must not take the adapter down with it
            }
        }

        /// <summary>A new log file in the chosen car's folder, so a car's logs never hold another car's traffic.</summary>
        void OpenLog()
        {
            lock (_pending)
            {
                CloseLogFile();
                _logUsed = false;
                try
                {
                    _logPath = Results.NewFile(Results.Logs, "app", ".log");
                    _logFile = new StreamWriter(_logPath) { AutoFlush = true };
                }
                catch
                {
                    _logFile = null;    // logging to file is optional
                    _logPath = null;
                }
            }
        }

        /// <summary>Closes the log; one nothing was written to (the car changed right after start, an idle run) is deleted.</summary>
        public void CloseLog()
        {
            lock (_pending) CloseLogFile();
        }

        void CloseLogFile()
        {
            // logging is optional: nothing here may stop a car change or the shutdown
            try { _logFile?.Dispose(); } catch { }
            _logFile = null;
            if (_logPath != null && !_logUsed) try { File.Delete(_logPath); } catch { }
            _logPath = null;
        }

        /// <summary>Updates found while the log was not open yet (at startup) wait here.</summary>
        readonly List<CarFileUpdate.Result> _startupUpdates = new();

        void ReportUpdate(CarFileUpdate.Result r) => Log(r.Outcome switch
        {
            CarFileUpdate.Outcome.Merged => Loc.I.Format("CarFileMerged", r.FromVersion, r.ToVersion, r.CarriedOver, r.Backup ?? ""),
            CarFileUpdate.Outcome.Repaired => Loc.I.Format("CarFileRepaired", r.Backup ?? ""),
            _ => Loc.I.Format("CarFileUpdated", r.FromVersion, r.ToVersion, r.Backup ?? "")
        });

        void Flush()
        {
            if (_pending.IsEmpty) return;
            var batch = new List<string>();
            while (_pending.TryDequeue(out var line)) batch.Add(line);
            LogLines?.Invoke(batch);
        }

        public void ReloadCatalog() => Catalog = Catalog.Load(CatalogPath);

        /// <summary>
        /// Switches to a vehicle (with its car file), to the demo car built from a car file, or to a
        /// car file without a vehicle; remembered for the next start.
        /// </summary>
        public void UseCatalog(string path, bool demo = false, Vehicle? vehicle = null)
        {
            path = AppPaths.WorkingCopy(path, ReportUpdate);
            var loaded = Catalog.Load(path);        // throws before anything changes if the file is bad
            if (demo && loaded.Platform.Demo == null) throw new InvalidOperationException(Loc.I.Format("NoDemoCar", loaded.Name));
            if (demo) vehicle = null;
            string key = CarKey(path, demo, vehicle);
            bool otherCar = !string.Equals(Results.Car, key, StringComparison.OrdinalIgnoreCase);
            Results.Car = key;                      // before the change is announced: listeners read results of the new car
            if (otherCar)
            {
                OpenLog();
                Vin = null;                         // the VIN of the car measured until now
                VinAsked = false;
            }
            IsDemoCar = demo;
            Vehicle = vehicle;
            Catalog = loaded;
            // announced even for the same path: the demo car and the real car share a file, and an import may replace a file with another car of the same name
            if (!string.Equals(CatalogPath, path, StringComparison.OrdinalIgnoreCase)) CatalogPath = path;
            else Raise(nameof(CatalogPath));
            Raise(nameof(CarTitle));
            Settings.Car = CarFiles.NameOf(path);
            Settings.DemoCar = demo;
            Settings.Vehicle = vehicle?.Name;
            Settings.Save();
        }
    }
}
