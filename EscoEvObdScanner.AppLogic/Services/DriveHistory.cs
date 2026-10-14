using System.Text.Json;

namespace EscoEvObdScanner.AppLogic.Services
{
    /// <summary>
    /// Drives measured while the live view was running, kept for the range
    /// estimate. A missing or broken file just means no history yet.
    /// </summary>
    public sealed class DriveHistory
    {
        const int Keep = 1000;

        readonly List<Drive> _drives;
        readonly string _file;

        /// <summary>The file could not be read (locked, no access): it is kept aside before the first save over it.</summary>
        bool _unread;

        DriveHistory(List<Drive> drives, string file, bool unread = false)
        {
            _drives = drives;
            _file = file;
            _unread = unread;
        }

        public IReadOnlyList<Drive> Drives => _drives;

        /// <summary>One car's drives: each car has its own consumption.</summary>
        public static DriveHistory Load(string car)
        {
            string FilePath = Path.Combine(AppPaths.ConfigDir, $"drives-{car}.json");
            try
            {
                if (File.Exists(FilePath))
                    return new DriveHistory(JsonSerializer.Deserialize<List<Drive>>(File.ReadAllText(FilePath)) ?? new(), FilePath);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Keep the unreadable file for the owner to look at instead of
                // overwriting it with the next drive; if it cannot be moved now
                // (locked), it is copied aside before the first save over it.
                try { File.Move(FilePath, Path.ChangeExtension(FilePath, $".bad-{DateTime.Now:yyyyMMddHHmmss}.json")); }
                catch (Exception move) when (move is IOException or UnauthorizedAccessException)
                {
                    return new DriveHistory(new(), FilePath, unread: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new DriveHistory(new(), FilePath, unread: true);
            }
            return new DriveHistory(new(), FilePath);
        }

        /// <summary>Adds the drive, or replaces the one with the same start as it grows.</summary>
        public void Save(Drive drive)
        {
            int i = _drives.FindIndex(d => d.Start == drive.Start);
            if (i >= 0) _drives[i] = drive; else _drives.Add(drive);
            if (_drives.Count > Keep) _drives.RemoveRange(0, _drives.Count - Keep);

            try
            {
                // written aside and swapped in, so a power cut mid-write cannot truncate the history
                Directory.CreateDirectory(AppPaths.ConfigDir);
                if (_unread && File.Exists(_file))
                {
                    File.Copy(_file, Path.ChangeExtension(_file, $".unread-{DateTime.Now:yyyyMMddHHmmss}.json"), overwrite: false);
                    _unread = false;
                }
                string temp = _file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_drives));
                File.Move(temp, _file, overwrite: true);
            }
            catch
            {
                // losing one update costs a little accuracy, nothing more
            }
        }
    }
}
