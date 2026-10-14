using System.Text.Json;

namespace EscoEvObdScanner.AppLogic.Services
{
    /// <summary>
    /// One car the user measures, by a name of their own ("Own Mokka-e", a registration number).
    /// Several vehicles can share a car file: the car file describes the platform, the vehicle
    /// has its own results folder and drive history.
    /// </summary>
    public sealed class Vehicle
    {
        public string Name { get; set; } = "";

        /// <summary>The car file's name without .json.</summary>
        public string Car { get; set; } = "";

        /// <summary>
        /// A hash of the battery ECU's serial number, taken at the first connection: a later
        /// connection to another car is noticed. The serial number itself is not kept.
        /// </summary>
        public string? SerialHash { get; set; }

        /// <summary>The VIN the car gave, and when it was first read: a report made later still names the car.</summary>
        public string? Vin { get; set; }
        public DateTime? VinRead { get; set; }

        /// <summary>The car was asked for its VIN and did not give it (the e-CMP does not over the OBD port).</summary>
        public bool VinNotGiven { get; set; }

        /// <summary>The battery management's date of manufacture and versions as last read: a report made later from a recording has them too.</summary>
        public DateTime? BatteryMade { get; set; }
        public string? BmsSoftware { get; set; }
        public string? BmsHardware { get; set; }
        public DateTime? BatteryInfoRead { get; set; }

        /// <summary>When the owner took the car into use, and its odometer then: the yearly electricity figures count from here.</summary>
        public DateTime? InUseSince { get; set; }
        public double? InUseKm { get; set; }
    }

    /// <summary>The user's vehicles, kept in vehicles.json next to the settings. A missing or broken file means none; a broken one is moved aside as vehicles.bad-<time>.json, one that could not be read is copied aside before the first save.</summary>
    public static class Vehicles
    {
        static string FilePath => Path.Combine(AppPaths.ConfigDir, "vehicles.json");
        static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        /// <summary>The file could not be read (locked, no access): it is kept aside before anything is saved over it.</summary>
        static bool _unread;

        public static List<Vehicle> Load()
        {
            _unread = false;
            try
            {
                return File.Exists(FilePath)
                    ? (JsonSerializer.Deserialize<List<Vehicle?>>(File.ReadAllText(FilePath)) ?? new())
                        .Where(v => v != null && !string.IsNullOrWhiteSpace(v.Name) && !string.IsNullOrWhiteSpace(v.Car)).Select(v => v!).ToList()
                    : new();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // kept for the owner to look at instead of overwritten by the next save
                try { File.Move(FilePath, Path.ChangeExtension(FilePath, $".bad-{DateTime.Now:yyyyMMddHHmmss}.json")); }
                catch (Exception move) when (move is IOException or UnauthorizedAccessException) { _unread = true; }    // then the first save keeps a copy
                return new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _unread = true;
                return new();
            }
        }

        public static void Save(IEnumerable<Vehicle> vehicles)
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            // the list in memory lacks the vehicles of a file that could not be read: keep that file first
            if (_unread && File.Exists(FilePath))
            {
                File.Copy(FilePath, Path.ChangeExtension(FilePath, $".unread-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: false);
                _unread = false;
            }
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(vehicles.ToList(), Opts));
            File.Move(temp, FilePath, overwrite: true);
        }

        /// <summary>
        /// The vehicle's folder name for results and drive history: the name with characters a path cannot
        /// hold replaced, and an underscore before a name Windows reserves for a device (CON, NUL, COM4 ...).
        /// </summary>
        public static string FolderName(string name)
        {
            var chars = name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
            string folder = new string(chars).TrimEnd('.', ' ');
            if (folder.Length == 0) return "_";
            // names Windows reserves for devices (CON, NUL, COM4 ...) cannot be folders
            string stem = folder.Split('.')[0];
            return System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? "_" + folder : folder;
        }

        /// <summary>
        /// Why a new vehicle cannot have this name, as a UI text key with its argument; null when it can.
        /// A car file's own name is taken: a car file used without a vehicle keeps its results there.
        /// </summary>
        public static (string Key, string Arg)? Problem(string name, IEnumerable<Vehicle> existing, IEnumerable<string> carFiles)
        {
            if (string.IsNullOrWhiteSpace(name)) return ("VehicleNameEmpty", "");
            string folder = FolderName(name);
            // the demo cars' results are in <car>-demo folders
            if (folder.EndsWith("-demo", StringComparison.OrdinalIgnoreCase)) return ("VehicleNameDemo", name.Trim());
            if (carFiles.Any(c => string.Equals(c, folder, StringComparison.OrdinalIgnoreCase))) return ("VehicleNameCarFile", name.Trim());
            if (existing.Any(v => string.Equals(FolderName(v.Name), folder, StringComparison.OrdinalIgnoreCase))) return ("VehicleNameTaken", name.Trim());
            return null;
        }
    }
}
