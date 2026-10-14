using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The shipped car files as written, before loading: loading forgives a lot (a misspelt poll value
    /// reads as "slow", an ECU with a bad address is left out), so a mistake there would never show.
    /// The file format is public; these keep it exactly as documented.
    /// </summary>
    public class CarFileRawTests
    {
        public static IEnumerable<object[]> Files() =>
            Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName), "*.json").Select(f => new object[] { Path.GetFileName(f) });

        static (JsonObject Raw, Catalog Loaded) Read(string file)
        {
            var path = Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName, file);
            return (JsonNode.Parse(File.ReadAllText(path))!.AsObject(), Catalog.Load(path));
        }

        static IEnumerable<(string Ecu, JsonObject Entry)> Entries(JsonObject raw) =>
            raw["ecus"]!.AsArray().OfType<JsonObject>().SelectMany(e =>
                new[] { "dids", "discovered" }.SelectMany(list => (e[list] as JsonArray ?? new()).OfType<JsonObject>().Select(d => ((string)e["id"]!, d))));

        [Theory, MemberData(nameof(Files))]
        public void NothingIsDroppedOnLoading(string file)
        {
            var (raw, loaded) = Read(file);
            Assert.Equal(raw["ecus"]!.AsArray().Count, loaded.Ecus.Count);
            Assert.Equal((raw["ecu_search"] as JsonArray)?.Count ?? 0, loaded.EcuSearch.Count);
            Assert.Equal(raw["live"]!.AsObject().Count, loaded.Live.Count);
        }

        [Theory, MemberData(nameof(Files))]
        public void FieldsHoldOnlyTheirDocumentedValues(string file)
        {
            var (raw, _) = Read(file);
            var levels = raw["confidence_levels"]!.AsObject().Select(p => p.Key).ToList();
            var sources = raw["sources"]!.AsArray().Select(s => (string)s!["id"]!).ToHashSet();

            foreach (var (name, live) in raw["live"]!.AsObject())
                Assert.True((string?)live!["poll"] is "fast" or "slow" or "cells" or "rare", $"{file} live {name}: poll");
            Assert.Contains((string)raw["platform"]!["elm_protocol"]!, Scanner.SupportedProtocols);
            foreach (var (ecu, d) in Entries(raw))
            {
                string at = $"{file} {ecu} {d["did"]}";
                Assert.True(levels.Contains((string?)d["confidence"] ?? ""), $"{at}: confidence");
                Assert.True(d["source"] is JsonArray { Count: > 0 }, $"{at}: source is a list");
                Assert.All(d["source"]!.AsArray(), s => Assert.True(sources.Contains((string)s!), $"{at}: source {s} not in sources"));
            }
            foreach (var frame in (raw["broadcast_frames"] as JsonArray ?? new()).OfType<JsonObject>())
                Assert.True(frame["source"] is JsonArray { Count: > 0 } list && list.All(s => sources.Contains((string)s!)),
                    $"{file} broadcast frame {frame["id"]}: source is a list of the file's sources");
        }

        [Theory, MemberData(nameof(Files))]
        public void NoFieldIsNull(string file)
        {
            var (raw, _) = Read(file);
            var nulls = new List<string>();
            void Walk(JsonNode? node, string path)
            {
                switch (node)
                {
                    case null: nulls.Add(path); break;
                    case JsonObject o: foreach (var (k, v) in o) Walk(v, $"{path}.{k}"); break;
                    case JsonArray a: for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]"); break;
                }
            }
            Walk(raw, file);
            Assert.Empty(nulls);
        }

        [Theory, MemberData(nameof(Files))]
        public void EntriesAreSortedAndUnique(string file)
        {
            var (raw, _) = Read(file);
            var rank = raw["confidence_levels"]!.AsObject().Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i);
            foreach (var e in raw["ecus"]!.AsArray().OfType<JsonObject>())
            {
                var all = new List<int>();
                foreach (var list in new[] { "dids", "discovered" })
                {
                    var keys = (e[list] as JsonArray ?? new()).OfType<JsonObject>()
                        .Select(d => (Rank: rank[(string)d["confidence"]!], Did: Catalog.ParseDid((string)d["did"]!))).ToList();
                    Assert.True(keys.SequenceEqual(keys.OrderBy(k => k.Rank).ThenBy(k => k.Did)), $"{file} {e["id"]} {list}: not sorted by confidence, then identifier");
                    all.AddRange(keys.Select(k => k.Did));
                }
                Assert.True(all.Count == all.Distinct().Count(), $"{file} {e["id"]}: an identifier is listed twice");
            }
        }

        static readonly string[] EntryOrder = { "did", "name", "unit", "bytes", "encoding", "expression", "confidence", "kind", "changes", "source", "notes", "samples" };
        static readonly string[] EcuOrder = { "id", "name", "request", "response", "answered", "found_by_search", "notes", "scanned", "dids", "discovered" };
        static readonly string[] LiveOrder = { "ecu", "did", "dids", "parts", "poll", "expression", "bytes", "signed", "scale", "offset", "skip", "count", "columns", "map", "on", "format", "min", "max" };

        /// <summary>The fields an object has, in the order of <paramref name="order"/>, are in that order: the file reads the same everywhere.</summary>
        static void InOrder(JsonObject o, string[] order, string at)
        {
            var known = o.Select(p => p.Key).Where(order.Contains).ToList();
            Assert.True(known.SequenceEqual(known.OrderBy(k => Array.IndexOf(order, k))), $"{at}: fields out of order ({string.Join(", ", known)})");
        }

        [Theory, MemberData(nameof(Files))]
        public void FieldsAreInOneOrder(string file)
        {
            var (raw, _) = Read(file);
            foreach (var e in raw["ecus"]!.AsArray().OfType<JsonObject>()) InOrder(e, EcuOrder, $"{file} {e["id"]}");
            foreach (var (ecu, d) in Entries(raw)) InOrder(d, EntryOrder, $"{file} {ecu} {d["did"]}");
            foreach (var (name, live) in raw["live"]!.AsObject()) InOrder(live!.AsObject(), LiveOrder, $"{file} live {name}");
        }

        [Theory, MemberData(nameof(Files))]
        public void NoSampleHoldsAVin(string file)
        {
            var (raw, _) = Read(file);
            var vin = new Regex("[A-HJ-NPR-Z0-9]{17}");
            foreach (var (ecu, d) in Entries(raw))
                foreach (var hex in (d["samples"] as JsonArray ?? new()).Select(s => (string?)s?["hex"]).OfType<string>())
                {
                    byte[] bytes;
                    try { bytes = Convert.FromHexString(hex); } catch (FormatException) { continue; }
                    var text = Encoding.ASCII.GetString(bytes.Select(b => b is >= 0x20 and < 0x7F ? b : (byte)' ').ToArray());
                    // a VIN holds letters (the maker code first): a run of digits is a temperature or a counter
                    bool looksLikeVin = vin.Matches(text).Any(m => m.Value.Count(char.IsLetter) >= 3);
                    Assert.False(looksLikeVin,$"{file} {ecu} {d["did"]}: a sample reads as a VIN ({text})");
                }
        }
    }
}
