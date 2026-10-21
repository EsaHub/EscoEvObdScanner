
namespace EscoEvObdScanner.AppLogic.Services
{
    /// <summary>
    /// Where the app keeps things. Results go to Documents so they are easy to
    /// find and send; car files and settings go to LocalAppData because the
    /// install folder under Program Files is not writable.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>Results: a folder per car, and in it one subfolder per kind (scans, snaps, fault codes, live recordings, reports, logs).</summary>
        public static string DataDir => Results.Root;

        public static string ConfigDir { get; private set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EscoEvObdScanner");

        /// <summary>Keeps a self-test run away from the user's own results and settings.</summary>
        public static void UseIsolatedFolders(string root)
        {
            Results.Root = Path.Combine(root, "data");
            ConfigDir = Path.Combine(root, "config");
        }

        /// <summary>The car files shipped with the app (read-only under Program Files).</summary>
        public static string BundledCarsDir => Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName);

        /// <summary>The user's working copies: what merge writes to, and where imported car files go.</summary>
        public static string UserCarsDir => Path.Combine(ConfigDir, CarFiles.FolderName);

        public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");

        /// <summary>
        /// The Bluetooth LE adapter settings: the user's file in the config folder if there is one, else the
        /// one shipped next to the program, else the defaults. <paramref name="problem"/> tells why a file was not used.
        /// </summary>
        public static BleAdapters BleAdapters(out string? problem) =>
            EscoEvObdScanner.BleAdapters.Load(out problem, Path.Combine(ConfigDir, EscoEvObdScanner.BleAdapters.FileName),
                Path.Combine(AppContext.BaseDirectory, EscoEvObdScanner.BleAdapters.FileName));

        /// <summary>Every car: the user's copies first, then shipped ones the user has no copy of.</summary>
        public static List<string> Cars() => CarFiles.In(UserCarsDir, BundledCarsDir);

        /// <summary>
        /// The working copy of a car by name, else of a car that has a demo car
        /// (so a first start can be tried without an adapter), else of the first
        /// car there is; created from the shipped file on first use.
        /// </summary>
        public static string ResolveCar(string? name, Action<CarFileUpdate.Result>? report = null)
        {
            var cars = Cars();
            string chosen = cars.FirstOrDefault(c => string.Equals(CarFiles.NameOf(c), name, StringComparison.OrdinalIgnoreCase))
                            ?? cars.FirstOrDefault(HasDemo)
                            ?? cars.FirstOrDefault()
                            ?? throw new FileNotFoundException(Infrastructure.Loc.I.Format("ErrNoCarFiles", BundledCarsDir));
            return WorkingCopy(chosen, report);
        }

        static bool HasDemo(string path)
        {
            try { return Catalog.Load(path).Platform.Demo != null; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }

        /// <summary>Where a working copy is kept before an update replaces it.</summary>
        public static string BackupDir => Path.Combine(UserCarsDir, "backup");

        /// <summary>
        /// The working copy of a car file, shipped or the user's own: created
        /// from the shipped file on first use and brought up to a newer shipped
        /// version (see <see cref="CarFileUpdate"/>). Scan results are merged
        /// into it, so the app never works on the read-only shipped file.
        /// <paramref name="report"/> hears what an update did.
        /// </summary>
        public static string WorkingCopy(string path, Action<CarFileUpdate.Result>? report = null)
        {
            string target = Path.Combine(UserCarsDir, Path.GetFileName(path));
            // a car of the user's own, with nothing shipped under its name: nothing to keep up with
            if (Shipped(target) is not string shipped) return path;
            var result = CarFileUpdate.Sync(shipped, target, BackupDir);
            if (result.Outcome is CarFileUpdate.Outcome.Replaced or CarFileUpdate.Outcome.Merged or CarFileUpdate.Outcome.Repaired) report?.Invoke(result);
            // valid JSON can still be no car file (a hand edit with a wrong type): the shipped file takes its place
            try { Catalog.Load(target); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                report?.Invoke(CarFileUpdate.Repair(shipped, target, BackupDir));
            }
            return target;
        }

        /// <summary>The shipped file a working copy came from, if there is one.</summary>
        public static string? Shipped(string path)
        {
            string shipped = Path.Combine(BundledCarsDir, Path.GetFileName(path));
            return File.Exists(shipped) ? shipped : null;
        }

        /// <summary>
        /// Copies a car file from anywhere into the user's cars folder, marked
        /// as imported: an update of a shipped file with the same name never
        /// replaces it.
        /// </summary>
        public static string Import(string file)
        {
            Directory.CreateDirectory(UserCarsDir);
            string target = Path.Combine(UserCarsDir, Path.GetFileName(file));
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file),
                null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
            root["imported"] = true;
            CatalogMerge.Write(root, target);
            return target;
        }
    }
}
