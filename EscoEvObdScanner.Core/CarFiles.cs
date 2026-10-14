namespace EscoEvObdScanner
{
    /// <summary>
    /// Car files: one JSON per platform in a "cars" folder, named after it
    /// (stellantis-ecmp.json). The program knows no car itself; it lists what
    /// the folders hold.
    /// </summary>
    public static class CarFiles
    {
        public const string FolderName = "cars";

        /// <summary>The name the simulated car's results for a car file are kept under, apart from the real car's.</summary>
        public static string DemoName(string carName) => carName + "-demo";

        /// <summary>Car files in the given cars folders, by file name; the first folder wins a clash.</summary>
        public static List<string> In(params string[] folders) =>
            folders.Where(Directory.Exists)
                .SelectMany(f => Directory.GetFiles(f, "*.json"))
                .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// A car by name ("stellantis-ecmp", with or without .json) in the given
        /// folders, or a path to a file anywhere. Null if there is no such file.
        /// </summary>
        public static string? Find(string nameOrPath, params string[] folders)
        {
            if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
            string file = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : nameOrPath + ".json";
            return folders.Select(f => Path.Combine(f, file)).FirstOrDefault(File.Exists);
        }

        /// <summary>The name a car is chosen by: the file name without .json.</summary>
        public static string NameOf(string path) => Path.GetFileNameWithoutExtension(path);
    }
}
