namespace EscoEvObdScanner
{
    /// <summary>
    /// Where the app and the command line keep what they record: a folder per
    /// car, and in it one subfolder per kind, so two cars' snaps or fault codes
    /// never mix. The app's folder is the vehicle's name, the command line's the
    /// car file's; they meet only when the app uses a car file without a vehicle.
    /// </summary>
    public static class Results
    {
        public const string Scans = "scans";
        public const string Snaps = "snaps";
        public const string Dtc = "dtc";
        public const string Live = "live";
        public const string Watch = "watch";
        public const string Scaling = "scaling";
        public const string Logs = "logs";
        public const string Reports = "reports";

        /// <summary>Documents\EscoEvObdScanner unless set otherwise (the self-test, or --out on the command line).</summary>
        public static string Root { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EscoEvObdScanner");

        /// <summary>The folder of the car whose results these are (a vehicle's name, a car file's name, or "&lt;car&gt;-demo"); null keeps them directly under the root.</summary>
        public static string? Car { get; set; }

        public static string Folder(string kind) => Car is { Length: > 0 } car ? Path.Combine(Root, car, kind) : Path.Combine(Root, kind);

        /// <summary>
        /// A new timestamped file, e.g. scans\scan_bms_D800-D8FF_20260926_101500.jsonl.
        /// Two runs within the same second (stop, start) get _2, _3 rather than overwriting.
        /// </summary>
        public static string NewFile(string kind, string stem, string extension = ".jsonl")
        {
            string folder = Folder(kind);
            Directory.CreateDirectory(folder);
            string start = Path.Combine(folder, $"{stem}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}");

            string path = start + extension;
            for (int n = 2; File.Exists(path) || File.Exists(path + ".part"); n++) path = $"{start}_{n}{extension}";
            return path;
        }

        /// <summary>Files of one kind, newest first by name (the timestamp).</summary>
        public static IEnumerable<string> Files(string kind, string pattern)
        {
            string folder = Folder(kind);
            try
            {
                return Directory.Exists(folder)
                    ? Directory.GetFiles(folder, pattern).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).ToList()
                    : Enumerable.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Command-line file arguments: a path as given if it exists, otherwise the
        /// same name in the given result folders. Wildcards (scan_*.jsonl) are
        /// expanded here, since PowerShell and cmd pass them on as they are.
        /// </summary>
        public static List<string> Resolve(IEnumerable<string> args, params string[] kinds)
        {
            var found = new List<string>();
            foreach (var arg in args)
            {
                if (arg.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    found.Add(File.Exists(arg) ? arg
                        : kinds.Select(k => Path.Combine(Folder(k), arg)).FirstOrDefault(File.Exists) ?? arg);
                    continue;
                }

                string? dir = Path.GetDirectoryName(arg);
                string pattern = Path.GetFileName(arg);
                var folders = string.IsNullOrEmpty(dir) ? new[] { "." }.Concat(kinds.Select(Folder)) : new[] { dir };
                found.AddRange(folders.Where(Directory.Exists)
                    .SelectMany(f => Directory.GetFiles(f, pattern))
                    .OrderBy(f => f, StringComparer.Ordinal));
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
