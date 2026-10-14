namespace EscoEvObdScanner.Tests
{
    public class SnapshotAndMergeTests
    {
        static string TempFile(string name) =>
            Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"), name);

        static string TakeSnap(Scanner scanner, Catalog catalog, string note)
        {
            var path = TempFile("snap.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var w = new StreamWriter(path))
                Snapshot.Take(scanner, catalog, Snapshot.Targets(catalog), note, w);
            return path;
        }

        [Fact]
        public void SnapReadsEveryKnownIdentifier()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var targets = Snapshot.Targets(catalog, rare: true, refused: true);
            var results = Snapshot.Take(scanner, catalog, targets, "test", TextWriter.Null);

            Assert.Equal(targets.Count, results.Sum(r => r.Total));
            // the identifiers with recorded data answer: at least 350 of them
            Assert.True(results.Sum(r => r.Answered) >= 350);
        }

        /// <summary>An adapter that has lost the car: it answers every request with "?".</summary>
        sealed class DeadAdapter : IElmLink
        {
            public event Action<string>? Trace;
            public bool IsOpen => true;
            public void Open() { }
            public void Dispose() { }
            public string[] Send(string command, int timeoutMs = 5000)
            {
                Trace?.Invoke(command);
                return command.StartsWith("AT") ? new[] { "OK" } : new[] { "?" };
            }
        }

        /// <summary>An adapter that has lost its power supply but whose USB side is still there: no reply at all.</summary>
        sealed class SilentAdapter : IElmLink
        {
            public event Action<string>? Trace;
            public bool IsOpen => true;
            public void Open() { }
            public void Dispose() { }
            public string[] Send(string command, int timeoutMs = 5000) { Trace?.Invoke(command); return Array.Empty<string>(); }
        }

        [Fact]
        public void SnapGivesUpWhenNoEcuCanBeAddressed()
        {
            // every target command goes unanswered: not one ECU after another, the adapter itself
            var catalog = TestData.Catalog();
            var ex = Assert.Throws<AdapterException>(() =>
                Snapshot.Take(new Scanner(new SilentAdapter()), catalog, Snapshot.Targets(catalog), "", TextWriter.Null));
            Assert.Equal(AdapterProblem.NoEcuAddressable, ex.Problem);
            Assert.Throws<AdapterException>(() => KnownValues.Read(new Scanner(new SilentAdapter()), catalog));
        }

        [Fact]
        public void SnapGivesUpWhenTheAdapterStopsAnswering()
        {
            // a file of nothing but adapter errors is no snap: stop and say so
            var catalog = TestData.Catalog();
            var written = new StringWriter();
            var ex = Assert.Throws<AdapterException>(() =>
                Snapshot.Take(new Scanner(new DeadAdapter()), catalog, Snapshot.Targets(catalog), "", written));
            Assert.Contains("adapter", ex.Message);
            Assert.True(written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < Snapshot.MaxAdapterErrors);
        }

        [Fact]
        public void SnapStopsOnSilentEcu()
        {
            var (_, scanner, catalog) = TestData.DemoCarWithout("obc");
            var obc = Snapshot.Targets(catalog, "obc");
            var results = Snapshot.Take(scanner, catalog, obc, "", TextWriter.Null);
            Assert.All(results, r => Assert.Equal(0, r.Answered));
            Assert.All(results, r => Assert.True(r.StoppedSilent));
        }

        [Fact]
        public void CompareFindsWhatChanged()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            var a = TakeSnap(scanner, catalog, "before");

            car.SetData(0x6B4, 0xD410, new byte[] { 0x78, 0x00 });              // SOC 60 %
            car.SetData(0x6B4, 0xD873, new byte[] { 0x00, 0x01, 0x48, 0xB0 });  // +100
            var b = TakeSnap(scanner, catalog, "after");

            var (changes, same, onlyInOne) = Snapshot.Compare(Snapshot.Load(a), Snapshot.Load(b));

            Assert.Equal(2, changes.Count);
            Assert.True(same > 300);
            Assert.Equal(0, onlyInOne);
            var soc = changes.Single(c => c.Did == "0xD410");
            Assert.Equal(60.0, soc.B!.Value!.Value, 3);
            Assert.Equal("before", Snapshot.Load(a).Note);
        }

        [Fact]
        public void SnapLeavesOutRareAndRefusedUnlessAsked()
        {
            var catalog = TestData.Catalog();
            var all = Snapshot.Targets(catalog, rare: true, refused: true);
            Assert.Contains(all, d => d.Rare);
            Assert.Contains(all, d => d.Refused);

            var fast = Snapshot.Targets(catalog);
            Assert.DoesNotContain(fast, d => d.Rare || d.Refused);
            Assert.Equal(all.Count(d => !d.Rare && !d.Refused), fast.Count);

            Assert.DoesNotContain(Snapshot.Targets(catalog, rare: true), d => d.Refused);
            Assert.DoesNotContain(Snapshot.Targets(catalog, refused: true), d => d.Rare);
            Assert.Equal(all.Count, Snapshot.Targets(catalog, rare: true, refused: true).Count);
        }

        [Fact]
        public void CompareCountsWhatOnlyOneSnapRead()
        {
            // a snap that also read the rare ones against one that did not: the extra ones are counted, not listed as changes
            var (_, scanner, catalog) = TestData.DemoCar();
            var a = TakeSnap(scanner, catalog, "fast");
            var b = TempFile("snap.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(b)!);
            using (var w = new StreamWriter(b))
                Snapshot.Take(scanner, catalog, Snapshot.Targets(catalog, rare: true), "with rare", w);

            var (changes, same, onlyInOne) = Snapshot.Compare(Snapshot.Load(a), Snapshot.Load(b));

            Assert.Empty(changes);
            Assert.Equal(Snapshot.Targets(catalog).Count, same);
            Assert.Equal(Snapshot.Targets(catalog, rare: true).Count - same, onlyInOne);
        }

        [Fact]
        public void ScannedCoverageCountsFullRangesOnce()
        {
            var ranges = new[]
            {
                new ScannedRange { RangeText = "0x0000-0xCFFF", Step = 16 },    // a sample: not scanned in full
                new ScannedRange { RangeText = "0x0100-0x01FF" },
                new ScannedRange { RangeText = "0x0180-0x02FF" },                // overlaps the one before
                new ScannedRange { RangeText = "0xD000-0xDFFF" },
                new ScannedRange { RangeText = "bad" }
            };

            var (covered, overlapping) = ScannedRange.Coverage(ranges, 0x0000, 0x0FFF);
            Assert.Equal(0x200, covered);
            Assert.Equal(3, overlapping.Count);
            Assert.Equal(0x1000, ScannedRange.Coverage(ranges, 0xD000, 0xDFFF).Covered);
            Assert.Equal(0x80, ScannedRange.Coverage(ranges, 0xD000, 0xD07F).Covered);
            Assert.Empty(ScannedRange.Coverage(ranges, 0xE000, 0xEFFF).Overlapping);
        }

        [Fact]
        public void CarFileMarksSilentEcusAndScannedRanges()
        {
            var catalog = TestData.Catalog();
            // the addresses from public lists that have not answered this car
            Assert.Contains(catalog.Ecus, e => e.Answered == false);
            Assert.DoesNotContain(catalog.Ecus, e => e.Answered == false && e.Discovered.Any(d => d.Samples is { Count: > 0 }));
            Assert.All(catalog.Ecus.SelectMany(e => e.Scanned), s => Assert.True(s.IsValid));
            Assert.Contains(catalog.Ecu("bms").Scanned, s => s.IsFull && s.From == 0xD000);
            // the ECU searches, by request address
            Assert.Contains(catalog.EcuSearch, s => s.From == 0x700 && s.To == 0x7DF);
        }

        [Fact]
        public void MergeOfANewValueDropsTheRareMark()
        {
            var catalogPath = TestData.CatalogCopy();
            var catalog = Catalog.Load(catalogPath);
            var rare = catalog.Discovered.First(d => d.Rare && d.Samples is { Count: 1 } && !catalog.Identifying.Contains(d.Did));
            var run = TempFile("scan.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            string other = rare.Samples![0].Hex == "00" ? "01" : "00";
            File.WriteAllLines(run, new[]
            {
                // the value it has: still rare
                $$"""{"ts":"2026-10-10T10:00:00Z","ecu":"{{rare.Ecu}}","did":"{{rare.DidHex}}","kind":"positive","hex":"{{rare.Samples[0].Hex}}"}"""
            });
            CatalogMerge.Merge(catalog, new[] { run });
            Assert.True(Catalog.Load(catalogPath).Discovered.Single(d => d.Ecu == rare.Ecu && d.Did == rare.Did).Rare);

            File.WriteAllLines(run, new[]
            {
                $$"""{"ts":"2026-10-10T11:00:00Z","ecu":"{{rare.Ecu}}","did":"{{rare.DidHex}}","kind":"positive","hex":"{{other}}"}"""
            });
            CatalogMerge.Merge(Catalog.Load(catalogPath), new[] { run });
            Assert.False(Catalog.Load(catalogPath).Discovered.Single(d => d.Ecu == rare.Ecu && d.Did == rare.Did).Rare);
        }

        [Fact]
        public void ChangeMeasuresCountersAndArrays()
        {
            var counter = new Snapshot.Change(
                new Snapshot.Entry("bms", "0xD873", null, "positive", "0001484C", null),
                new Snapshot.Entry("bms", "0xD873", null, "positive", "000148B0", null));
            Assert.Equal(100, counter.Delta);

            var array = new Snapshot.Change(
                new Snapshot.Entry("bms", "0xD440", null, "positive", "0E2D0E2E0E30", null),
                new Snapshot.Entry("bms", "0xD440", null, "positive", "0E500E2E0E52", null));
            Assert.Null(array.Delta);
            Assert.Equal(2, array.BytesChanged);

            var gone = new Snapshot.Change(new Snapshot.Entry("vcu", "0xD402", "speed", "positive", "0000", null), null);
            Assert.Equal("positive -> missing", gone.Describe());
        }

        [Fact]
        public void MergeAddsNewAndSkipsCatalogued()
        {
            var catalogPath = TestData.CatalogCopy();
            var catalog = Catalog.Load(catalogPath);
            int before = catalog.Discovered.Count;

            var run = TempFile("scan.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            File.WriteAllLines(run, new[]
            {
                """{"ts":"2026-09-26T10:00:00Z","ecu":"bms","did":"0xD999","kind":"positive","len":2,"hex":"0102"}""",
                """{"ts":"2026-09-26T10:01:00Z","ecu":"bms","did":"0xD999","kind":"positive","len":2,"hex":"0103"}""",
                """{"ts":"2026-09-26T10:01:00Z","ecu":"bms","did":"0xD815","kind":"positive","len":2,"hex":"1800"}""",
                "not json at all"
            });

            var r = CatalogMerge.Merge(catalog, new[] { run });

            Assert.Equal(new CatalogMerge.Result(1, 1, 1), r);
            var reloaded = Catalog.Load(catalogPath);
            Assert.Equal(before + 1, reloaded.Discovered.Count);
            Assert.Contains("Ulkolämpötila", File.ReadAllText(catalogPath));   // not escaped
        }

        [Fact]
        public void EcuSearchFindingsAreAddedOnceAndCanThenTakeIdentifiers()
        {
            var catalogPath = TestData.CatalogCopy();
            var catalog = Catalog.Load(catalogPath);
            int before = catalog.Ecus.Count;

            // one new ECU, and one the file already has (the BMS)
            int added = CatalogMerge.AddEcus(catalog, new[] { (0x6C1, 0x6A1), (catalog.Ecu("bms").Request, catalog.Ecu("bms").Response) },
                new DateTime(2026, 10, 8));

            Assert.Equal(1, added);
            var reloaded = Catalog.Load(catalogPath);
            Assert.Equal(before + 1, reloaded.Ecus.Count);
            var ecu = reloaded.Ecus.Single(e => e.Request == 0x6C1);
            Assert.Equal(0x6A1, ecu.Response);
            Assert.Contains("0x6C1", ecu.Name);
            Assert.Contains("\"user_changes\": true", File.ReadAllText(catalogPath));

            // a second time adds nothing, nor one on a reply ID the file uses
            Assert.Equal(0, CatalogMerge.AddEcus(reloaded, new[] { (0x6C1, 0x6A1), (0x6C2, reloaded.Ecu("bms").Response) }, DateTime.Now));

            // its scan can now be merged
            var run = TempFile("scan.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            File.WriteAllLines(run, new[] { $$"""{"ts":"2026-10-08T10:00:00Z","ecu":"{{ecu.Id}}","did":"0xD401","kind":"positive","len":1,"hex":"07"}""" });
            Assert.Equal(new CatalogMerge.Result(1, 0, 0), CatalogMerge.Merge(reloaded, new[] { run }));
            var found = Assert.Single(Catalog.Load(catalogPath).Ecus.Single(e => e.Request == 0x6C1).Discovered);
            Assert.Equal("own-scan", found.Source);
            Assert.Equal("own-snaps", CatalogMerge.OwnSource(@"C:\x\snap_20261008_101500.jsonl"));
            Assert.Equal("own-watch", CatalogMerge.OwnSource("watch_vcu_20261007_190126.jsonl"));
        }

        [Fact]
        public void MergeNeverStoresSerialNumbersOrVin()
        {
            var catalogPath = TestData.CatalogCopy();
            var catalog = Catalog.Load(catalogPath);

            var run = TempFile("scan.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            File.WriteAllLines(run, new[]
            {
                """{"ecu":"ecu6b5","did":"0xD901","kind":"positive","len":6,"hex":"393837363534"}""",
                """{"ecu":"ecu6b5","did":"0xF190","kind":"positive","len":6,"hex":"565858585858"}"""
            });

            var r = CatalogMerge.Merge(catalog, new[] { run });

            // the ECU is recorded as answering them, without the value
            Assert.Equal(new CatalogMerge.Result(2, 0, 0), r);
            Assert.DoesNotContain("393837363534", File.ReadAllText(catalogPath));
            Assert.DoesNotContain("565858585858", File.ReadAllText(catalogPath));
            var ecu = Catalog.Load(catalogPath).Ecu("ecu6b5");
            Assert.All(ecu.Discovered.Where(d => Catalog.Load(catalogPath).Identifying.Contains(d.Did)), d => Assert.Empty(d.Samples ?? new()));

            // a second scan adds nothing
            Assert.Equal(new CatalogMerge.Result(0, 0, 2), CatalogMerge.Merge(Catalog.Load(catalogPath), new[] { run }));
        }

        [Fact]
        public void MergeKeepsTheFirstSampleAndTheNewest()
        {
            // a watch log of a counter: a new value every line
            var catalogPath = TestData.CatalogCopy();
            var run = TempFile("watch_bms.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            int count = CatalogMerge.MaxSamples + 8;
            File.WriteAllLines(run, Enumerable.Range(1, count).Select(i =>
                $$"""{"ts":"2026-10-10T10:{{i:D2}}:00Z","ecu":"bms","did":"0xD9F0","kind":"positive","hex":"{{i:X4}}"}"""));

            var r = CatalogMerge.Merge(Catalog.Load(catalogPath), new[] { run });

            Assert.Equal(new CatalogMerge.Result(1, count - 1, 0), r);
            var entry = Catalog.Load(catalogPath).Discovered.Single(d => d.Ecu == "bms" && d.Did == 0xD9F0);
            Assert.Equal(CatalogMerge.MaxSamples, entry.Samples!.Count);
            Assert.Equal("0001", entry.Samples[0].Hex);
            Assert.Equal($"{count:X4}", entry.Samples[^1].Hex);
            Assert.Equal($"{count - CatalogMerge.MaxSamples + 2:X4}", entry.Samples[1].Hex);
            Assert.Equal("own-watch", entry.Source);
        }

        [Fact]
        public void MergeClearsStoredValuesOfIdentifyingIdentifiers()
        {
            // a copy where an identifying identifier still holds a value, from before it was listed as one
            var catalogPath = TestData.CatalogCopy();
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
            var bms = root["ecus"]!.AsArray().Single(e => (string?)e!["id"] == "bms")!;
            bms["discovered"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
                """{ "did": "0xF18C", "samples": [ { "ts": "t1", "hex": "5A5A5A5A5A5A5A5A" } ] }"""));
            CatalogMerge.Write(root, catalogPath);
            var run = TempFile("scan.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            File.WriteAllText(run, "");

            var r = CatalogMerge.Merge(Catalog.Load(catalogPath), new[] { run });

            Assert.Equal(new CatalogMerge.Result(0, 0, 0, Cleared: true), r);
            Assert.DoesNotContain("5A5A5A5A5A5A5A5A", File.ReadAllText(catalogPath));
            Assert.Contains(Catalog.Load(catalogPath).Ecu("bms").Discovered, d => d.Did == 0xF18C);

            // nothing left to clear: not written again
            var written = File.GetLastWriteTimeUtc(catalogPath);
            File.SetLastWriteTimeUtc(catalogPath, written.AddMinutes(-5));
            Assert.Equal(new CatalogMerge.Result(0, 0, 0), CatalogMerge.Merge(Catalog.Load(catalogPath), new[] { run }));
            Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(catalogPath));
        }

        [Fact]
        public void MergeClearsAStoredValueWhenTheSameIdentifierComesAgain()
        {
            var catalogPath = TestData.CatalogCopy();
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
            var ecu = root["ecus"]!.AsArray().Single(e => (string?)e!["id"] == "ecu6b5")!;
            ecu["discovered"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
                """{ "did": "0xD901", "samples": [ { "ts": "t1", "hex": "393837363534" } ] }"""));
            CatalogMerge.Write(root, catalogPath);
            var run = TempFile("scan.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(run)!);
            File.WriteAllText(run, """{"ecu":"ecu6b5","did":"0xD901","kind":"positive","hex":"313233343536"}""");

            var r = CatalogMerge.Merge(Catalog.Load(catalogPath), new[] { run });

            Assert.True(r.Cleared);
            Assert.Equal(0, r.Added + r.Updated);
            var text = File.ReadAllText(catalogPath);
            Assert.DoesNotContain("393837363534", text);
            Assert.DoesNotContain("313233343536", text);
        }

        [Fact]
        public void FileArgumentsAreFoundInTheResultFolders()
        {
            string old = Results.Root;
            try
            {
                Results.Root = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
                string a = Results.NewFile(Results.Scans, "scan_bms");
                File.WriteAllText(a, "");
                string b = Results.NewFile(Results.Scans, "scan_bms");   // same second: must not reuse the name
                File.WriteAllText(b, "");

                Assert.NotEqual(a, b);
                Assert.Equal(new[] { a }, Results.Resolve(new[] { Path.GetFileName(a) }, Results.Scans));
                Assert.Equal(2, Results.Resolve(new[] { "scan_*.jsonl" }, Results.Scans).Count);
            }
            finally
            {
                Results.Root = old;
            }
        }
    }
}
