namespace EscoEvObdScanner.Tests
{
    /// <summary>The battery management's identification and counters: decoding, and the live reads of the demo car.</summary>
    public class BatteryIdentityTests
    {
        [Fact]
        public void BcdDateWithAnUnknownOrderIsNoDate()
        {
            Assert.Null(Bytes.BcdDate(Convert.FromHexString("080123"), "mdy"));
            Assert.Null(Bytes.BcdDate(Convert.FromHexString("080123"), ""));
            Assert.Equal(new DateTime(2023, 1, 8), Bytes.BcdDate(Convert.FromHexString("080123"), "DMY"));
        }

        [Fact]
        public void BcdDateIsDayMonthYear()
        {
            Assert.Equal(new DateTime(2023, 1, 8), Bytes.BcdDate(Convert.FromHexString("080123")));
            Assert.Equal(new DateTime(2023, 3, 8), Bytes.BcdDate(Convert.FromHexString("080323")));
        }

        [Theory]
        [InlineData("0801")]        // too short
        [InlineData("320123")]      // day 32
        [InlineData("081323")]      // month 13
        [InlineData("300223")]      // 30 February
        [InlineData("0A0123")]      // not BCD
        [InlineData("290223")]      // 29 February in a common year
        [InlineData("000123")]      // day 0
        [InlineData("FFFFFF")]
        public void BcdDateRefusesWhatIsNoDate(string hex) => Assert.Null(Bytes.BcdDate(Convert.FromHexString(hex)));

        [Theory]
        [InlineData("290224", 2024, 2, 29)]
        [InlineData("31122399", 2023, 12, 31)]      // a longer reply: the first three bytes
        public void BcdDateEdges(string hex, int y, int m, int d) =>
            Assert.Equal(new DateTime(y, m, d), Bytes.BcdDate(Convert.FromHexString(hex)));


        [Fact]
        public void TextDropsThePadding()
        {
            Assert.Equal("A0BMUC059S74CV20", Bytes.Text(Convert.FromHexString("4130424D55433035395337344356323000")));
            Assert.Equal("810221-00504V1.7", Bytes.Text(Convert.FromHexString("3831303232312D303035303456312E3720")));
            Assert.Null(Bytes.Text(Convert.FromHexString("0000FFFF20")));
        }

        [Fact]
        public void TheDemoCarGivesItsBatteryIdentityAndCounters()
        {
            var (_, scanner, catalog) = TestData.DemoCar();

            // the first round reads the rare values too
            var s = new LivePoller(scanner, catalog).Poll();

            Assert.Equal(new DateTime(2023, 1, 8), s.BatteryMade);
            Assert.Equal("A0BMUC059S74CV20", s.BmsSoftware);
            Assert.Equal(0x0F56, s.ContactorCloses);
            Assert.Equal(0, s.TimeAbove55);
            Assert.True(s.HasLifetimeCounters);
        }

        [Fact]
        public void TheTextsAreReadOnceOutsideTheLiveView()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var s = new VehicleState();

            LivePoller.ReadTexts(scanner, catalog, s);

            Assert.Equal(new DateTime(2023, 1, 8), s.BatteryMade);
            Assert.Equal("A0BMUC059S74CV20", s.BmsSoftware);
        }
    }
}
