namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// Adapter resets in the middle of work, which reply wins when several arrive,
    /// fault code echo checks, discovery limits, merge input filtering and
    /// partial cell reads.
    /// </summary>
    public class RecoveryTests
    {
        [Fact]
        public void ResetMidScanRestoresTargetAndRetries()
        {
            // the adapter browns out while a read is in flight
            var link = new ScriptedLink()
                .On("22D410", "\u0000ELM327 v1.4b")      // junk byte before the banner
                .On("22D410", "62D4108996");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            link.Sent.Clear();

            var r = scanner.ReadDid(0xD410);

            Assert.Equal(ResponseKind.Positive, r.Kind);                // retried after recovery
            int reset = link.Sent.IndexOf("ATZ");
            int header = link.Sent.IndexOf("ATSH6B4");
            Assert.True(reset >= 0 && header > reset);                  // set up again, then re-targeted
            Assert.Equal("22D410", link.Sent.Last());
        }

        [Fact]
        public void KnownValuesSkipAnEcuTheAdapterWillNotAddressAgainAfterAReset()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "two.json");
            File.WriteAllText(path, """
                {
                  "ecus": [
                    { "id": "a", "request": "0x7E4", "response": "0x7EC", "dids": [ { "did": "0x0101" }, { "did": "0x0103" } ] },
                    { "id": "b", "request": "0x7E2", "response": "0x7EA", "dids": [ { "did": "0x0102" } ] }
                  ]
                }
                """);
            // the adapter resets during the first read of "a" and then refuses its header
            var link = new ScriptedLink()
                .On("220101", "ELM327 v1.4b")
                .On("ATSH7E4", "OK").On("ATSH7E4", "?")
                .On("220102", "6201020A");

            var readings = KnownValues.Read(new Scanner(link), Catalog.Load(path));

            Assert.Equal(new[] { ("b", 0x0102) }, readings.Select(r => (r.Ecu.Id, r.Def.Did)));
            Assert.DoesNotContain("220103", link.Sent);         // the rest of "a" is skipped
        }

        [Fact]
        public void ResetWithoutTargetStillReinitialises()
        {
            var link = new ScriptedLink().On("1902FF", "LV RESET").On("1902FF", "5902FF");
            var r = new Scanner(link).Request("1902FF", 0x19);
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Contains("ATZ", link.Sent);
        }

        [Theory]
        [InlineData("7F2233|7F2231", 0x31)]     // stale "locked" first, ours "not there": no phantom hit
        [InlineData("7F2231|7F2233", 0x33)]     // stale "not there" first, ours "locked": not hidden
        public void LastNegativeWins(string reply, byte nrc)
        {
            var r = new Scanner(new ScriptedLink().On("22D400", reply)).ReadDid(0xD400);
            Assert.Equal(nrc, r.Nrc);
        }

        [Fact]
        public void StaleNegativeBeforeOurPendingStillWaits()
        {
            var link = new ScriptedLink().On("22D410", "7F2231|7F2278").On("22D410", "7F2278|62D4108996");
            Assert.Equal(ResponseKind.Positive, new Scanner(link).ReadDid(0xD410).Kind);
        }

        [Fact]
        public void DtcReplyForAnotherSubFunctionIsIgnored()
        {
            // a late answer to 19 04 must not be read as the answer to 19 06
            var link = new ScriptedLink().On("1906108309FF", "5904108309280101|5906108309280102");
            var r = new Scanner(link).Request("1906108309FF", 0x19);
            Assert.Equal(0x06, r.Data[0]);
        }

        [Fact]
        public void DiscoveryRefusesLowIdsAndReversedRanges()
        {
            var (_, scanner, _) = TestData.DemoCar();
            Assert.Throws<ArgumentOutOfRangeException>(() => scanner.DiscoverEcus(0x100, 0x1FF));
            Assert.Throws<ArgumentOutOfRangeException>(() => scanner.DiscoverEcus(0x6BF, 0x6A0));
        }

        [Fact]
        public void MergeIgnoresUnusableLines()
        {
            var path = TestData.CatalogCopy();
            var dir = Path.GetDirectoryName(path)!;
            var run = Path.Combine(dir, "run.jsonl");
            File.WriteAllLines(run, new[]
            {
                """{"ecu":"bms","did":42,"hex":"01"}""",                 // did is a number
                """{"ecu":"bms","did":"zz","hex":"01"}""",               // not hex
                """{"ecu":"bms","did":"0xD997","hex":7}""",              // hex is a number, no kind
                """{"ecu":"bms","did":"0xD995","kind":"no-data","hex":""}""", // a failed read proves nothing
                """{"ecu":"bms","did":"0xD996","kind":"positive","len":1,"hex":"01"}""",
                """{"ecu":"bms","did":"0xd996","kind":"positive","len":1,"hex":"01"}"""  // same entry, other spelling
            });

            var r = CatalogMerge.Merge(Catalog.Load(path), new[] { run });

            Assert.Equal(1, r.Added);           // D996 only, once
            var text = File.ReadAllText(path);
            Assert.Contains("0xD996", text);
            Assert.DoesNotContain("0xD997", text);
            Assert.DoesNotContain("0xD995", text);
        }

        [Fact]
        public void SpacedHexMeansTheAdapterIsOnDefaults()
        {
            // an adapter that reset while idle prints spaces again (ATS0 lost)
            var link = new ScriptedLink().On("22D410", "62 D4 10 89 96").On("22D410", "62D4108996");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            link.Sent.Clear();

            Assert.Equal(ResponseKind.Positive, scanner.ReadDid(0xD410).Kind);
            Assert.Contains("ATZ", link.Sent);          // set up again
            Assert.Contains("ATSH6B4", link.Sent);      // and pointed back at the same ECU
        }

        [Fact]
        public void SpacedMultiFrameReplyMeansTheAdapterIsOnDefaults()
        {
            // as captured from a Niro EV after the adapter reset while the car slept: echo and spaces back,
            // and a frame lost on the way
            var link = new ScriptedLink()
                .On("220105", "220105|02E |0: 62 01 05 FF FB 74 |1: 0F 01 2C 00 01 2C 0C |3: 1A 47 18 00 00 50 0D")
                .On("220105", "02E|0:620105FFFB74|1:0F015E2001350B|2:0C0B0B0C0C0C28|3:8847180000500E|4:0003E660533800|5:99000000000000|6:000C0C0C0CAAAA");
            var scanner = new Scanner(link);
            scanner.Target(0x7E4, 0x7EC);
            link.Sent.Clear();

            var r = scanner.ReadDid(0x0105);

            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(43, r.Data.Length);             // 0x2E bytes less the reply byte and the identifier
            int reset = link.Sent.IndexOf("ATZ");
            Assert.True(reset >= 0 && link.Sent.IndexOf("ATSH7E4") > reset);
        }

        [Fact]
        public void SpacedMultiFrameReplyIsSeenWithoutTheEcho()
        {
            var link = new ScriptedLink()
                .On("220105", "02E |0: 62 01 05 FF FB 74 |1: 0F 01 2C 00 01 2C 0C")
                .On("220105", "62010500");
            var scanner = new Scanner(link);
            scanner.Target(0x7E4, 0x7EC);
            link.Sent.Clear();

            Assert.Equal(ResponseKind.Positive, scanner.ReadDid(0x0105).Kind);
            Assert.Contains("ATZ", link.Sent);
        }

        [Fact]
        public void EchoWhenTargetingMeansTheAdapterIsOnDefaults()
        {
            // reset while idle; the next ECU switch is the first command since
            var link = new ScriptedLink().On("ATH0", "OK").On("ATH0", "ATH0|OK");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            link.Sent.Clear();

            scanner.Target(0x7E4, 0x7EC);

            int reset = link.Sent.IndexOf("ATZ");
            Assert.True(reset >= 0, string.Join(",", link.Sent));
            Assert.True(link.Sent.IndexOf("ATE0") > reset && link.Sent.LastIndexOf("ATSH7E4") > reset);
        }

        [Fact]
        public void EchoedReplyIsNotTrusted()
        {
            var link = new ScriptedLink().On("22D410", "22D410|62D4108996").On("22D410", "62D4108996");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            link.Sent.Clear();

            Assert.Equal(ResponseKind.Positive, scanner.ReadDid(0xD410).Kind);
            Assert.Contains("ATZ", link.Sent);
            Assert.Equal(2, link.Sent.Count(c => c == "22D410"));     // asked again after the setup
        }

        [Fact]
        public void DiscoveryNoticesAnAdapterThatResetWhileIdle()
        {
            // the first probe comes back echoed: the adapter is on its defaults, the wide filter gone
            var link = new ScriptedLink().On("3E00", "3E00|NO DATA");
            new Scanner(link).DiscoverEcus(0x700, 0x701);
            int reset = link.Sent.IndexOf("ATZ");
            Assert.True(reset >= 0 && link.Sent.IndexOf("ATCF400", reset) > reset, string.Join(",", link.Sent));
        }

        [Fact]
        public void MergeTakesOnlyRepliesThatProveTheIdentifierExists()
        {
            var path = TestData.CatalogCopy();
            var run = Path.Combine(Path.GetDirectoryName(path)!, "refusals.jsonl");
            File.WriteAllLines(run, new[]
            {
                """{"ecu":"bms","did":"0xD9A1","kind":"negative:requestOutOfRange","hex":""}""",   // does not exist
                """{"ecu":"bms","did":"0xD9A2","kind":"negative:serviceNotSupported","hex":""}""",
                """{"ecu":"bms","did":"0xD9A3","kind":"negative:securityAccessDenied","hex":""}""",  // exists, locked
                """{"ecu":"bms","did":"0xD9A4","kind":"negative:conditionsNotCorrect","hex":""}"""   // exists, not now
            });
            var r = CatalogMerge.Merge(Catalog.Load(path), new[] { run });
            Assert.Equal(2, r.Added);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("0xD9A1", text);
            Assert.DoesNotContain("0xD9A2", text);
            Assert.Contains("0xD9A3", text);
        }

        [Fact]
        public void FailedRetargetKeepsTheResetPending()
        {
            var link = new ScriptedLink()
                .On("22D410", "ELM327 v1.4b")
                .On("ATSH6B4", "OK")         // the first Target, before the reset
                .On("ATSH6B4", "?");         // refused during recovery
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);

            Assert.Throws<AdapterException>(() => scanner.ReadDid(0xD410));

            // the next operation still starts with a fresh setup
            link.Sent.Clear();
            scanner.Target(0x6B4, 0x694);
            Assert.Contains("ATZ", link.Sent);
        }

        [Fact]
        public void SetupCutShortIsDoneAgainBeforeTheNextTarget()
        {
            var link = new ScriptedLink().On("ATSP6", "?");
            var scanner = new Scanner(link);
            Assert.Throws<AdapterException>(() => scanner.Init());

            link.Sent.Clear();
            scanner.Target(0x6B4, 0x694);

            int reset = link.Sent.IndexOf("ATZ");
            Assert.True(reset >= 0 && link.Sent.IndexOf("ATSP6") > reset && link.Sent.IndexOf("ATSH6B4") > reset, string.Join(",", link.Sent));
        }

        [Fact]
        public void SetupCutShortIsDoneAgainBeforeTheNextRequest()
        {
            var link = new ScriptedLink().On("ATCAF1", "?").On("1902FF", "5902FF");
            var scanner = new Scanner(link);
            Assert.Throws<AdapterException>(() => scanner.Init());

            link.Sent.Clear();
            Assert.Equal(ResponseKind.Positive, scanner.Request("1902FF", 0x19).Kind);
            Assert.True(link.Sent.IndexOf("ATCAF1") < link.Sent.IndexOf("1902FF"));
        }

        [Fact]
        public void RecoveredScannerDoesNotResetAgain()
        {
            var link = new ScriptedLink().On("22D410", "LV RESET").On("22D410", "62D4108996").On("22D410", "62D4108996");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            scanner.ReadDid(0xD410);

            link.Sent.Clear();
            Assert.Equal(ResponseKind.Positive, scanner.ReadDid(0xD410).Kind);
            Assert.Equal(new[] { "22D410" }, link.Sent);
        }

        [Fact]
        public void ReplyAfterAResetIsAskedAgainOnTheTarget()
        {
            // whatever answered on the default header is never filed under the ECU
            var link = new ScriptedLink().On("22D410", "62D4101111|ELM327 v1.4b").On("22D410", "62D4108996");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);

            var r = scanner.ReadDid(0xD410);

            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
        }

        [Fact]
        public void ResetOnTheRetryTooIsNoAnswer()
        {
            var link = new ScriptedLink().On("22D410", "ELM327 v1.4b").On("22D410", "62 D4 10 89 96");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);

            Assert.Equal(ResponseKind.AdapterError, scanner.ReadDid(0xD410).Kind);

            // and the next read starts with the setup again
            link.Sent.Clear();
            scanner.ReadDid(0xD410);
            Assert.Contains("ATZ", link.Sent);
        }

        [Fact]
        public void PendingAfterASecondResetIsNotAskedAgainOnTheDefaultHeader()
        {
            // reset during the read, and the retry's "pending" comes in the adapter's default format
            var link = new ScriptedLink().On("22D410", "ELM327 v1.4b").On("22D410", "7F 22 78");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            link.Sent.Clear();

            Assert.Equal(ResponseKind.AdapterError, scanner.ReadDid(0xD410).Kind);
            Assert.DoesNotContain("ATSTFF", link.Sent);         // no long-wait retry on the broadcast header
            Assert.Equal(2, link.Sent.Count(c => c == "22D410"));
        }

        [Fact]
        public void DiscoveryStopsAtAnIdWhoseHeaderIsRefusedAfterAReset()
        {
            // the adapter resets on the probe of 0x700 and then refuses its header
            var link = new ScriptedLink()
                .On("ATSH700", "OK").On("ATSH700", "?")
                .On("3E00", "ELM327 v1.4b")
                .On("3E00", "7E8027E00");                       // would be an OBD ECU answering on 7DF
            var scanner = new Scanner(link);

            var found = scanner.DiscoverEcus(0x700, 0x700);

            Assert.Empty(found);
            Assert.Equal(1, link.Sent.Count(c => c == "3E00"));  // nothing more under the default header
            Assert.DoesNotContain("22F186", link.Sent);
        }

        [Fact]
        public void ShortCellReadDoesNotReplaceACompleteOne()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            var poller = new LivePoller(scanner, catalog);
            var first = poller.Poll();

            // next cell read: a glitch after cell 10 stops the decoding there
            var glitched = new byte[240];
            for (int i = 0; i < 10; i++) { glitched[2 * i] = 0x0E; glitched[2 * i + 1] = 0x2D; }
            glitched[20] = 0xFF; glitched[21] = 0xFF;
            car.SetData(0x6B4, 0xD440, glitched);

            VehicleState s = first;
            for (int i = 0; i < LivePoller.CellsEvery; i++) s = poller.Poll();

            Assert.Equal(108, s.CellVoltages!.Length);          // the complete reading kept
            Assert.Equal(first.CellsTime, s.CellsTime);         // and not re-stamped
        }
    }
}
