namespace EscoEvObdScanner.Tests
{
    public class ExprTests
    {
        [Theory]
        [InlineData("B4", new byte[] { 0x0A }, 10.0)]
        [InlineData("SB4", new byte[] { 0xFB }, -5.0)]
        [InlineData("SB4", new byte[] { 0x7F }, 127.0)]
        [InlineData("S[B4:B5]", new byte[] { 0xFF, 0xFE }, -2.0)]
        [InlineData("s [B4:B5]/10", new byte[] { 0x80, 0x00 }, -3276.8)]
        [InlineData("[B4:B5]/16", new byte[] { 0x19, 0xED }, 414.8125)]
        [InlineData("(B4*256+B5)/512", new byte[] { 0x89, 0x96 }, 68.79296875)]
        [InlineData("S[B4:B7]", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, -1.0)]
        [InlineData("-SB4", new byte[] { 0xFF }, 1.0)]
        [InlineData("B4-40", new byte[] { 0x35 }, 13.0)]
        [InlineData("[B4:B7]/64-1200", new byte[] { 0x00, 0x01, 0x2C, 0x41 }, 1.015625)]
        [InlineData("[B5:B6]/16", new byte[] { 0x01, 0x06, 0x0A }, 96.625)]
        public void Evaluates(string expression, byte[] payload, double expected)
        {
            Assert.Equal(expected, Expr.Evaluate(expression, payload)!.Value, 6);
        }

        [Theory]
        [InlineData("B5")]              // payload too short
        [InlineData("S[B4:B11]")]       // wider than 7 bytes
        [InlineData("B4 +")]            // dangling operator
        [InlineData("B4 B5")]           // trailing garbage
        [InlineData("[B4:B5")]          // missing bracket
        [InlineData("")]
        [InlineData(null)]
        public void ReturnsNullForUnusableInput(string? expression)
        {
            Assert.Null(Expr.Evaluate(expression, new byte[] { 1 }));
        }
    }
}
