using System.Text.Json;

namespace EscoEvObdScanner.AppLogic.Services
{
    /// <summary>When a car was taken into use, and its odometer then.</summary>
    public sealed class InUseEntry
    {
        public DateTime? Since { get; set; }
        public double? Km { get; set; }
    }

    /// <summary>
    /// Remembered between runs. A missing file means defaults; a broken file (cut short, edited by
    /// hand) or one that could not be read (locked, no access) also means defaults, and is copied
    /// aside before the first save over it.
    /// </summary>
    public sealed class Settings
    {
        public string Language { get; set; } = "";      // empty: the system language on first start
        public string? Port { get; set; }
        public int Baud { get; set; } = 115200;
        public string? Car { get; set; }          // car file name without .json
        public bool DemoCar { get; set; }         // the simulated car built from that file
        public string? Vehicle { get; set; }      // the chosen vehicle's name (see Vehicles)
        public bool ShowTraffic { get; set; }
        public double? PriceCents { get; set; }   // electricity, cents per kWh; null: no cost shown
        public double ChargeLossPercent { get; set; } = 10;   // drawn from the grid on top of what reaches the battery
        // taken into use and the odometer then, by car file for those used without a vehicle (a vehicle keeps its own)
        public Dictionary<string, InUseEntry> InUse { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutoStartLive { get; set; }    // the live view starts by itself once connected

        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        /// <summary>The file could not be read: it is kept aside before anything is saved over it.</summary>
        bool _unread;

        public static Settings Load()
        {
            try
            {
                var s = File.Exists(AppPaths.SettingsFile)
                    ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new Settings()
                    : new Settings();
                // the dictionary read from the file compares case-sensitively: car file names do not (of two spellings the first wins)
                var inUse = new Dictionary<string, InUseEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var (car, entry) in s.InUse ?? new()) if (entry != null) inUse.TryAdd(car, entry);
                s.InUse = inUse;
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Settings { _unread = true };
            }
            catch
            {
                // a broken file (cut short, edited by hand): kept aside by the first save, not overwritten
                return new Settings { _unread = true };
            }
        }

        public void Save()
        {
            try
            {
                // written aside and swapped in, so a power cut mid-write cannot leave a cut-off file
                Directory.CreateDirectory(AppPaths.ConfigDir);
                // these settings lack what the unread file holds: keep that file first
                if (_unread && File.Exists(AppPaths.SettingsFile))
                    File.Copy(AppPaths.SettingsFile, Path.ChangeExtension(AppPaths.SettingsFile, $".unread-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: false);
                _unread = false;
                string temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, Opts));
                File.Move(temp, AppPaths.SettingsFile, overwrite: true);
            }
            catch
            {
                // settings are a convenience; failing to save them must not stop the app
            }
        }
    }
}
