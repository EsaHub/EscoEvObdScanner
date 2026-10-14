using System.Text.Json.Nodes;

namespace EscoEvObdScanner.Tests
{
    /// <summary>A newer shipped car file reaches the user without losing what they merged into their copy.</summary>
    public class CarFileUpdateTests
    {
        static string Dir()
        {
            var d = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        // a shipped car file: version, one identifying DID, the BMS with one known and one discovered identifier
        static string Car(int version, string extraKnown = "") => $$"""
            {
              "schema": 2, "version": {{version}},
              "platform": { "name": "Test", "identifying": ["0xD901"] },
              "ecus": [
                { "id": "bms", "name": { "fi": "BMS" }, "request": "0x6B4", "response": "0x694",
                  "dids": [ { "did": "0xD816", "name": "pack_current", "expression": "[B4:B7]/64-1200" }{{extraKnown}} ],
                  "discovered": [ { "did": "0xD440", "samples": [ { "ts": "t1", "hex": "0E2D" } ] } ] }
              ],
              "blocks": []
            }
            """;

        static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        [Fact]
        public void TheFirstUseCopiesTheShippedFile()
        {
            var d = Dir();
            File.WriteAllText(Path.Combine(d, "shipped.json"), Car(1));
            var r = CarFileUpdate.Sync(Path.Combine(d, "shipped.json"), Path.Combine(d, "user", "car.json"), Path.Combine(d, "backup"));
            Assert.Equal(CarFileUpdate.Outcome.Created, r.Outcome);
            Assert.Equal(1, CarFileUpdate.VersionOf(Read(Path.Combine(d, "user", "car.json"))));
        }

        [Fact]
        public void AnUnchangedCopyIsReplaced()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            File.WriteAllText(working, Car(1));
            File.WriteAllText(shipped, Car(2));
            var r = CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));
            Assert.Equal(CarFileUpdate.Outcome.Replaced, r.Outcome);
            Assert.Equal(2, CarFileUpdate.VersionOf(Read(working)));
            // kept anyway: the user may have edited it by hand
            Assert.Equal(1, CarFileUpdate.VersionOf(Read(r.Backup!)));
        }

        [Fact]
        public void TheUsersFindingsAreCarriedOverAndTheOldCopyKept()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            // the user merged: a new discovered identifier, a sample, and a value of an identifying DID (not to be kept)
            var mine = Read(Path.Combine(Write(d, "v1.json", Car(1))));
            mine["user_changes"] = true;
            var disc = mine["ecus"]![0]!["discovered"]!.AsArray();
            disc[0]!["samples"]!.AsArray().Add(new JsonObject { ["ts"] = "t2", ["hex"] = "0E30" });
            disc.Add(new JsonObject { ["did"] = "0xD8AA", ["samples"] = new JsonArray(new JsonObject { ["ts"] = "t3", ["hex"] = "03" }) });
            disc.Add(new JsonObject { ["did"] = "0xD901", ["samples"] = new JsonArray(new JsonObject { ["ts"] = "t4", ["hex"] = "56494E" }) });
            File.WriteAllText(working, mine.ToJsonString());
            // version 2 catalogues 0xD8AA
            File.WriteAllText(shipped, Car(2, """, { "did": "0xD8AA", "name": "charge_cable_state" }"""));

            var r = CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));

            Assert.Equal(CarFileUpdate.Outcome.Merged, r.Outcome);
            Assert.True(File.Exists(r.Backup));
            var root = Read(working);
            Assert.Equal(2, CarFileUpdate.VersionOf(root));
            Assert.True(root["user_changes"]!.GetValue<bool>());
            var bms = root["ecus"]![0]!.AsObject();
            // catalogued in version 2: its meaning, plus the user's sample
            var known = bms["dids"]!.AsArray().Single(e => (string?)e!["did"] == "0xD8AA")!;
            Assert.Equal("charge_cable_state", (string?)known["name"]);
            Assert.Single(known["samples"]!.AsArray());
            Assert.DoesNotContain(bms["discovered"]!.AsArray(), e => (string?)e!["did"] == "0xD8AA");
            // the discovered one keeps both samples
            var cells = bms["discovered"]!.AsArray().Single(e => (string?)e!["did"] == "0xD440")!;
            Assert.Equal(2, cells["samples"]!.AsArray().Count);
            // the identifying DID comes along, but without its value
            var vin = bms["discovered"]!.AsArray().Single(e => (string?)e!["did"] == "0xD901")!;
            Assert.Empty(vin["samples"]!.AsArray());
        }

        [Fact]
        public void AnUnreadableCopyIsReplacedAndKept()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            File.WriteAllText(shipped, Car(1));
            File.WriteAllText(working, "{ \"schema\": 2, \"ecus\": [");
            var r = CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));
            Assert.Equal(CarFileUpdate.Outcome.Repaired, r.Outcome);
            Assert.Equal(1, CarFileUpdate.VersionOf(Read(working)));
            Assert.StartsWith("{ \"schema\": 2, \"ecus\": [", File.ReadAllText(r.Backup!));
        }

        [Fact]
        public void RepairPutsTheShippedFileInPlaceAndKeepsTheBrokenCopy()
        {
            // valid JSON, but no car file: a hand edit gave the ECUs the wrong type
            var d = Dir();
            string shipped = Write(d, "shipped.json", Car(4)), working = Write(d, "car.json", """{ "schema": 2, "version": 9, "ecus": "bms" }""");

            var r = CarFileUpdate.Repair(shipped, working, Path.Combine(d, "backup"));

            Assert.Equal(CarFileUpdate.Outcome.Repaired, r.Outcome);
            Assert.Equal(4, r.ToVersion);
            Assert.Equal(File.ReadAllText(shipped), File.ReadAllText(working));
            Assert.Equal(Path.Combine(d, "backup"), Path.GetDirectoryName(r.Backup));
            Assert.StartsWith("car-broken-", Path.GetFileName(r.Backup));
            Assert.Contains("\"ecus\": \"bms\"", File.ReadAllText(r.Backup!));
            Catalog.Load(working);
        }

        [Fact]
        public void CarriedSamplesKeepTheFirstAndTheNewest()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            // the working copy has collected many values of 0xD440; the shipped file has its first one
            var mine = Read(Write(d, "v1.json", Car(1)));
            mine["user_changes"] = true;
            var samples = mine["ecus"]![0]!["discovered"]![0]!["samples"]!.AsArray();
            for (int i = 1; i <= CatalogMerge.MaxSamples + 5; i++)
                samples.Add(new JsonObject { ["ts"] = $"t{i + 1}", ["hex"] = $"0E{i:X2}" });
            File.WriteAllText(working, mine.ToJsonString());
            File.WriteAllText(shipped, Car(2));

            var r = CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));

            Assert.Equal(CarFileUpdate.Outcome.Merged, r.Outcome);
            Assert.Equal(CatalogMerge.MaxSamples + 5, r.CarriedOver);
            var kept = Read(working)["ecus"]![0]!["discovered"]![0]!["samples"]!.AsArray().Select(s => (string?)s!["hex"]).ToList();
            Assert.Equal(CatalogMerge.MaxSamples, kept.Count);
            Assert.Equal("0E2D", kept[0]);
            Assert.Equal($"0E{CatalogMerge.MaxSamples + 5:X2}", kept[^1]);
            Assert.Equal(kept.Count, kept.Distinct().Count());
        }

        [Theory]
        [InlineData("0E2D", true)]       // nothing new carried: the shipped mark stays
        [InlineData("0E99", false)]      // a value of the user's own: it changes after all
        public void TheSeldomChangesMarkGoesOnlyWithACarriedSample(string userHex, bool kept)
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            // the shipped entry already has two values and is marked as seldom changing
            var ship = Read(Write(d, "v2.json", Car(2)));
            var entry = ship["ecus"]![0]!["discovered"]![0]!.AsObject();
            entry["samples"]!.AsArray().Add(new JsonObject { ["ts"] = "t5", ["hex"] = "0E40" });
            entry["changes"] = "rare";
            File.WriteAllText(shipped, ship.ToJsonString());
            var mine = Read(Write(d, "v1.json", Car(1)));
            mine["user_changes"] = true;
            mine["ecus"]![0]!["discovered"]![0]!["samples"]![0]!["hex"] = userHex;
            File.WriteAllText(working, mine.ToJsonString());

            CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));

            var result = Read(working)["ecus"]![0]!["discovered"]![0]!.AsObject();
            Assert.Equal(kept, result.ContainsKey("changes"));
        }

        [Fact]
        public void AnIdentifierTheShippedFileDroppedStaysDropped()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            // version 1 catalogues 0xD8AA (wrongly); version 2 does not list it
            var mine = Read(Write(d, "v1.json", Car(1, """, { "did": "0xD8AA", "name": "wrong_guess" }""")));
            mine["user_changes"] = true;
            File.WriteAllText(working, mine.ToJsonString());
            File.WriteAllText(shipped, Car(2));
            CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));
            Assert.DoesNotContain("0xD8AA", File.ReadAllText(working));
        }

        [Fact]
        public void FindingsFollowAnEcuTheShippedFileRenamed()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            // the working copy calls the ECU "battery"; version 2 calls the same address "bms"
            var mine = Read(Write(d, "v1.json", Car(1)));
            mine["user_changes"] = true;
            mine["ecus"]![0]!["id"] = "battery";
            mine["ecus"]![0]!["discovered"]!.AsArray().Add(new JsonObject { ["did"] = "0xD8AA", ["samples"] = new JsonArray(new JsonObject { ["ts"] = "t3", ["hex"] = "03" }) });
            File.WriteAllText(working, mine.ToJsonString());
            File.WriteAllText(shipped, Car(2));

            CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));

            var ecus = Read(working)["ecus"]!.AsArray();
            Assert.Equal("bms", (string?)Assert.Single(ecus)!["id"]);
            Assert.Contains(ecus[0]!["discovered"]!.AsArray(), e => (string?)e!["did"] == "0xD8AA");
        }

        [Fact]
        public void ShippedCarFilesCarryNoUserMarks()
        {
            // a merge or an import in the source tree would make every user's copy look changed
            foreach (var path in CarFiles.In(Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName)))
            {
                var root = Read(path);
                Assert.Null(root["user_changes"]);
                Assert.Null(root["imported"]);
            }
        }

        [Fact]
        public void ANewerOrImportedCopyIsLeftAlone()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            File.WriteAllText(shipped, Car(2));
            File.WriteAllText(working, Car(3));
            Assert.Equal(CarFileUpdate.Outcome.UpToDate, CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup")).Outcome);
            Assert.Equal(3, CarFileUpdate.VersionOf(Read(working)));

            var imported = Read(Write(d, "x.json", Car(1)));
            imported["imported"] = true;
            File.WriteAllText(working, imported.ToJsonString());
            Assert.Equal(CarFileUpdate.Outcome.UpToDate, CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup")).Outcome);
            Assert.Equal(1, CarFileUpdate.VersionOf(Read(working)));
        }

        [Fact]
        public void MergeMarksTheCopyAsChanged()
        {
            var catalog = Catalog.Load(TestData.CatalogCopy());
            var scan = Path.Combine(Path.GetDirectoryName(catalog.Path)!, "scan.jsonl");
            File.WriteAllText(scan, """{"ecu":"bms","did":"0xD8F1","kind":"positive","hex":"0102"}""" + "\n");
            CatalogMerge.Merge(catalog, new[] { scan });
            Assert.True(Read(catalog.Path)["user_changes"]!.GetValue<bool>());
        }

        [Fact]
        public void EveryShippedCarFileHasAVersion()
        {
            foreach (var path in CarFiles.In(Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName)))
                Assert.True(CarFileUpdate.VersionOf(Read(path)) >= 1, path);
        }

        static string Write(string dir, string name, string text)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, text);
            return p;
        }

        [Fact]
        public void AValueTheUserSawMarksARefusedEntryAsAnswering()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            var mine = Read(Write(d, "v1.json", Car(1)));
            mine["user_changes"] = true;
            mine["ecus"]![0]!["discovered"]!.AsArray().Add(new JsonObject { ["did"] = "0xD9AA", ["samples"] = new JsonArray(new JsonObject { ["ts"] = "t", ["hex"] = "0102" }) });
            File.WriteAllText(working, mine.ToJsonString());
            // version 2 lists it as refused, with no value
            var v2 = Read(Write(d, "v2.json", Car(2)));
            v2["ecus"]![0]!["discovered"]!.AsArray().Add(new JsonObject { ["did"] = "0xD9AA", ["kind"] = "negative:conditionsNotCorrect", ["samples"] = new JsonArray() });
            File.WriteAllText(shipped, v2.ToJsonString());

            CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));

            var entry = Read(working)["ecus"]![0]!["discovered"]!.AsArray().Single(e => (string?)e!["did"] == "0xD9AA")!;
            Assert.Equal("positive", (string?)entry["kind"]);
            Assert.Equal(2, (int)entry["bytes"]!);
        }

        [Fact]
        public void OnlyAnEcuTheSearchAddedIsCarriedOver()
        {
            var d = Dir();
            string shipped = Path.Combine(d, "shipped.json"), working = Path.Combine(d, "car.json");
            var mine = Read(Write(d, "v1.json", Car(1)));
            mine["user_changes"] = true;
            var ecus = mine["ecus"]!.AsArray();
            // one the user's ECU search added, and one an earlier shipped file had that version 2 drops
            ecus.Add(new JsonObject { ["id"] = "ecu6c0", ["request"] = "0x6C0", ["response"] = "0x6A0", ["found_by_search"] = true, ["discovered"] = new JsonArray() });
            ecus.Add(new JsonObject { ["id"] = "old", ["request"] = "0x7E5", ["response"] = "0x7ED", ["dids"] = new JsonArray() });
            File.WriteAllText(working, mine.ToJsonString());
            File.WriteAllText(shipped, Car(2));

            CarFileUpdate.Sync(shipped, working, Path.Combine(d, "backup"));

            var ids = Read(working)["ecus"]!.AsArray().Select(e => (string?)e!["id"]).ToList();
            Assert.Contains("ecu6c0", ids);
            Assert.DoesNotContain("old", ids);
        }
    }
}
