using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Folds run files into each ECU's "discovered" array, and adds ECUs an
    /// ECU search found.
    ///
    /// Deliberately edits the JSON document tree rather than round-tripping
    /// through the typed model, so every field the model does not cover
    /// survives untouched.
    /// </summary>
    public static class CatalogMerge
    {
        /// <summary>
        /// Added: new discovered entries. Updated: entries that got a new sample.
        /// Skipped: lines for identifiers already curated, or carrying nothing new to keep
        /// (an identifying value, a refusal for a known entry). Lines that cannot be used at
        /// all (malformed, unknown ECU, failed read) are not counted.
        /// </summary>
        /// <param name="Cleared">Stored values of identifying identifiers were removed, so the file was written.</param>
        public sealed record Result(int Added, int Updated, int Skipped, bool Cleared = false);

        public const int Schema = 2;

        /// <summary>Samples kept per discovered identifier.</summary>
        public const int MaxSamples = 12;


        static string? Text(JsonObject? o, string name) =>
            o?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        static JsonObject ReadRoot(string path)
        {
            // Same leniency as Catalog.Load (comments, trailing commas; keys as written),
            // or a catalogue would load fine everywhere else and fail only here.
            var docOpts = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            return JsonNode.Parse(File.ReadAllText(path), null, docOpts)?.AsObject()
                   ?? throw new InvalidDataException($"{path} is empty");
        }

        /// <summary>
        /// Adds the ECUs an ECU search found that the car file does not have, as
        /// unnamed ECUs with their addresses and empty identifier lists, so they
        /// can be scanned. One whose request or reply address the file already
        /// uses is left out. Returns how many were added.
        /// </summary>
        public static int AddEcus(Catalog catalog, IEnumerable<(int Request, int Response)> found, DateTime when)
        {
            var root = ReadRoot(catalog.Path);
            if (root["ecus"] is not JsonArray ecus) root["ecus"] = ecus = new JsonArray();
            var ids = ecus.OfType<JsonObject>().Select(e => Text(e, "id")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requests = ecus.OfType<JsonObject>().Select(e => Catalog.ParseHexOrDefault(Text(e, "request") ?? "")).ToHashSet();
            var responses = ecus.OfType<JsonObject>().Select(e => Catalog.ParseHexOrDefault(Text(e, "response") ?? "")).ToHashSet();

            int added = 0;
            foreach (var (req, resp) in found)
            {
                // an address the file has, or a reply ID another ECU answers on, is not added
                if (requests.Contains(req) || responses.Contains(resp)) continue;
                requests.Add(req);
                responses.Add(resp);
                string id = $"ecu{req:x3}";
                for (int n = 2; ids.Contains(id); n++) id = $"ecu{req:x3}_{n}";
                ids.Add(id);
                ecus.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = new JsonObject { ["fi"] = $"Tuntematon laite 0x{req:X3}", ["en"] = $"Unknown ECU 0x{req:X3}" },
                    ["request"] = $"0x{req:X3}",
                    ["response"] = $"0x{resp:X3}",
                    ["found_by_search"] = true,
                    ["notes"] = $"Found by the ECU search on {when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.",
                    ["dids"] = new JsonArray(),
                    ["discovered"] = new JsonArray()
                });
                added++;
            }

            if (added == 0) return 0;
            // the copy now holds the user's findings: an update of the shipped file carries the ECU over whole
            root["user_changes"] = true;
            Write(root, catalog.Path);
            return added;
        }

        /// <summary>The source id of a result file by its name: snap_ files are own-snaps, watch_ files own-watch, the rest own-scan.</summary>
        public static string OwnSource(string file) => Path.GetFileName(file).ToLowerInvariant() switch
        {
            var n when n.StartsWith("snap_") => "own-snaps",
            var n when n.StartsWith("watch_") => "own-watch",
            _ => "own-scan"
        };

        public static Result Merge(Catalog catalog, IEnumerable<string> files)
        {
            var root = ReadRoot(catalog.Path);

            static string Key(string ecu, string did) => ecu.ToLowerInvariant() + "|" + did;

            // every ECU object by id, and every discovered entry (first wins if listed twice)
            var ecus = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            var existing = new Dictionary<string, JsonObject>();
            foreach (var ecu in (root["ecus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (Text(ecu, "id") is not string id) continue;
                ecus.TryAdd(id, ecu);
                foreach (var o in (ecu["discovered"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    if (Catalog.ParseHexOrDefault(Text(o, "did") ?? "") is int d and >= 0) existing.TryAdd(Key(id, $"0x{d:X4}"), o);
            }

            int added = 0, updated = 0, skipped = 0;
            bool cleared = false;
            // merge records that an ECU answers these, but never the value (see Catalog.Identifying)
            var identifyingDids = catalog.Identifying;

            foreach (var file in files)
            {
                string source = OwnSource(file);
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JsonObject? rec;
                    try { rec = JsonNode.Parse(line)?.AsObject(); }
                    catch { continue; }
                    if (rec == null) continue;

                    // one odd line (wrong type, bad hex, unknown ECU) is ignored, not fatal
                    string? ecuId = Text(rec, "ecu");
                    int didNum = Catalog.ParseDid(Text(rec, "did"));
                    if (ecuId == null || didNum < 0) continue;
                    if (!ecus.TryGetValue(ecuId, out var ecuObj)) continue;
                    string did = $"0x{didNum:X4}";      // one spelling, so "0xd901" and "0xD901" are one entry

                    // Scans and snaps also log reads that failed. Only a reply proves the
                    // identifier exists: a value, or a refusal that means "exists, not now".
                    string kind = Text(rec, "kind") ?? "";
                    string hex = Text(rec, "hex") ?? "";
                    bool hasValue = kind == "positive" && hex.Length > 0;
                    // "does not exist" (requestOutOfRange) or "not supported" proves nothing
                    bool existsNotNow = kind is "negative:securityAccessDenied" or "negative:conditionsNotCorrect";
                    if (!hasValue && !existsNotNow) continue;

                    // already in the curated list — nothing to discover
                    if (catalog.Find(ecuId, didNum) != null) { skipped++; continue; }

                    // serial numbers and the VIN identify one car and must not end up in a shared file
                    bool identifying = identifyingDids.Contains(didNum);

                    string key = Key(ecuId, did);
                    if (existing.TryGetValue(key, out var entry))
                    {
                        if (identifying)
                        {
                            // listed as identifying after values were stored: drop them
                            if (entry["samples"] is JsonArray { Count: > 0 }) { entry["samples"] = new JsonArray(); cleared = true; }
                            skipped++;
                            continue;
                        }
                        if (!hasValue) { skipped++; continue; }
                        var samples = entry["samples"] as JsonArray ?? new JsonArray();
                        if (!samples.OfType<JsonObject>().Any(s => Text(s, "hex") == hex))
                        {
                            samples.Add(Sample(rec, hex));
                            // what the entry rests on now includes this kind of result (a snap or a watch after a scan)
                            if (entry["source"] is not JsonArray sources) entry["source"] = sources = new JsonArray();
                            if (!sources.Any(x => (string?)x == source)) sources.Add(source);
                            // a watch log of a counter would otherwise add a sample per change;
                            // keep the first one seen and the newest ones
                            while (samples.Count > MaxSamples) samples.RemoveAt(1);
                            entry["samples"] = samples;
                            // a positive reply marks the entry as answering, whatever it was recorded as
                            entry["kind"] = "positive";
                            entry["bytes"] = hex.Length / 2;
                            // a new value: it is not one that seldom changes
                            if (samples.Count > 1) entry.Remove("changes");
                            updated++;
                        }
                        else skipped++;     // a value it already has
                        continue;
                    }

                    var created = new JsonObject
                    {
                        ["did"] = did,
                        ["confidence"] = "unknown",
                        ["source"] = new JsonArray(source),
                        ["kind"] = rec["kind"]?.DeepClone(),
                        ["samples"] = identifying || !hasValue ? new JsonArray() : new JsonArray { Sample(rec, hex) }
                    };
                    if (hasValue) created["bytes"] = hex.Length / 2;
                    if (identifying) created["notes"] = "Identifies this car, so the value is not kept here; read it from the car.";
                    if (ecuObj["discovered"] is not JsonArray list) ecuObj["discovered"] = list = new JsonArray();
                    list.Add(created);
                    existing[key] = created;
                    added++;
                }
            }

            // the copy now holds the user's findings: an update of the shipped file must carry them over
            if (added + updated > 0) root["user_changes"] = true;
            // new entries were added at the end: the file's order is by confidence, then identifier
            if (added > 0) SortDiscovered(root);
            // fields added to entries (bytes, source, notes) take their place in the file's field order
            if (added + updated > 0) OrderFields(root);
            // values of every identifying identifier go, also of one listed as identifying after they were stored
            foreach (var ecu in (root["ecus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var list in new[] { ecu["dids"], ecu["discovered"] }.OfType<JsonArray>())
                    foreach (var entry in list.OfType<JsonObject>())
                        if (identifyingDids.Contains(Catalog.ParseHexOrDefault(Text(entry, "did") ?? "")) && entry["samples"] is JsonArray { Count: > 0 })
                        {
                            entry["samples"] = new JsonArray();
                            cleared = true;
                        }
            // written only when something was added, updated or cleared
            if (added + updated > 0 || cleared) Write(root, catalog.Path);
            return new Result(added, updated, skipped, cleared);
        }

        /// <summary>A sample of a result line: its time when it has one (no null field is written), and the value.</summary>
        static JsonObject Sample(JsonObject rec, string hex)
        {
            var sample = new JsonObject();
            if (Text(rec, "ts") is string ts) sample["ts"] = ts;
            sample["hex"] = hex;
            return sample;
        }

        /// <summary>Each ECU's discovered list in the file's order: by confidence (as confidence_levels lists them), then identifier.</summary>
        internal static void SortDiscovered(JsonObject root)
        {
            var levels = (root["confidence_levels"] as JsonObject)?.Select(p => p.Key).ToList() ?? new List<string>();
            int Rank(JsonObject e) => levels.IndexOf(Text(e, "confidence") ?? "") is int i and >= 0 ? i : levels.Count;
            foreach (var ecu in (root["ecus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (ecu["discovered"] is not JsonArray list) continue;
                // anything that is not an entry stays, after the sorted entries
                var sorted = list.OfType<JsonObject>().OrderBy(Rank).ThenBy(e => Catalog.ParseHexOrDefault(Text(e, "did"))).ToList();
                var rest = list.Where(n => n is not JsonObject).ToList();
                list.Clear();
                foreach (var e in sorted) list.Add(e);
                foreach (var n in rest) list.Add(n);
            }
        }

        /// <summary>The field order of an identifier entry in a car file.</summary>
        static readonly string[] EntryFields = { "did", "name", "unit", "bytes", "encoding", "expression", "confidence", "kind", "changes", "source", "notes", "samples" };

        /// <summary>Every identifier entry's fields in the file's order; fields not in the list keep their place after the known ones.</summary>
        internal static void OrderFields(JsonObject root)
        {
            foreach (var ecu in (root["ecus"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                foreach (var list in new[] { ecu["dids"], ecu["discovered"] }.OfType<JsonArray>())
                    foreach (var entry in list.OfType<JsonObject>())
                    {
                        var fields = entry.ToList();
                        var ordered = fields.Where(p => EntryFields.Contains(p.Key)).OrderBy(p => Array.IndexOf(EntryFields, p.Key))
                            .Concat(fields.Where(p => !EntryFields.Contains(p.Key))).ToList();
                        if (ordered.Select(p => p.Key).SequenceEqual(fields.Select(p => p.Key))) continue;
                        entry.Clear();
                        foreach (var (key, value) in ordered) entry[key] = value;
                    }
        }

        public static void Write(JsonObject root, string path)
        {
            // The relaxed encoder keeps ä and ö in the names readable instead
            // of escaping them. Comments in the file are not kept.
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            // write beside it and swap, so an interrupted merge cannot leave half a file
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(root.ToJsonString(opts));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
    }
}
