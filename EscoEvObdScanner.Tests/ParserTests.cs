namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// Response parsing against exact adapter output. The multi-frame and
    /// negative replies are copied from a real Mokka-e trace.
    /// </summary>
    public class ParserTests
    {
        static UdsResponse Read(string reply, int did = 0xF18C)
        {
            var link = new ScriptedLink().On($"22{did:X4}", reply);
            return new Scanner(link).ReadDid(did);
        }

        [Fact]
        public void SingleFrame()
        {
            var r = Read("62D4108996||", 0xD410);
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
        }

        [Fact]
        public void MultiFrameFromRealCar()
        {
            var r = Read("00F|0:62F18C313233|1:34353637383930|2:3132||");
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal("123456789012", Bytes.Ascii(r.Data));
        }

        [Fact]
        public void MultiFrameIsCutToDeclaredLength()
        {
            // 0x13 = 19 bytes declared; the frames carry 20, so the last byte (00) is padding and must go
            var r = Read("013|0:62F195413042|1:4D554330353953|2:37344356323000||", 0xF195);
            Assert.Equal(16, r.Data.Length);
            Assert.Equal("A0BMUC059S74CV20", Bytes.Ascii(r.Data));
        }

        [Fact]
        public void NegativeResponse()
        {
            var r = Read("7F2231||", 0xF190);
            Assert.Equal(ResponseKind.Negative, r.Kind);
            Assert.Equal(0x31, r.Nrc);
            Assert.False(r.Exists);
        }

        [Theory]
        [InlineData(0x33, true)]    // locked, but there
        [InlineData(0x22, true)]    // there, not available right now
        [InlineData(0x31, false)]   // not there
        [InlineData(0x11, false)]   // service-level, says nothing about the identifier
        [InlineData(0x7F, false)]
        public void ExistsFollowsNrc(byte nrc, bool exists)
        {
            Assert.Equal(exists, Read($"7F22{nrc:X2}", 0xD400).Exists);
        }

        [Fact]
        public void NoData() => Assert.Equal(ResponseKind.NoData, Read("NO DATA").Kind);

        [Fact]
        public void EmptyReplyIsNoData() => Assert.Equal(ResponseKind.NoData, Read("").Kind);

        [Theory]
        [InlineData("?")]
        [InlineData("CAN ERROR")]
        [InlineData("BUFFER FULL")]
        public void AdapterErrors(string reply) => Assert.Equal(ResponseKind.AdapterError, Read(reply).Kind);

        [Fact]
        public void SearchingIsNotMistakenForHex()
        {
            // S, E, A, C and H are hex digits: SEARCHING... must be skipped, not absorbed.
            // It also means the adapter lost its ATSP6 setting, so the setup is
            // repeated and the read asked again.
            var link = new ScriptedLink().On("22D410", "SEARCHING...|62D4108996").On("22D410", "62D4108996");
            var r = new Scanner(link).ReadDid(0xD410);
            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
            Assert.Contains("ATZ", link.Sent);
        }

        [Fact]
        public void ASlowEcuGetsALongerWaitAndIsAskedAgain()
        {
            // the late answer to 0xD401 lands in the window of 0xD402: wait longer and ask 0xD402 again
            var link = new ScriptedLink().On("22D402", "62D40101").On("22D402", "62D40202");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            var r = scanner.ReadDid(0xD402);
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(new byte[] { 0x02 }, r.Data);
            Assert.Contains("ATST64", link.Sent);
            // another ECU starts at the default wait; the slow one gets its longer wait back when addressed again
            scanner.Target(0x6A2, 0x682);
            Assert.Equal("ATST20", link.Sent.Last(c => c.StartsWith("ATST")));
            scanner.Target(0x6B4, 0x694);
            Assert.Equal("ATST64", link.Sent.Last(c => c.StartsWith("ATST")));
            // an ECU search probes at the default wait
            scanner.DiscoverEcus(0x700, 0x701);
            Assert.Contains("ATST20", link.Sent.SkipWhile(c => c != "ATST64").Skip(1));
        }

        [Fact]
        public void OneByteTesterPresentReplyIsPositive()
        {
            var link = new ScriptedLink().On("3E00", "7E00");
            var r = new Scanner(link).Request("3E00", 0x3E);
            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(new byte[] { 0x00 }, r.Data);
        }

        [Fact]
        public void EchoOfAnotherIdentifierIsRejected()
        {
            // a late answer to the previous request must not be filed under this one: this one got no answer
            var r = Read("62D4118996", 0xD410);
            Assert.Equal(ResponseKind.NoData, r.Kind);
        }

        [Fact]
        public void PendingThenAnswerInTheSameReply()
        {
            var r = Read("7F2278|62D4108996", 0xD410);
            Assert.Equal(ResponseKind.Positive, r.Kind);
        }

        [Fact]
        public void PendingOnlyIsRetriedWithLongerTimeout()
        {
            var link = new ScriptedLink()
                .On("22D410", "7F2278")
                .On("22D410", "7F2278|62D4108996");
            var r = new Scanner(link).ReadDid(0xD410);

            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Contains("ATSTFF", link.Sent);
            Assert.Equal("ATST20", link.Sent.Last());   // timeout restored afterwards
        }

        [Fact]
        public void BusyIsAskedAgain()
        {
            var link = new ScriptedLink().On("22D410", "7F2221").On("22D410", "62D4108996");
            var r = new Scanner(link).ReadDid(0xD410);

            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(new byte[] { 0x89, 0x96 }, r.Data);
            Assert.Equal(2, link.Sent.Count(c => c == "22D410"));
        }

        [Fact]
        public void BusyTwiceStaysARefusalThatProvesNothing()
        {
            var link = new ScriptedLink().On("22D410", "7F2221").On("22D410", "7F2221").On("22D410", "62D4108996");
            var r = new Scanner(link).ReadDid(0xD410);

            Assert.Equal(ResponseKind.Negative, r.Kind);
            Assert.Equal(0x21, r.Nrc);
            Assert.False(r.Exists);
            Assert.Equal("negative:busyRepeatRequest", r.KindLabel);
            Assert.Equal(2, link.Sent.Count(c => c == "22D410"));      // asked again once only
        }

        [Fact]
        public void BusyThenPendingWaitsForTheAnswer()
        {
            var link = new ScriptedLink().On("22D410", "7F2221").On("22D410", "7F2278").On("22D410", "7F2278|62D4108996");
            Assert.Equal(ResponseKind.Positive, new Scanner(link).ReadDid(0xD410).Kind);
            Assert.Contains("ATSTFF", link.Sent);
        }

        [Fact]
        public void PendingTwiceBecomesNoData()
        {
            var link = new ScriptedLink().On("22D410", "7F2278").On("22D410", "7F2278");
            Assert.Equal(ResponseKind.NoData, new Scanner(link).ReadDid(0xD410).Kind);
        }
    }
}
