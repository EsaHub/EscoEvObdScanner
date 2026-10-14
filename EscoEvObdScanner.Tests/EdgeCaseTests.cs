namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// Edge cases that would otherwise give a silently wrong answer: late or
    /// broken replies, adapter resets, refused adapter commands, bad catalogue
    /// entries and malformed demo data.
    /// </summary>
    public class EdgeCaseTests
    {
        static UdsResponse Read(string reply, int did)
        {
            var link = new ScriptedLink().On($"22{did:X4}", reply);
            return new Scanner(link).ReadDid(did);
        }

        [Fact]
        public void LateAnswerForAnotherIdentifierIsNotGluedOn()
        {
            // the previous identifier's late answer, then ours: never "ABCD62D410..."
            var r = Read("62D402ABCD|62D4108996", 0xD410);
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
        }

        [Fact]
        public void LateNegativeDoesNotHideTheRealAnswer()
        {
            var r = Read("7F2231|62D4108996", 0xD410);
            Assert.Equal(ResponseKind.Positive, r.Kind);
        }

        [Fact]
        public void PendingBetweenTwoAnswersPicksOurs()
        {
            var r = Read("62D402ABCD|7F2278|62D4108996", 0xD410);
            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
        }

        [Fact]
        public void LateSingleFrameBeforeMultiFrameDoesNotShiftBytes()
        {
            var r = Read("62D402ABCD|00F|0:62F18C313233|1:34353637383930|2:3132", 0xF18C);
            Assert.Equal("123456789012", Bytes.Ascii(r.Data));
        }

        [Fact]
        public void GarbledMessageNextToALateAnswerIsNotTakenForSilence()
        {
            // a late answer means a slow ECU: the request is asked again with a longer wait,
            // and a garbled message next to it (which may have been ours) is not taken for silence
            UdsResponse Twice(string first, string second) =>
                new Scanner(new ScriptedLink().On("22D410", first).On("22D410", second)).ReadDid(0xD410);
            Assert.Equal(ResponseKind.AdapterError, Twice("62D402ABCD|62D41089G6", "62D402ABCD|62D41089G6").Kind);
            Assert.Equal(ResponseKind.AdapterError, Twice("62D4108|62D402ABCD", "62D4108|62D402ABCD").Kind);
            Assert.Equal(ResponseKind.NoData, Twice("62D402ABCD", "NO DATA").Kind);
            Assert.Equal(new byte[] { 0x89, 0x96 }, Twice("62D402ABCD|62D41089G6", "62D4108996").Data);
        }

        [Fact]
        public void GarbledMessageDoesNotHideOurAnswer()
        {
            var r = Read("62D41|62D4108996", 0xD410);
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
        }

        [Fact]
        public void MultiFrameMissingAFrameIsRejected()
        {
            // declared 0x14 = 20 bytes, only 13 arrived
            var r = Read("014|0:62F195413042|1:4D554330353953", 0xF195);
            Assert.Equal(ResponseKind.AdapterError, r.Kind);
        }

        [Fact]
        public void MultiFrameOutOfOrderIsRejected()
        {
            var r = Read("014|0:62F195413042|2:37344356323000|1:4D554330353953", 0xF195);
            Assert.Equal(ResponseKind.AdapterError, r.Kind);
        }

        [Fact]
        public void OddDigitCountIsRejected()
        {
            // one character lost on the serial line
            Assert.Equal(ResponseKind.AdapterError, Read("62D410899", 0xD410).Kind);
        }

        [Fact]
        public void AdapterResetIsNoticedAndSetupRepeated()
        {
            var link = new ScriptedLink().On("22D410", "ELM327 v1.4b").On("22D410", "62D4108996");
            var scanner = new Scanner(link);

            // the read that caught the reset is repeated after the setup has run again
            Assert.Equal(ResponseKind.Positive, scanner.ReadDid(0xD410).Kind);
            Assert.Contains("ATZ", link.Sent);
        }

        [Fact]
        public void RefusedHeaderStopsInsteadOfReadingTheWrongEcu()
        {
            var link = new ScriptedLink().On("ATSH6B4", "?");
            Assert.Throws<AdapterException>(() => new Scanner(link).Target(0x6B4, 0x694));
        }

        [Theory]
        [InlineData("ATS0")]
        [InlineData("ATCAF1")]
        [InlineData("ATSP6")]
        public void RefusedSetupCommandIsTheAdaptersProblem(string command)
        {
            var link = new ScriptedLink().On(command, "?");
            var ex = Assert.Throws<AdapterException>(() => new Scanner(link).Init());
            Assert.Equal(AdapterProblem.SetupRefused, ex.Problem);
            Assert.Equal(command, ex.Command);
        }

        [Fact]
        public void EchoThatStaysOnStopsTheSetup()
        {
            // ATE0 is tried twice; the echo stays
            var link = new ScriptedLink().On("ATL0", "ATL0|OK").On("ATL0", "ATL0|OK");
            var ex = Assert.Throws<AdapterException>(() => new Scanner(link).Init());
            Assert.Equal(AdapterProblem.SetupRefused, ex.Problem);
            Assert.Equal(2, link.Sent.Count(c => c == "ATE0"));
        }

        [Fact]
        public void EchoThatGoesOffOnTheSecondTryIsFine()
        {
            var link = new ScriptedLink().On("ATL0", "ATL0|OK").On("ATL0", "OK");
            new Scanner(link).Init();
            Assert.Equal(new[] { "ATE0", "ATL0", "ATE0", "ATL0", "ATS0" }, link.Sent.SkipWhile(c => c != "ATE0").Take(5));
        }

        [Fact]
        public void RefusedSetupDuringRecoveryIsNotTakenForOneEcu()
        {
            // the adapter reset mid-read and will not switch spaces off again
            var link = new ScriptedLink().On("22D410", "ELM327 v1.4b").On("ATS0", "OK").On("ATS0", "?");
            var scanner = new Scanner(link);
            scanner.Init();
            scanner.Target(0x6B4, 0x694);

            var ex = Assert.Throws<AdapterException>(() => scanner.ReadDid(0xD410));
            Assert.Equal(AdapterProblem.SetupRefused, ex.Problem);
        }

        [Fact]
        public void RefusedSetupStopsAScanInsteadOfSkippingTheEcu()
        {
            var catalog = TestData.Catalog();
            var link = new ScriptedLink().On("22D800", "ELM327 v1.4b").On("ATS0", "?");
            var ex = Assert.Throws<AdapterException>(() =>
                DidScan.Run(new Scanner(link), catalog, catalog.Ecu("bms"), 0xD800, 0xD8FF, TextWriter.Null));
            Assert.Equal(AdapterProblem.SetupRefused, ex.Problem);
        }

        [Fact]
        public void RefusedSetupStopsTheIdentityCheck()
        {
            // one ECU that cannot be addressed is skipped; an adapter that cannot be set up is not
            var link = new ScriptedLink().On("22F18C", "ELM327 v1.4b").On("ATS0", "?");
            var ex = Assert.Throws<AdapterException>(() => VehicleIdentity.Check(new Scanner(link), TestData.Catalog()));
            Assert.Equal(AdapterProblem.SetupRefused, ex.Problem);
        }

        [Fact]
        public void MissingFlowControlCommandsAreTolerated()
        {
            // cheap clones answer "?" to the flow-control commands; targeting still succeeds
            var link = new ScriptedLink().On("ATFCSH6B4", "?").On("ATFCSD300000", "?").On("ATFCSM1", "?");
            new Scanner(link).Target(0x6B4, 0x694);
        }

        [Fact]
        public void TimeoutIsRestoredEvenIfTheRetryFails()
        {
            var link = new ThrowingOnSecondRequest();
            var scanner = new Scanner(link);
            Assert.Throws<IOException>(() => scanner.ReadDid(0xD410));
            Assert.Equal("ATST20", link.Sent.Last());
        }

        sealed class ThrowingOnSecondRequest : IElmLink
        {
            int _requests;
            public List<string> Sent { get; } = new();
            public event Action<string>? Trace { add { } remove { } }
            public bool IsOpen => true;
            public void Open() { }
            public void Dispose() { }

            public string[] Send(string command, int timeoutMs = 5000)
            {
                Sent.Add(command);
                if (command.StartsWith("AT")) return new[] { "OK" };
                if (++_requests == 2) throw new IOException("port gone");
                return new[] { "7F2278" };
            }
        }

        [Fact]
        public void DiscoverySkipsBroadcastAndReplyIds()
        {
            var (car, scanner, _) = TestData.DemoCar();

            scanner.DiscoverEcus(0x7DF, 0x7DF);
            Assert.Empty(car.Requests);          // 0x7DF: nothing sent
            scanner.DiscoverEcus(0x7E8, 0x7EF);
            Assert.Empty(car.Requests);          // standard reply IDs: nothing sent
            scanner.DiscoverEcus(0x7D5, 0x7D5, skip: new[] { 0x7D5 });
            Assert.Empty(car.Requests);          // caller's skip list honoured
            scanner.DiscoverEcus(0x7D0, 0x7D0);
            Assert.NotEmpty(car.Requests);       // anything else is probed
        }

        [Fact]
        public void BadCatalogueEntryDoesNotBreakLookups()
        {
            var path = TestData.CatalogCopy();
            var text = File.ReadAllText(path).Replace("\"did\": \"0xD434\"", "\"did\": \"not hex\"");
            File.WriteAllText(path, text);

            var catalog = Catalog.Load(path);
            Assert.NotNull(catalog.Find("bms", 0xD410));                     // other entries still found
            Assert.DoesNotContain(Snapshot.Targets(catalog), d => d.Did < 0);
        }

        [Fact]
        public void DivisionByZeroByteIsNull()
        {
            Assert.Null(Expr.Evaluate("100/B4", new byte[] { 0 }));
        }

        [Fact]
        public void MergeLeavesNoTempFile()
        {
            var path = TestData.CatalogCopy();
            var run = Path.Combine(Path.GetDirectoryName(path)!, "run.jsonl");
            File.WriteAllText(run, """{"ecu":"bms","did":"0xD998","kind":"positive","len":1,"hex":"01"}""");

            CatalogMerge.Merge(Catalog.Load(path), new[] { run });

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Contains("0xD998", File.ReadAllText(path));
        }

        [Fact]
        public void SimulatedCarSkipsMalformedLines()
        {
            var car = new SimulatedCar(TestData.Catalog(), new[]
            {
                "not json",
                """{"ecu":"bms","did":42,"hex":"01"}""",
                """{"ecu":"bms","did":"0xD410","hex":"XYZ"}""",
                """{"ecu":"bms","did":"0xD410","hex":"8996"}"""
            });
            car.Open();
            var scanner = new Scanner(car);
            scanner.Target(0x6B4, 0x694);
            Assert.Equal(new byte[] { 0x89, 0x96 }, scanner.ReadDid(0xD410).Data);
        }
    }
}
