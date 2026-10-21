namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// What the adapter link makes of the characters read before the prompt:
    /// a reply cut off by the deadline, the command echoed back, blank lines.
    /// </summary>
    public class Elm327Tests
    {
        [Fact]
        public void ReplyBeforeThePromptIsSplitIntoLines()
        {
            var (_, lines, echoed) = Elm327.Reply("00F\r0:62F18C313233\r1:34353637383930\r2:3132\r\r", prompt: true, "22F18C");
            Assert.Equal(new[] { "00F", "0:62F18C313233", "1:34353637383930", "2:3132" }, lines);
            Assert.False(echoed);
        }

        [Fact]
        public void LastLineWithoutThePromptIsKeptWhenItEndsTheReply()
        {
            // the prompt itself is not part of what was read
            Assert.Equal(new[] { "62D4108996" }, Elm327.Reply("62D4108996\r", prompt: true, "22D410").Lines);
            Assert.Equal(new[] { "62D4108996" }, Elm327.Reply("62D4108996", prompt: true, "22D410").Lines);
        }

        [Fact]
        public void LineCutOffByTheDeadlineIsDropped()
        {
            // "62D4" of "62D4108996" would still look like hex
            var (raw, lines, _) = Elm327.Reply("7F2278\r62D4", prompt: false, "22D410");
            Assert.Equal(new[] { "7F2278" }, lines);
            Assert.Equal("7F2278\r", raw);
        }

        [Fact]
        public void ReplyWithNoFinishedLineBeforeTheDeadlineIsEmpty()
        {
            var (raw, lines, echoed) = Elm327.Reply("62D41089", prompt: false, "22D410");
            Assert.Empty(lines);
            Assert.Equal("", raw);
            Assert.False(echoed);
        }

        [Fact]
        public void EchoedCommandIsTakenOffAndReported()
        {
            // an adapter back on its defaults (ATE0 lost) repeats the command first
            var (_, lines, echoed) = Elm327.Reply("22d410\r62D4108996\r\r", prompt: true, "22D410");
            Assert.True(echoed);
            Assert.Equal(new[] { "62D4108996" }, lines);
        }

        [Fact]
        public void CommandTextLaterInTheReplyIsNoEcho()
        {
            var (_, lines, echoed) = Elm327.Reply("OK\rATH0\r", prompt: true, "ATH0");
            Assert.False(echoed);
            Assert.Equal(new[] { "OK" }, lines);
        }

        [Fact]
        public void LinefeedsAndSpacesAroundLinesAreIgnored()
        {
            // ATL0 lost: CR LF line ends
            var (_, lines, echoed) = Elm327.Reply("ATZ\r\n\r\n ELM327 v1.4b \r\n", prompt: true, "ATZ");
            Assert.True(echoed);
            Assert.Equal(new[] { "ELM327 v1.4b" }, lines);
        }
    }
}
