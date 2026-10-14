namespace EscoEvObdScanner.Tests
{
    /// <summary>The scan, fault and known-value loops shared by the CLI and the app.</summary>
    public class ReaderTests
    {
        [Fact]
        public void ScanFindsRecordedIdentifiers()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var output = new StringWriter();
            var hits = new List<DidScan.Hit>();

            var r = DidScan.Run(scanner, catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, output, hits.Add);

            Assert.Equal(DidScan.Outcome.Completed, r.Outcome);
            Assert.Equal(43, r.Hits);
            Assert.Equal(43, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains(hits, h => h.Did == 0xD815 && h.Known?.EnglishName == "Pack voltage");
        }

        [Fact]
        public void ScanStopsWhenEcuGoesSilent()
        {
            var (_, scanner, _) = TestData.DemoCar();
            var ghost = new EcuDef { Id = "ghost", RequestHex = "0x700", ResponseHex = "0x708" };

            var r = DidScan.Run(scanner, TestData.Catalog(), ghost, 0x0000, 0x0FFF, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.Silent, r.Outcome);
            Assert.Equal(DidScan.MaxSilent - 1, r.LastDid);
            Assert.Equal(0, r.ResumeFrom);
        }

        [Fact]
        public void ScanStopsWhenServiceIsRefused()
        {
            var link = new ScriptedLink().On("22D800", "7F2211");
            var catalog = TestData.Catalog();

            var r = DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.ServiceRefused, r.Outcome);
            Assert.Equal(0x11, r.RefusalNrc);
        }

        [Fact]
        public void ScanCanBeCancelled()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            using var cts = new CancellationTokenSource();
            var r = DidScan.Run(scanner, catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null,
                progress: did => { if (did == 0xD810) cts.Cancel(); }, ct: cts.Token);
            Assert.Equal(DidScan.Outcome.Cancelled, r.Outcome);
        }

        /// <summary>A link on which every read in <paramref name="from"/>..<paramref name="to"/> comes back as a CAN error.</summary>
        static ScriptedLink CanErrors(int from, int to)
        {
            var link = new ScriptedLink();
            for (int did = from; did <= to; did++) link.On($"22{did:X4}", "CAN ERROR");
            return link;
        }

        [Fact]
        public void ScanTargetsTheEcuAgainAfterABurstOfErrors()
        {
            // errors for a while, then answers again
            int last = 0xD800 + DidScan.ErrorsBeforeRetarget + 4;
            var link = CanErrors(0xD800, last);
            var catalog = TestData.Catalog();

            var r = DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, last + 10, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.Completed, r.Outcome);
            // at the start, then after the burst
            Assert.Equal(2, link.Sent.Count(c => c == "ATSH6B4"));
            int retarget = link.Sent.IndexOf("ATSH6B4", link.Sent.IndexOf("ATSH6B4") + 1);
            Assert.Equal($"22{0xD800 + DidScan.ErrorsBeforeRetarget - 1:X4}", link.Sent.Take(retarget).Last(c => c.StartsWith("22")));
        }

        [Fact]
        public void ScanOfANoLongerAnsweringBusStopsWithWhereToResume()
        {
            // a car that went to sleep mid-scan can answer every read with a bus error instead of nothing
            var link = CanErrors(0xD800, 0xD8FF);
            var catalog = TestData.Catalog();

            var r = DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.Silent, r.Outcome);
            Assert.Equal(0xD800 + DidScan.MaxSilent - 1, r.LastDid);
            Assert.Equal(0xD800, r.ResumeFrom);
        }

        [Fact]
        public void ScanStopsWhenTheEcuCannotBeAddressed()
        {
            var link = new ScriptedLink().On("ATSH6B4", "?");
            var catalog = TestData.Catalog();

            var r = DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.AdapterRefused, r.Outcome);
            Assert.Equal(0xD800, r.LastDid);
            Assert.DoesNotContain(link.Sent, c => c.StartsWith("22"));
        }

        [Fact]
        public void ScanStopsWhenTheEcuCannotBeAddressedAgain()
        {
            var link = CanErrors(0xD800, 0xD8FF).On("ATSH6B4", "OK").On("ATSH6B4", "?");
            var catalog = TestData.Catalog();

            var r = DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.AdapterRefused, r.Outcome);
            Assert.Equal(0xD800 + DidScan.ErrorsBeforeRetarget - 1, r.LastDid);
        }

        [Fact]
        public void ScanStopsWhenTheAdapterWillNotBePointedBackAfterAReset()
        {
            var link = new ScriptedLink()
                .On("22D805", "ELM327 v1.4b")
                .On("ATSH6B4", "OK")
                .On("ATSH6B4", "?");
            var catalog = TestData.Catalog();

            var r = DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null);

            Assert.Equal(DidScan.Outcome.AdapterRefused, r.Outcome);
            Assert.Equal(0xD805, r.LastDid);
        }

        [Fact]
        public void ScanOfAnEcuWithABadAddressIsRefused()
        {
            var bad = new EcuDef { Id = "bad", RequestHex = "0x800", ResponseHex = "0x808" };
            var r = DidScan.Run(new Scanner(new ScriptedLink()), TestData.Catalog(), bad, 0xD800, 0xD8FF, TextWriter.Null);
            Assert.Equal(DidScan.Outcome.AdapterRefused, r.Outcome);
        }

        [Fact]
        public void ClockIsReadFromTheEcuTheCarFileNames()
        {
            var (car, scanner, catalog) = TestData.DemoCar();

            var clock = DtcReader.ReadClock(scanner, catalog);

            Assert.NotNull(clock);
            Assert.Equal(0x229E1E11, clock!.Value.Value);              // the VCU's 0xD4CA in the demo data
            Assert.Equal("22D4CA", car.Requests.Last());
            Assert.True((DateTime.UtcNow - clock.Value.Utc).Duration() < TimeSpan.FromMinutes(1));
        }

        [Fact]
        public void ClockIsNullWhenTheEcuDoesNotGiveIt()
        {
            var catalog = TestData.Catalog();
            Assert.Null(DtcReader.ReadClock(new Scanner(new ScriptedLink()), catalog));
            Assert.Null(DtcReader.ReadClock(new Scanner(new ScriptedLink().On("22D4CA", "7F2231")), catalog));
            // more than eight bytes is no clock
            Assert.Null(DtcReader.ReadClock(new Scanner(new ScriptedLink().On("22D4CA", "00D|0:62D4CA010203|1:04050607080900")), catalog));
        }

        [Fact]
        public void ClockIsNotReadWhenTheCarFileHasNone()
        {
            var catalog = Catalog.Load(Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName, "kia-eniro.json"));
            Assert.Null(catalog.Platform.FaultTime?.Clock);
            var link = new ScriptedLink();

            Assert.Null(DtcReader.ReadClock(new Scanner(link), catalog));
            Assert.Empty(link.Sent);
        }

        [Fact]
        public void FaultsPerEcu()
        {
            var (_, scanner, catalog) = TestData.DemoCar();

            var vcu = DtcReader.Read(scanner, catalog.Ecu("vcu"), deep: false);
            var obc = DtcReader.Read(scanner, catalog.Ecu("obc"), deep: true);

            Assert.True(vcu.Answered);
            Assert.Empty(vcu.Faults);
            var p1c00 = Assert.Single(obc.Faults);
            Assert.Equal("P1C00", p1c00.Dtc.Code);
            Assert.Contains("\"severity\":\"stored\"", DtcReader.ToJson(obc.Ecu, p1c00));
            // the freeze frame gives the odometer at the time of the fault
            var frame = DtcSnapshot.Decode(p1c00.Snapshot, null, catalog.Platform.FaultTime)!;
            Assert.Equal(59340, frame.Km);
            Assert.NotNull(frame.Clock);

            var (_, silentScanner, _) = TestData.DemoCarWithout("obc");
            var silent = DtcReader.Read(silentScanner, catalog.Ecu("obc"), deep: false);
            Assert.False(silent.Answered);
            Assert.Equal("no-data", silent.Problem);
        }

        [Fact]
        public void KnownValuesDecode()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var readings = KnownValues.Read(scanner, catalog);

            Assert.Equal(catalog.Dids.Count, readings.Count);
            var soc = readings.Single(r => r.Def.Did == 0xD410);
            Assert.Equal(80.79, soc.Value!.Value, 2);
            var sw = readings.Single(r => r.Def.Did == 0xF195);
            Assert.Equal("A0BMUC059S74CV20", sw.Text);
            var vin = readings.Single(r => r.Def.Did == 0xF190);
            Assert.Equal("requestOutOfRange", vin.Problem);
        }
    }
}
