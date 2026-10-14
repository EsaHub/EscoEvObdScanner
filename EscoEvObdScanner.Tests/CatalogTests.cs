using System.Text.Json.Nodes;

namespace EscoEvObdScanner.Tests
{
    /// <summary>Sanity checks on every shipped car file.</summary>
    public class CatalogTests
    {
        static string CarsDir => Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName);

        public static TheoryData<string> Cars()
        {
            var data = new TheoryData<string>();
            foreach (var path in CarFiles.In(CarsDir)) data.Add(CarFiles.NameOf(path));
            return data;
        }

        static string PathOf(string car) => Path.Combine(CarsDir, car + ".json");
        static Catalog Load(string car) => Catalog.Load(PathOf(car));

        [Fact]
        public void CarFilesAreShipped()
        {
            Assert.Contains("stellantis-ecmp", CarFiles.In(CarsDir).Select(CarFiles.NameOf));
        }

        [Theory, MemberData(nameof(Cars))]
        public void Loads(string car)
        {
            var c = Load(car);
            Assert.NotEmpty(c.Ecus);
            Assert.NotEmpty(c.Dids);
            Assert.NotEmpty(c.Blocks);
            Assert.False(string.IsNullOrWhiteSpace(c.Platform.Name));
        }

        [Theory, MemberData(nameof(Cars))]
        public void EveryIdentifierPointsAtAKnownEcu(string car)
        {
            var c = Load(car);
            var ids = c.Ecus.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(c.Dids.Concat(c.Discovered), d => Assert.Contains(d.Ecu, ids));
        }

        [Theory, MemberData(nameof(Cars))]
        public void EveryExpressionParses(string car)
        {
            // enough payload for any expression in the car files
            var payload = Enumerable.Repeat((byte)0x01, 64).ToArray();
            var c = Load(car);
            Assert.All(c.Dids.Where(d => !string.IsNullOrEmpty(d.Expression)),
                d => Assert.True(Expr.Evaluate(d.Expression, payload).HasValue, $"{d.DidHex}: {d.Expression}"));
            Assert.All(c.Live.Where(l => !string.IsNullOrEmpty(l.Value.Expression)),
                l => Assert.True(Expr.Evaluate(l.Value.Expression, payload).HasValue, $"{l.Key}: {l.Value.Expression}"));
        }

        [Theory, MemberData(nameof(Cars))]
        public void EveryLiveEntryIsUsable(string car)
        {
            // every live entry points at an ECU in the file and uses a name the dashboard knows
            var c = Load(car);
            Assert.All(c.Live, l =>
            {
                Assert.NotNull(c.TryEcu(l.Value.Ecu));
                Assert.NotEmpty(l.Value.Dids);
                Assert.True(LivePoller.Scalars.ContainsKey(l.Key) || LivePoller.Texts.ContainsKey(l.Key) || LivePoller.Arrays.Contains(l.Key), l.Key);
            });
        }

        [Theory, MemberData(nameof(Cars))]
        public void EveryNameIsInFinnishAndEnglish(string car)
        {
            // every ECU and block, and every identifier with a name or a confidence of confirmed, reported
            // or unverified, is named in both languages; no entry has a "label" key
            var c = Load(car);
            var named = c.Ecus.Select(e => ($"ECU {e.Id}", e.Names))
                .Concat(c.Blocks.Select(b => ($"block {b.Id}", b.Names)))
                .Concat(c.Dids.Concat(c.Discovered)
                    .Where(d => d.Names != null || d.Confidence is "confirmed" or "reported" or "unverified")
                    .Select(d => ($"{d.Ecu} {d.DidHex}", d.Names)));
            Assert.All(named, n =>
            {
                Assert.False(string.IsNullOrWhiteSpace(n.Names?.GetValueOrDefault("fi")), $"{n.Item1}: no Finnish name");
                Assert.False(string.IsNullOrWhiteSpace(n.Names?.GetValueOrDefault("en")), $"{n.Item1}: no English name");
            });
            Assert.DoesNotContain("\"label\"", File.ReadAllText(PathOf(car)));
        }

        [Theory, MemberData(nameof(Cars))]
        public void EveryArrayPartNamesAnIdentifier(string car)
        {
            // a part with a bad DID would be skipped and shift the numbering of every later cell
            Assert.All(Load(car).Live.Where(l => l.Value.Parts != null),
                l => Assert.Equal(l.Value.Parts!.Count, l.Value.Pieces.Count));
            Assert.All(Load(car).Live.Where(l => l.Value.Parts == null && l.Value.DidList is { Count: > 0 }),
                l => Assert.Equal(l.Value.DidList!.Count, l.Value.Dids.Count));
        }

        [Theory, MemberData(nameof(Cars))]
        public void NoDuplicateIdentifiers(string car)
        {
            var keys = Load(car).Dids.Select(d => (d.Ecu.ToLowerInvariant(), d.Did)).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        [Theory, MemberData(nameof(Cars))]
        public void NothingIsBothCataloguedAndDiscovered(string car)
        {
            var c = Load(car);
            var catalogued = c.Dids.Select(d => (d.Ecu.ToLowerInvariant(), d.Did)).ToHashSet();
            Assert.DoesNotContain(c.Discovered, d => catalogued.Contains((d.Ecu.ToLowerInvariant(), d.Did)));
        }

        [Theory, MemberData(nameof(Cars))]
        public void EcuRequestAddressesAreDistinct(string car)
        {
            var c = Load(car);
            Assert.Equal(c.Ecus.Count, c.Ecus.Select(e => e.Request).Distinct().Count());
        }

        [Theory, MemberData(nameof(Cars))]
        public void EcuReplyAddressesAreDistinct(string car)
        {
            // two ECUs on one reply ID: replies would be filed under the wrong one
            var c = Load(car);
            Assert.Equal(c.Ecus.Count, c.Ecus.Select(e => e.Response).Distinct().Count());
        }

        [Theory, MemberData(nameof(Cars))]
        public void EcuAddressesAreElevenBit(string car)
        {
            Assert.All(Load(car).Ecus, e => Assert.True(e.Request is >= 0 and <= 0x7FF && e.Response is >= 0 and <= 0x7FF, e.Id));
        }

        [Theory, MemberData(nameof(Cars))]
        public void BlocksHaveValidRanges(string car)
        {
            Assert.All(Load(car).Blocks.Where(b => b.IsScannable), b => Assert.True(b.From <= b.To, b.Id));
        }

        [Theory, MemberData(nameof(Cars))]
        public void IsInThePerEcuLayout(string car)
        {
            var root = JsonNode.Parse(File.ReadAllText(PathOf(car)))!.AsObject();
            Assert.Equal(CatalogMerge.Schema, root["schema"]!.GetValue<int>());
            Assert.All(root["ecus"]!.AsArray(), e =>
            {
                Assert.NotNull(e!["dids"]);
                Assert.NotNull(e["discovered"]);
            });
        }

        [Theory, MemberData(nameof(Cars))]
        public void HoldsNoSerialNumbersOrVin(string car)
        {
            var c = Load(car);
            Assert.All(c.Discovered.Concat(c.Dids).Where(d => c.Identifying.Contains(d.Did)),
                d => Assert.Empty(d.Samples ?? new()));
        }

        [Theory, MemberData(nameof(Cars))]
        public void IsPlainJsonWithoutComments(string car)
        {
            // merge rewrites the file and would drop comments silently
            Assert.NotNull(JsonNode.Parse(File.ReadAllText(PathOf(car))));
        }
    }
}
