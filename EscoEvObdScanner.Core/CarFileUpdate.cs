using System.Text.Json;
using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Keeps a user's working copy of a car file up to date with the one
    /// shipped with the app. The working copy is what scan results are merged
    /// into, so a newer shipped file must not simply overwrite it: a working
    /// copy without merged findings is replaced, one with merged findings
    /// becomes the shipped file with those findings carried over. Either way the
    /// working copy is saved to the backup folder first, so hand edits are kept
    /// too. A working copy that cannot be read is replaced the same way.
    /// Car files carry a "version" number that goes up with every change.
    /// </summary>
    public static class CarFileUpdate
    {
        public enum Outcome { UpToDate, Created, Replaced, Merged, Repaired }

        public sealed record Result(Outcome Outcome, int FromVersion, int ToVersion, string? Backup, int CarriedOver);

        static readonly JsonDocumentOptions DocOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static int VersionOf(JsonObject root) =>
            root["version"] is JsonValue v && v.TryGetValue(out int n) ? n : 0;

        /// <summary>
        /// Brings <paramref name="working"/> up to <paramref name="shipped"/>.
        /// Never goes back to an older version, so a copy newer than the shipped
        /// file (a car file from elsewhere, a later build) is left alone, and
        /// never replaces a car file the user imported under a shipped name.
        /// </summary>
        public static Result Sync(string shipped, string working, string backupDir)
        {
            var newRoot = Load(shipped);
            int newVersion = VersionOf(newRoot);
            if (!File.Exists(working))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(working))!);
                File.Copy(shipped, working);
                return new Result(Outcome.Created, 0, newVersion, null, 0);
            }

            JsonObject oldRoot;
            try { oldRoot = Load(working); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                // not readable (cut short, edited by hand): the shipped file takes its place, the broken one is kept
                string broken = Backup(working, backupDir, "broken");
                File.Copy(shipped, working, overwrite: true);
                return new Result(Outcome.Repaired, 0, newVersion, broken, 0);
            }
            int oldVersion = VersionOf(oldRoot);
            // a car file the user imported under a shipped name is theirs: never replaced
            bool imported = oldRoot["imported"] is JsonValue im && im.TryGetValue(out bool i) && i;
            if (oldVersion >= newVersion || imported) return new Result(Outcome.UpToDate, oldVersion, oldVersion, null, 0);

            string backup = Backup(working, backupDir, $"v{oldVersion}");
            if (oldRoot["user_changes"] is not JsonValue uc || !uc.TryGetValue(out bool changed) || !changed)
            {
                File.Copy(shipped, working, overwrite: true);
                return new Result(Outcome.Replaced, oldVersion, newVersion, backup, 0);
            }

            // the user's findings on top of the shipped file
            int carried = CarryOver(oldRoot, newRoot);
            CatalogMerge.SortDiscovered(newRoot);
            CatalogMerge.OrderFields(newRoot);
            newRoot["user_changes"] = true;
            CatalogMerge.Write(newRoot, working);
            return new Result(Outcome.Merged, oldVersion, newVersion, backup, carried);
        }

        /// <summary>
        /// Puts the shipped file in place of a working copy that does not load
        /// as a car file, keeping the broken copy in the backup folder.
        /// </summary>
        public static Result Repair(string shipped, string working, string backupDir)
        {
            string broken = Backup(working, backupDir, "broken");
            File.Copy(shipped, working, overwrite: true);
            return new Result(Outcome.Repaired, 0, VersionOf(Load(shipped)), broken, 0);
        }

        /// <summary>Copies the working copy to the backup folder as name-tag-time.json and returns that path.</summary>
        static string Backup(string working, string backupDir, string tag)
        {
            Directory.CreateDirectory(backupDir);
            // the time keeps each backup: a second repair must not overwrite the first broken copy
            string backup = Path.Combine(backupDir,
                $"{Path.GetFileNameWithoutExtension(working)}-{tag}-{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.json");
            File.Copy(working, backup, overwrite: true);
            return backup;
        }

        static JsonObject Load(string path) =>
            JsonNode.Parse(File.ReadAllText(path), null, DocOptions)?.AsObject()
            ?? throw new InvalidDataException($"{path} is empty");

        static string? Text(JsonObject? o, string name) =>
            o?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        static IEnumerable<JsonObject> Entries(JsonObject ecu) =>
            (ecu["dids"] as JsonArray ?? new JsonArray()).Concat(ecu["discovered"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

        /// <summary>An ECU the user's ECU search added: marked "found_by_search", or with notes starting "Found by the ECU search" as merge writes them.</summary>
        static bool FoundBySearch(JsonObject ecu) =>
            ecu["found_by_search"] is JsonValue v && v.TryGetValue(out bool b) && b
            || Text(ecu, "notes")?.StartsWith("Found by the ECU search", StringComparison.Ordinal) == true;

        /// <summary>
        /// What merge can have added to the working copy goes into the shipped
        /// file: discovered identifiers the shipped file does not have (into the
        /// ECU's discovered list) and samples (added to the shipped file's entry
        /// when it has the identifier). Catalogued identifiers of the working copy
        /// are not carried, so one the shipped file dropped stays dropped. An ECU
        /// the shipped file does not have is carried whole if the user's ECU search added it; an ECU is matched by its
        /// id, else by its request and reply address (an ECU the shipped file renamed). Values of identifying
        /// DIDs are never carried. Returns how many ECUs, identifiers and samples were added.
        /// </summary>
        static int CarryOver(JsonObject oldRoot, JsonObject newRoot)
        {
            if (newRoot["ecus"] is not JsonArray newEcus) newRoot["ecus"] = newEcus = new JsonArray();
            var identifying = IdentifyingDids(newRoot);
            int carried = 0;
            foreach (var oldEcu in (oldRoot["ecus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (Text(oldEcu, "id") is not string id) continue;
                var newEcu = newEcus.OfType<JsonObject>().FirstOrDefault(e => string.Equals(Text(e, "id"), id, StringComparison.OrdinalIgnoreCase))
                             ?? newEcus.OfType<JsonObject>().FirstOrDefault(e => SameAddress(e, oldEcu));
                if (newEcu == null)
                {
                    // only an ECU the user's ECU search added; one an earlier shipped file had and this one dropped stays dropped
                    if (!FoundBySearch(oldEcu)) continue;
                    var copy = (JsonObject)oldEcu.DeepClone();
                    foreach (var e in Entries(copy))
                        if (identifying.Contains(Catalog.ParseHexOrDefault(Text(e, "did") ?? ""))) e["samples"] = new JsonArray();
                    newEcus.Add(copy);
                    carried++;
                    continue;
                }
                var known = Entries(newEcu)
                    .Select(e => (Did: Catalog.ParseHexOrDefault(Text(e, "did") ?? ""), Entry: e))
                    .Where(x => x.Did >= 0)
                    .GroupBy(x => x.Did).ToDictionary(g => g.Key, g => g.First().Entry);
                foreach (var oldEntry in Entries(oldEcu))
                {
                    int did = Catalog.ParseHexOrDefault(Text(oldEntry, "did") ?? "");
                    if (did < 0) continue;
                    bool discovered = (oldEcu["discovered"] as JsonArray)?.Contains(oldEntry) == true;
                    if (!known.TryGetValue(did, out var newEntry))
                    {
                        if (!discovered) continue;          // catalogued in the working copy only: the shipped file decides
                        var copy = (JsonObject)oldEntry.DeepClone();
                        if (identifying.Contains(did)) copy["samples"] = new JsonArray();
                        if (newEcu["discovered"] is not JsonArray list) newEcu["discovered"] = list = new JsonArray();
                        list.Add(copy);
                        known[did] = copy;
                        carried++;
                    }
                    else if (!identifying.Contains(did) && oldEntry["samples"] is JsonArray oldSamples && oldSamples.Count > 0)
                    {
                        // one sample per distinct value, as merge keeps them
                        if (newEntry["samples"] is not JsonArray samples) newEntry["samples"] = samples = new JsonArray();
                        var have = samples.OfType<JsonObject>().Select(x => Text(x, "hex")).ToHashSet();
                        int added = 0;
                        foreach (var x in oldSamples.OfType<JsonObject>())
                            if (have.Add(Text(x, "hex"))) { samples.Add(x.DeepClone()); added++; }
                        carried += added;
                        while (samples.Count > CatalogMerge.MaxSamples) samples.RemoveAt(1);
                        // as merge: an entry the user saw with a value of its own is not one that seldom changes,
                        // nor one that only refused, and its length is known
                        if (added > 0 && samples.Count > 1) newEntry.Remove("changes");
                        if (added > 0)
                        {
                            if (newEntry["kind"] is not null) newEntry["kind"] = "positive";
                            if (newEntry["bytes"] is null && samples.OfType<JsonObject>().Select(x => Text(x, "hex")).FirstOrDefault(h => h is { Length: > 0 }) is string hex)
                                newEntry["bytes"] = hex.Length / 2;
                        }
                    }
                }
            }
            return carried;
        }

        static bool SameAddress(JsonObject a, JsonObject b)
        {
            int request = Catalog.ParseHexOrDefault(Text(a, "request") ?? ""), response = Catalog.ParseHexOrDefault(Text(a, "response") ?? "");
            return request >= 0 && response >= 0
                   && request == Catalog.ParseHexOrDefault(Text(b, "request") ?? "")
                   && response == Catalog.ParseHexOrDefault(Text(b, "response") ?? "");
        }

        /// <summary>The identifiers whose values are never kept: the ISO ones and the car file's own list.</summary>
        static HashSet<int> IdentifyingDids(JsonObject root)
        {
            var set = new HashSet<int> { 0xF18C, 0xF190 };
            if (root["platform"]?["identifying"] is JsonArray list)
                foreach (var v in list.OfType<JsonValue>())
                    if (v.TryGetValue(out string? s) && Catalog.ParseHexOrDefault(s) is int d and >= 0) set.Add(d);
            return set;
        }
    }
}
