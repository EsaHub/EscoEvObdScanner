using System.Globalization;
using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>
    /// A snap reads the identifiers found so far, once, and saves the values
    /// together with a note on what the car was doing. Two snaps taken either
    /// side of a change - charging, a drive, the heater - are then compared:
    /// whatever moved, and by how much, is what gives an identifier away.
    /// Values that seldom change and identifiers the ECU refused are read only
    /// when asked for.
    /// </summary>
    public static class Snapshot
    {
        // After this many unanswered reads in a row the ECU has gone to sleep or
        // is not reachable in the current state; the rest of its list is skipped.
        public const int MaxSilent = 20;

        // This many adapter errors in a row, across ECUs, mean the adapter itself
        // is not answering (it lost power, went to sleep): the snap is abandoned
        // rather than saved as a file of errors.
        public const int MaxAdapterErrors = 10;

        /// <summary>Unreachable: the adapter would not address the ECU, so nothing was asked.</summary>
        public sealed record EcuResult(EcuDef Ecu, int Answered, int Total, bool StoppedSilent, bool Unreachable = false);

        /// <summary>This many ECUs in a row that cannot be addressed, with none reached before, mean the adapter is gone.</summary>
        public const int MaxUnreachable = 2;

        /// <summary>
        /// Catalogued first, then discovered; an identifier in both is read once.
        /// Rare and refused identifiers (see <see cref="DidDef.ReadWith"/>) only when asked for.
        /// </summary>
        public static List<DidDef> Targets(Catalog catalog, string? ecuFilter = null, bool rare = false, bool refused = false) =>
            catalog.Dids.Concat(catalog.Discovered)
                .Where(d => !string.IsNullOrEmpty(d.Ecu) && d.Did >= 0 && catalog.TryEcu(d.Ecu) != null)
                .Where(d => d.ReadWith(rare, refused))
                .Where(d => ecuFilter == null ||
                            string.Equals(d.Ecu, ecuFilter, StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => (Ecu: d.Ecu.ToLowerInvariant(), d.Did))
                .Select(g => g.First())
                .ToList();

        /// <summary>
        /// Reads every target and writes one JSON line per identifier read to
        /// <paramref name="output"/>. Calls <paramref name="ecuDone"/> after
        /// each ECU and <paramref name="progress"/> after each read.
        /// </summary>
        public static List<EcuResult> Take(
            Scanner scanner, Catalog catalog, IReadOnlyList<DidDef> targets, string note, TextWriter output,
            Action<EcuDef>? ecuStarted = null, Action<EcuResult>? ecuDone = null,
            Action<int, int>? progress = null, CancellationToken ct = default)
        {
            var results = new List<EcuResult>();
            int done = 0, adapterErrors = 0, unreachable = 0;

            foreach (var group in targets.GroupBy(d => d.Ecu.ToLowerInvariant()))
            {
                if (ct.IsCancellationRequested) break;

                var ecu = catalog.Ecu(group.Key);
                ecuStarted?.Invoke(ecu);
                try { scanner.Target(ecu.Request, ecu.Response); }
                catch (Exception ex) when (ex is AdapterException { Problem: AdapterProblem.CommandRefused } or ArgumentOutOfRangeException)
                {
                    // this ECU cannot be addressed; the others still can, unless none can
                    if (++unreachable >= MaxUnreachable && results.All(r => r.Unreachable))
                        throw new AdapterException(AdapterProblem.NoEcuAddressable,
                            $"The adapter would not address {unreachable} ECUs in a row. Disconnect, check the adapter and connect again.",
                            count: unreachable);
                    done += group.Count();
                    progress?.Invoke(done, targets.Count);
                    results.Add(new EcuResult(ecu, 0, group.Count(), false, Unreachable: true));
                    ecuDone?.Invoke(results[^1]);
                    continue;
                }

                int ok = 0, silent = 0, asked = 0;
                bool stopped = false, lost = false;
                foreach (var d in group.OrderBy(x => x.Did))
                {
                    if (ct.IsCancellationRequested) break;

                    UdsResponse r;
                    // after an adapter reset the ECU is addressed again; refused, this ECU ends here, the snap goes on
                    try { r = scanner.ReadDid(d.Did, 1000); }
                    catch (AdapterException ex) when (ex.Problem == AdapterProblem.CommandRefused)
                    {
                        lost = true;
                        done += group.Count() - asked;
                        progress?.Invoke(done, targets.Count);
                        break;
                    }
                    asked++;
                    progress?.Invoke(++done, targets.Count);

                    adapterErrors = r.Kind == ResponseKind.AdapterError ? adapterErrors + 1 : 0;
                    if (adapterErrors >= MaxAdapterErrors)
                        throw new AdapterException(AdapterProblem.NotAnswering,
                            $"The adapter gave no usable answer to {MaxAdapterErrors} reads in a row. Disconnect, check the adapter and connect again.",
                            count: MaxAdapterErrors);
                    silent = r.Kind == ResponseKind.NoData ? silent + 1 : 0;
                    if (r.Kind == ResponseKind.Positive) ok++;

                    output.WriteLine(new JsonObject
                    {
                        ["ts"] = DateTime.UtcNow.ToString("O"),
                        ["note"] = note,
                        ["ecu"] = ecu.Id,
                        ["did"] = $"0x{d.Did:X4}",
                        ["name"] = d.EnglishName,
                        ["kind"] = r.KindLabel,
                        ["hex"] = Bytes.Hex(r.Data),
                        ["value"] = r.Kind == ResponseKind.Positive ? Expr.Evaluate(d.Expression, r.Data) : null
                    }.ToJsonString());

                    if (silent >= MaxSilent)
                    {
                        stopped = true;
                        // what this ECU will not be asked still counts as done
                        done += group.Count() - asked;
                        progress?.Invoke(done, targets.Count);
                        break;
                    }
                }

                var result = new EcuResult(ecu, ok, group.Count(), stopped, Unreachable: lost);
                results.Add(result);
                ecuDone?.Invoke(result);
            }

            return results;
        }

        // -------------------------------------------------------------------

        public sealed record Entry(string Ecu, string Did, string? Name, string Kind, string Hex, double? Value);

        public sealed record Snap(string Path, string Note, Dictionary<string, Entry> Entries);

        public static Snap Load(string path)
        {
            var entries = new Dictionary<string, Entry>();
            string note = "";

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? o;
                try { o = JsonNode.Parse(line)?.AsObject(); }
                catch { continue; }
                if (o == null) continue;

                // a file from elsewhere may hold a field of the wrong type; skip, do not throw
                string? ecu = Text(o, "ecu");
                string? did = Text(o, "did");
                if (ecu == null || did == null) continue;

                note = Text(o, "note") ?? note;
                entries[$"{ecu}|{did}"] = new Entry(
                    ecu, did,
                    Text(o, "name"),
                    Text(o, "kind") ?? "",
                    Text(o, "hex") ?? "",
                    o["value"] is JsonValue v && v.TryGetValue(out double d) ? d : null);
            }

            return new Snap(path, note, entries);
        }

        static string? Text(JsonObject o, string name) =>
            o[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        // -------------------------------------------------------------------

        /// <summary>
        /// One identifier whose value differs between two snaps. Short values
        /// (up to 4 bytes) come with their numeric difference, which is what
        /// reveals a counter's unit.
        /// </summary>
        public sealed record Change(Entry? A, Entry? B)
        {
            Entry Any => B ?? A!;
            public string Ecu => Any.Ecu;
            public string Did => Any.Did;
            public string? Name => Any.Name;

            public bool BothPositive => A?.Kind == "positive" && B?.Kind == "positive";

            public bool IsNumeric =>
                BothPositive && A!.Hex.Length == B!.Hex.Length && A.Hex.Length is > 0 and <= 8
                && A.Hex.All(Uri.IsHexDigit) && B.Hex.All(Uri.IsHexDigit);

            public ulong? RawA => IsNumeric ? ulong.Parse(A!.Hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
            public ulong? RawB => IsNumeric ? ulong.Parse(B!.Hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
            public long? Delta => IsNumeric ? unchecked((long)(RawB!.Value - RawA!.Value)) : null;

            public int BytesChanged
            {
                get
                {
                    if (!BothPositive || A!.Hex.Length != B!.Hex.Length) return 0;
                    int n = 0;
                    for (int i = 0; i + 1 < A.Hex.Length; i += 2)
                        if (A.Hex[i] != B.Hex[i] || A.Hex[i + 1] != B.Hex[i + 1]) n++;
                    return n;
                }
            }

            public string Describe()
            {
                if (!BothPositive)
                    return $"{A?.Kind ?? "missing"} -> {B?.Kind ?? "missing"}";
                if (IsNumeric)
                    return $"{A!.Hex} -> {B!.Hex}   {RawA} -> {RawB}  ({Delta:+#;-#;0})";
                if (A!.Hex.Length != B!.Hex.Length)
                    return $"length {A.Hex.Length / 2} -> {B.Hex.Length / 2} bytes";
                return $"{BytesChanged} of {A.Hex.Length / 2} bytes differ";
            }
        }

        /// <summary>
        /// The identifiers both snaps read, changed and unchanged. One that only
        /// one snap read (a snap that also read the rare ones, an identifier merged
        /// in between) is counted, not compared: a snap writes every identifier it
        /// asked, answered or not, so a missing one was not asked.
        /// </summary>
        public static (List<Change> Changes, int Unchanged, int OnlyInOne) Compare(Snap a, Snap b)
        {
            var changes = new List<Change>();
            int same = 0, onlyInOne = 0;

            foreach (var key in a.Entries.Keys.Union(b.Entries.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                a.Entries.TryGetValue(key, out var x);
                b.Entries.TryGetValue(key, out var y);

                if (x == null || y == null) { onlyInOne++; continue; }
                if (x.Kind == y.Kind && x.Hex == y.Hex) { same++; continue; }
                changes.Add(new Change(x, y));
            }

            return (changes, same, onlyInOne);
        }
    }
}
