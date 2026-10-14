using System.Globalization;
using System.Text.Json.Nodes;
using EscoEvObdScanner.AppLogic.ViewModels;

namespace EscoEvObdScanner.Tests
{
    /// <summary>Odd inputs that must not give a wrong answer: blank serials, numbers that are no numbers, other date forms.</summary>
    public class EdgeGuardTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("00000000")]
        [InlineData("FFFFFFFF")]
        [InlineData("2020202020")]
        public void ABlankSerialTellsNoCarApart(string hex) => Assert.True(VehicleIdentity.IsBlank(Convert.FromHexString(hex)));

        [Fact]
        public void ARealSerialIsNotBlank() => Assert.False(VehicleIdentity.IsBlank(Convert.FromHexString("3132333435")));

        [Fact]
        public void EnergyFiguresThatAreNoNumbersGiveNoEstimate()
        {
            Assert.Null(EnergyCost.Euros(double.NaN, 16, 10));
            Assert.Null(EnergyCost.Per100Km(-5, 1000));
            Assert.Null(EnergyCost.Since(double.NaN, 60000, 0));
            Assert.Null(EnergyCost.PerYear(1000, DateTime.Now.AddYears(1), DateTime.Now));    // taken into use in the future
        }

        [Theory]
        [InlineData("nl-NL", "4-12-2024")]
        [InlineData("fi-FI", "4.12.2024")]
        [InlineData("en-US", "2024-12-04")]
        public void TheInUseDateReadsInTheFormItIsShownIn(string culture, string text)
        {
            var saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            try { Assert.Equal(new DateTime(2024, 12, 4), LiveViewModel.ParseDate(text)); }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        [Fact]
        public void MergeKeepsTheFileOrderAndTheSourcesAndWritesNoNullTime()
        {
            var path = TestData.CatalogCopy();
            var dir = Path.GetDirectoryName(path)!;
            // a snap of an identifier a scan found before, and a new one below the existing ones, without a time
            string scan = Path.Combine(dir, "scan_bms_D000-DFFF_20261012_071519.jsonl");
            File.WriteAllText(scan, """{"ecu":"bms","did":"0xDFFE","kind":"positive","hex":"01"}""");
            string snap = Path.Combine(dir, "snap_20261012_080000.jsonl");
            File.WriteAllText(snap, """{"ts":"2026-10-12T05:00:00Z","ecu":"bms","did":"0xDFFE","kind":"positive","hex":"02"}""");
            CatalogMerge.Merge(Catalog.Load(path), new[] { scan });
            CatalogMerge.Merge(Catalog.Load(path), new[] { snap });

            var bms = JsonNode.Parse(File.ReadAllText(path))!["ecus"]!.AsArray().Single(e => (string?)e!["id"] == "bms")!;
            var discovered = bms["discovered"]!.AsArray().OfType<JsonObject>().ToList();
            var entry = discovered.Single(e => (string?)e["did"] == "0xDFFE");
            Assert.Equal(new[] { "own-scan", "own-snaps" }, entry["source"]!.AsArray().Select(s => (string)s!));
            Assert.DoesNotContain(entry["samples"]!.AsArray(), s => s!.AsObject().ContainsKey("ts") && s["ts"] is null);
            Assert.False(entry["samples"]![0]!.AsObject().ContainsKey("ts"));
            var dids = discovered.Select(e => Catalog.ParseDid((string)e["did"]!)).ToList();
            Assert.Equal(dids.OrderBy(d => d), dids);
            // and the fields in the file's order
            var order = new[] { "did", "name", "unit", "bytes", "encoding", "expression", "confidence", "kind", "changes", "source", "notes", "samples" };
            var keys = entry.Select(p => p.Key).ToList();
            Assert.Equal(keys.OrderBy(k => Array.IndexOf(order, k)), keys);
        }
    }
}
