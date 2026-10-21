using System.Collections.ObjectModel;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>
    /// One car in the list: a vehicle with its car file, or the demo car simulated from a car file.
    /// Unavailable: a vehicle whose car file is missing, listed so that it can be removed.
    /// </summary>
    public sealed record CarChoice(string Path, string Title, string? Cars, bool Demo = false, Vehicle? Vehicle = null, bool Unavailable = false)
    {
        public override string ToString() => Title;
    }

    public sealed class ConnectionViewModel : ObservableObject
    {
        const int MaxLogLines = 2000;

        readonly AppState _state;
        PortInfo? _selectedPort;
        CarChoice? _selectedCar;
        int _baud;

        public ConnectionViewModel(AppState state)
        {
            _state = state;
            _baud = state.Settings.Baud;

            RefreshPorts = new RelayCommand(LoadPorts);
            // the demo car needs no adapter
            // a vehicle whose car file is missing is listed only to be removed: nothing to connect as
            Connect = new AsyncCommand(ConnectAsync, () => (_state.IsDemoCar || SelectedPort != null) && !Adapter.IsBusy && !Adapter.IsConnected
                                                         && _selectedCar is not { Unavailable: true });
            Disconnect = new AsyncCommand(async () =>
            {
                await Adapter.DisconnectAsync();
                _state.Log(Loc.I["Disconnected"]);
            }, () => Adapter.IsConnected);
            // like the car list: a different car file only while disconnected
            BrowseCatalog = new RelayCommand(Browse, () => CanChangeCar);
            ResetCatalog = new RelayCommand(Reset, () => CanChangeCar);
            AddVehicle = new RelayCommand(() => ShowAddVehicle(null), () => CanChangeCar);
            RemoveVehicle = new RelayCommand(Remove, () => CanChangeCar && _selectedCar?.Vehicle != null);
            OpenDataFolder = new RelayCommand(() => OpenFolder(AppPaths.DataDir));
            ClearLog = new RelayCommand(() => LogLines.Clear());

            state.LogLines += batch =>
            {
                // drop the oldest first, so the list never grows past the limit; a tenth at a time,
                // so that a busy log does not remove (and redraw) one line for every line added
                int overflow = LogLines.Count + batch.Count - MaxLogLines;
                if (overflow > 0) overflow = Math.Min(LogLines.Count, overflow + MaxLogLines / 10);
                for (int i = 0; i < overflow; i++) LogLines.RemoveAt(0);
                foreach (var line in batch.Skip(Math.Max(0, batch.Count - MaxLogLines))) LogLines.Add(line);
            };

            // the car fixes the protocol the adapter was set up with: change it only while disconnected
            Adapter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AdapterService.IsConnected) or nameof(AdapterService.IsBusy)) Raise(nameof(CanChangeCar));
            };

            // the list shows the car in use, however it was chosen
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppState.CatalogPath)) ShowCurrentCar();
                if (e.PropertyName == nameof(AppState.IsDemoCar)) Raise(nameof(NeedsAdapter));
            };

            // the demo car's name in the list is a UI text
            Loc.I.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Loc.Language)) LoadCars(); };

            AppPaths.BleAdapters(out string? bleProblem);
            if (bleProblem != null) _state.Log(Loc.I.Format("BleConfigBroken", bleProblem));

            LoadPorts();
            LoadCars();
        }

        public ObservableCollection<CarChoice> Cars { get; } = new();

        public CarChoice? SelectedCar
        {
            get => _selectedCar;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedCar)) return;
                if (!CanChangeCar) { Raise(nameof(SelectedCar)); return; }
                if (value.Unavailable)
                {
                    // nothing to use: chosen only to be removed
                    _selectedCar = value;
                    Raise(nameof(SelectedCar));
                    Raise(nameof(CarCovers));
                    return;
                }
                try
                {
                    _state.UseCatalog(value.Path, value.Demo, value.Vehicle);
                    _selectedCar = value;
                    _state.Log(Loc.I.Format("CarChosen", value.Title));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AsyncCommand.Report(ex);    // a setter's exception would otherwise vanish in the binding engine
                }
                Raise(nameof(SelectedCar));
                Raise(nameof(CarCovers));
            }
        }

        public string? CarCovers => _selectedCar?.Cars;

        /// <summary>Port and speed matter only for a real car.</summary>
        public bool NeedsAdapter => !_state.IsDemoCar;

        public bool CanChangeCar => !Adapter.IsConnected && !Adapter.IsBusy;

        public AdapterService Adapter => _state.Adapter;
        public AppState State => _state;

        public ObservableCollection<PortInfo> Ports { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();
        public int[] BaudRates { get; } = { 9600, 38400, 57600, 115200, 230400, 500000 };

        public PortInfo? SelectedPort { get => _selectedPort; set => Set(ref _selectedPort, value); }

        public int Baud
        {
            get => _baud;
            set { if (Set(ref _baud, value)) { _state.Settings.Baud = value; _state.Settings.Save(); } }
        }

        public bool ShowTraffic
        {
            get => _state.Settings.ShowTraffic;
            set { _state.Settings.ShowTraffic = value; _state.Settings.Save(); Raise(nameof(ShowTraffic)); }
        }

        public string DataFolder => AppPaths.DataDir;

        public RelayCommand RefreshPorts { get; }
        public AsyncCommand Connect { get; }
        public AsyncCommand Disconnect { get; }
        public RelayCommand BrowseCatalog { get; }
        public RelayCommand ResetCatalog { get; }
        public RelayCommand AddVehicle { get; }
        public RelayCommand RemoveVehicle { get; }
        public RelayCommand OpenDataFolder { get; }
        public RelayCommand ClearLog { get; }

        /// <summary>Every car file that loads, with its name and the cars it covers; a broken working copy through its shipped file.</summary>
        public IReadOnlyList<(string Path, Catalog Catalog)> CarFiles()
        {
            var files = new List<(string, Catalog)>();
            foreach (var path in AppPaths.Cars())
            {
                try { files.Add((path, Catalog.Load(path))); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _state.Log($"{Path.GetFileName(path)}: {ex.Message}");
                    // listed through its shipped file: choosing it repairs the working copy (see CarFileUpdate)
                    if (AppPaths.Shipped(path) is string shipped && !string.Equals(Path.GetFullPath(shipped), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                    {
                        try { files.Add((shipped, Catalog.Load(shipped))); }
                        catch (Exception inner) when (inner is not OutOfMemoryException) { _state.Log($"{Path.GetFileName(shipped)}: {inner.Message}"); }
                    }
                }
            }
            return files;
        }

        void LoadCars()
        {
            Cars.Clear();
            var files = CarFiles();
            // the user's vehicles first, then a simulated car for each car file with recorded data
            foreach (var v in _state.VehicleList)
            {
                var file = files.FirstOrDefault(f => string.Equals(EscoEvObdScanner.CarFiles.NameOf(f.Path), v.Car, StringComparison.OrdinalIgnoreCase));
                if (file.Catalog == null)
                {
                    Cars.Add(new CarChoice("", Loc.I.Format("VehicleUnavailableTitle", v.Name, v.Car), Loc.I["VehicleUnavailableHint"], Vehicle: v, Unavailable: true));
                    continue;
                }
                Cars.Add(new CarChoice(file.Path, Loc.I.Format("VehicleTitle", v.Name, file.Catalog.Name), file.Catalog.Platform.Cars, Vehicle: v));
            }
            foreach (var (path, c) in files.Where(f => f.Catalog.Platform.Demo != null))
                Cars.Add(new CarChoice(path, Loc.I.Format("DemoCarTitle", c.Name), Loc.I["DemoCarCovers"], Demo: true));
            // a car file in use without a vehicle (an import just chosen) stays in the list
            if (!_state.IsDemoCar && _state.Vehicle == null)
                Cars.Add(new CarChoice(_state.CatalogPath, _state.Catalog.Name, _state.Catalog.Platform.Cars));
            ShowCurrentCar();
        }

        void ShowCurrentCar()
        {
            _selectedCar = Cars.FirstOrDefault(c => c.Demo == _state.IsDemoCar && ReferenceEquals(c.Vehicle, _state.Vehicle)
                && string.Equals(EscoEvObdScanner.CarFiles.NameOf(c.Path), EscoEvObdScanner.CarFiles.NameOf(_state.CatalogPath), StringComparison.OrdinalIgnoreCase));
            Raise(nameof(SelectedCar));
            Raise(nameof(CarCovers));
            AppHost.I.RequeryCommands();
        }

        void LoadPorts()
        {
            Ports.Clear();
            foreach (var p in AppHost.I.Ports()) Ports.Add(p);
            // the Bluetooth LE adapter used last time stays choosable before it is heard advertising again
            if (_state.Settings.Port is string saved && new PortInfo(saved, "").IsBluetoothLe && Ports.All(p => p.Name != saved))
                Ports.Add(new PortInfo(saved, Loc.I["BleAdapterSaved"]));

            SelectedPort = Ports.FirstOrDefault(p => p.Name == _state.Settings.Port)
                        ?? Ports.FirstOrDefault(PortInfo.LooksLikeAdapter)
                        ?? Ports.FirstOrDefault();
        }

        /// <summary>
        /// Raised once a connection is made and the car checked; not when it turned out to be another car,
        /// nor when none of the car file's ECUs answered (another car's file, or the car is off).
        /// </summary>
        public event Action? Connected;

        /// <summary>The check found that none of the car file's ECUs answered.</summary>
        bool _noEcuAnswered;

        async Task ConnectAsync()
        {
            _noEcuAnswered = false;
            await ConnectAndCheckAsync();
            if (Adapter.IsConnected && !_noEcuAnswered) Connected?.Invoke();
        }

        async Task ConnectAndCheckAsync()
        {
            _state.Vin = null;
            _state.VinAsked = false;
            if (_state.IsDemoCar)
            {
                await Adapter.ConnectDemoAsync(_state.Catalog);
                _state.Log(Loc.I.Format("ConnectedTo", Adapter.Version ?? ""));
                await ReadVinAsync();
                return;
            }
            var port = SelectedPort ?? throw new InvalidOperationException(Loc.I["ErrNoPort"]);
            _state.Log($"{Loc.I["Connect"]}: {port.Name} @ {Baud}");
            await Adapter.ConnectAsync(port.Name, Baud, _state.Catalog);
            _state.Settings.Port = port.Name;
            _state.Settings.Save();
            _state.Log(Loc.I.Format("ConnectedTo", Adapter.Version ?? ""));
            await ReadVinAsync();
            if (_state.Vehicle is not { } vehicle) return;
            bool? same = await CheckVehicleAsync(vehicle);
            if (same == false)
            {
                // recordings, drive history and reports would all go under the wrong vehicle
                _state.Vin = null;
                _state.VinAsked = false;
                await Adapter.DisconnectAsync();
                _state.Log(Loc.I["VehicleWrongDisconnected"]);
                return;
            }
            if (_state.Vin is string vin) KeepVin(vehicle, vin, verified: same == true);
            else if (_state.VinAsked && _state.Vin == null && vehicle.Vin == null && !vehicle.VinNotGiven)
            {
                vehicle.VinNotGiven = true;
                try { _state.SaveVehicles(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _state.Log(Loc.I.Format("VehicleSaveFailed", ex.Message)); }
            }
            await KeepBatteryInfoAsync(vehicle, verified: same == true);
        }

        /// <summary>
        /// The battery management's date of manufacture and versions go with the vehicle, for reports made
        /// afterwards from recordings, with the date of the latest read from the same car. Without the identity
        /// check to confirm it is the same car, only empty values are filled.
        /// </summary>
        internal async Task KeepBatteryInfoAsync(Vehicle vehicle, bool verified)
        {
            var read = new VehicleState();
            try { await Adapter.RunAsync("BatteryInfoReading", (scanner, _) => LivePoller.ReadTexts(scanner, _state.Catalog, read)); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or PortLostException)) { return; }      // a help for the report, not a condition; a lost port is reported
            bool changed = false, confirmed = false;
            void Keep<T>(T? value, T? kept, Action<T> set)
            {
                if (value == null) return;
                if (verified) confirmed = true;     // read from the same car: the read date moves even if nothing changed
                if (Equals(value, kept) || (!verified && kept != null)) return;
                set(value);
                changed = true;
            }
            Keep(read.BatteryMade, vehicle.BatteryMade, v => vehicle.BatteryMade = v);
            Keep(read.BmsSoftware, vehicle.BmsSoftware, v => vehicle.BmsSoftware = v);
            Keep(read.BmsHardware, vehicle.BmsHardware, v => vehicle.BmsHardware = v);
            if (!changed && !confirmed) return;
            vehicle.BatteryInfoRead = DateTime.Now;
            try { _state.SaveVehicles(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _state.Log(Loc.I.Format("VehicleSaveFailed", ex.Message)); }
        }

        /// <summary>
        /// The VIN goes with the vehicle, for reports made afterwards, unless the car on the adapter is
        /// another one; the read date moves with every read from the same car. Without the identity check to
        /// confirm it is the same car, another VIN does not replace the one kept.
        /// </summary>
        internal void KeepVin(Vehicle vehicle, string vin, bool verified)
        {
            if (vehicle.Vin == vin && vehicle.VinRead != null && !verified) return;
            if (!verified && vehicle.Vin != null && vehicle.Vin != vin)
            {
                _state.Log(Loc.I.Format("VinNotKept", vin, vehicle.Name, vehicle.Vin));
                return;
            }
            (vehicle.Vin, vehicle.VinRead) = (vin, DateTime.Now);
            try { _state.SaveVehicles(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _state.Log(Loc.I.Format("VehicleSaveFailed", ex.Message)); }
        }

        /// <summary>The VIN for the report, which ties the report to the car measured.</summary>
        async Task ReadVinAsync()
        {
            try { _state.Vin = await Adapter.RunAsync("VinReading", (scanner, _) => VehicleIdentity.ReadVin(scanner, _state.Catalog)); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or PortLostException)) { return; }      // a help for the report, not a condition; a lost port is reported
            _state.VinAsked = true;
            _state.Log(_state.Vin is string vin ? Loc.I.Format("VinRead", vin) : Loc.I["VinNotGiven"]);
        }

        /// <summary>
        /// The car on the adapter should be the vehicle chosen: its battery ECU's serial number (as a
        /// hash) is kept at the first connection and compared afterwards. A car that does not tell is
        /// not checked. True when it is the vehicle, false when it is another car, null when the check could not tell.
        /// </summary>
        async Task<bool?> CheckVehicleAsync(Vehicle vehicle)
        {
            string? id;
            bool answered, silent;
            try { (id, answered, silent) = await Adapter.RunAsync("VehicleChecking", (scanner, _) => VehicleIdentity.Check(scanner, _state.Catalog)); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or PortLostException)) { return null; }      // the check is a help, not a condition; a lost port is reported
            if (id == null && !answered)
            {
                // no ECU of the car file answers: most likely the vehicle has another car's file; the live view is not started by itself
                _noEcuAnswered = true;
                var text = Loc.I.Format("VehicleNoAnswer", vehicle.Name, _state.Catalog.Name);
                _state.Log(text);
                AppHost.I.Tell(text, Notice.Warning);
                return null;
            }
            if (id == null) { _state.Log(Loc.I.Format(silent ? "VehicleNotAwake" : "VehicleNoIdentity", vehicle.Name)); return null; }
            var other = _state.VehicleList.FirstOrDefault(v => !ReferenceEquals(v, vehicle) && v.SerialHash == id);
            if (other != null)
            {
                _state.Log(Loc.I.Format("VehicleIsOther", vehicle.Name, other.Name));
                AppHost.I.Tell(Loc.I.Format("VehicleIsOther", vehicle.Name, other.Name), Notice.Warning);
                return false;
            }
            if (vehicle.SerialHash == null)
                KeepIdentity(vehicle, id, "VehicleRecognised");
            else if (vehicle.SerialHash != id)
            {
                // a warning, not an error: a replaced battery ECU changes the identity of the same car
                _state.Log(Loc.I.Format("VehicleMismatch", vehicle.Name));
                if (!AppHost.I.Ask(Loc.I.Format("VehicleMismatchAsk", vehicle.Name), Notice.Warning, defaultNo: true)) return false;
                KeepIdentity(vehicle, id, "VehicleIdentityUpdated");
            }
            return true;
        }

        void KeepIdentity(Vehicle vehicle, string id, string logKey)
        {
            vehicle.SerialHash = id;
            try
            {
                _state.SaveVehicles();
                _state.Log(Loc.I.Format(logKey, vehicle.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _state.Log(Loc.I.Format("VehicleSaveFailed", ex.Message));      // kept for this run; checked again next time
            }
        }

        /// <summary>
        /// Adds a vehicle on a car file and chooses it; returns why it could not, in the UI language,
        /// or null. Also the self-test's way in.
        /// </summary>
        internal string? AddVehicleNamed(string name, string carPath)
        {
            var carNames = AppPaths.Cars().Select(EscoEvObdScanner.CarFiles.NameOf);
            if (Vehicles.Problem(name, _state.VehicleList, carNames) is { } problem) return Loc.I.Format(problem.Key, problem.Arg);
            var vehicle = new Vehicle { Name = name.Trim(), Car = EscoEvObdScanner.CarFiles.NameOf(carPath) };
            // saved first: the list in memory changes only when the file did
            try { Vehicles.Save(_state.VehicleList.Append(vehicle)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Loc.I.Format("VehicleSaveFailed", ex.Message); }
            _state.VehicleList.Add(vehicle);
            try { _state.UseCatalog(carPath, false, vehicle); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { LoadCars(); return ex.Message; }
            _state.Log(Loc.I.Format("VehicleAdded", vehicle.Name));
            LoadCars();
            return null;
        }

        void ShowAddVehicle(string? carPath)
        {
            var files = CarFiles().Select(f => new CarFileChoice(f.Path, f.Catalog.Name, f.Catalog.Platform.Cars)).ToList();
            // no car file chosen in advance unless just imported: a vehicle on another car's file reads nothing right
            AppHost.I.AddVehicle(files, carPath, AddVehicleNamed);
        }

        void Remove()
        {
            if (_selectedCar?.Vehicle is not { } vehicle) return;
            if (!AppHost.I.Ask(Loc.I.Format("VehicleRemoveConfirm", vehicle.Name), Notice.Question)) return;
            // saved first: the list in memory changes only when the file did
            try { Vehicles.Save(_state.VehicleList.Where(v => !ReferenceEquals(v, vehicle))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AsyncCommand.Report(new InvalidOperationException(Loc.I.Format("VehicleSaveFailed", ex.Message)));
                return;
            }
            _state.VehicleList.Remove(vehicle);
            // the car file stays in use, without a vehicle; the vehicle's results stay where they are
            try { if (ReferenceEquals(_state.Vehicle, vehicle)) _state.UseCatalog(_state.CatalogPath); }
            finally { LoadCars(); }
        }

        void Browse()
        {
            if (AppHost.I.PickCarFile(Path.GetDirectoryName(_state.CatalogPath)) is not string picked) return;

            // one of the user's own cars: choose it, it needs no import
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(picked)), Path.GetFullPath(AppPaths.UserCarsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Catalog.Load(picked);
                _state.UseCatalog(picked);
                LoadCars();
                return;
            }

            // a car file from elsewhere becomes one of the user's cars, so it is in the list next time too
            Catalog.Load(picked);         // refuse a broken file before copying it
            string target = Path.Combine(AppPaths.UserCarsDir, Path.GetFileName(picked));
            if (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(picked), StringComparison.OrdinalIgnoreCase)
                && !AppHost.I.Ask(Loc.I.Format("ImportReplaceConfirm", Path.GetFileName(target)), Notice.Warning)) return;
            string imported = AppPaths.Import(picked);
            _state.UseCatalog(imported);
            LoadCars();
            ShowAddVehicle(imported);
        }

        void Reset()
        {
            if (!AppHost.I.Ask(Loc.I["ResetCatalogConfirm"], Notice.Warning)) return;
            // the shipped file of this car overwrites its working copy
            if (AppPaths.Shipped(_state.CatalogPath) is not string shipped)
            {
                AppHost.I.Tell(Loc.I["NoShippedCar"], Notice.Information);
                return;
            }
            string working = Path.Combine(AppPaths.UserCarsDir, Path.GetFileName(shipped));
            Directory.CreateDirectory(AppPaths.UserCarsDir);
            File.Copy(shipped, working, overwrite: true);
            // a reset of the demo car's file keeps the demo car chosen
            _state.UseCatalog(working, _state.IsDemoCar && Catalog.Load(working).Platform.Demo != null, _state.Vehicle);
            LoadCars();
        }

        public static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            AppHost.I.Open(path);
        }
    }
}
