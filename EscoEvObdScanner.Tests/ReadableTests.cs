namespace EscoEvObdScanner.Tests
{
    public class ReadableTests
    {
        static string R(string hex) => Bytes.Readable(Convert.FromHexString(hex)).Replace('\u00A0', ' ');

        [Fact]
        public void ShortRepliesAreOneNumber()
        {
            Assert.Equal("100", R("64"));
            Assert.Equal("231", R("00E7"));
            Assert.Equal("58983", R("0000E667"));
        }

        [Fact]
        public void TextStaysText()
        {
            Assert.Equal("\"VE80_L_27_09-4M     VE80-SER\"", R("564538305F4C5F32375F30392D344D2020202020564538302D5345522020202020202020"));
        }

        [Fact]
        public void LongRepliesAreSixteenBitValues()
        {
            Assert.Equal("3629  3630  3632", R("0E2D0E2E0E30"));
            Assert.EndsWith("…", Bytes.Readable(new byte[40]));
        }

        [Fact]
        public void BadHexIsEmpty()
        {
            Assert.Empty(Bytes.FromHex("XYZ"));
            Assert.Equal("", Bytes.Readable(Bytes.FromHex(null)));
        }
    }
}
