using System.Text;

namespace EscoEvObdScanner.Tests
{
    /// <summary>Stored identities are compared with new ones: the same car must give the same hash in every version.</summary>
    public class VehicleIdentityTests
    {
        [Fact]
        public void HashFormulaStaysTheSame()
        {
            Assert.Equal("A1133D16CC9BEDF4", VehicleIdentity.Hash(0xF18C, Encoding.ASCII.GetBytes("VR7UKZKXZNJ123456")));
        }

        [Fact]
        public void SerialNumberAndVinOfTheSameBytesDiffer()
        {
            var data = Encoding.ASCII.GetBytes("VR7UKZKXZNJ123456");
            Assert.NotEqual(VehicleIdentity.Hash(0xF18C, data), VehicleIdentity.Hash(0xF190, data));
        }

        [Fact]
        public void TheSameCarGivesTheSameIdentity()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            string? first = VehicleIdentity.Read(scanner, catalog);
            Assert.NotNull(first);
            Assert.Equal(16, first!.Length);
            Assert.Equal(first, VehicleIdentity.Read(scanner, catalog));
        }

        [Fact]
        public void NoEcuAnsweringMeansTheCarFileIsNotThisCars()
        {
            // a car on the adapter whose addresses are all different: silence on every ECU of the car file
            var link = new ScriptedLink();
            var (id, answered) = VehicleIdentity.Check(new Scanner(link), TestData.Catalog());

            Assert.Null(id);
            Assert.False(answered);
            Assert.Contains("ATSH6B4", link.Sent);              // the ECU of "soc" was asked
            Assert.True(link.Sent.Count(c => c.StartsWith("ATSH")) > 1);
        }

        [Fact]
        public void ARefusalStillCountsAsAnAnswer()
        {
            // the car file fits, the car only keeps its serial number to itself
            var link = new ScriptedLink().On("22F18C", "7F2233").On("22F190", "7F2231");
            var (id, answered) = VehicleIdentity.Check(new Scanner(link), TestData.Catalog());

            Assert.Null(id);
            Assert.True(answered);
        }

        [Fact]
        public void EcuThatCannotBeAddressedIsSkipped()
        {
            var link = new ScriptedLink().On("ATSH6B4", "?").On("22F18C", "62F18C313233");
            var (id, answered) = VehicleIdentity.Check(new Scanner(link), TestData.Catalog());

            Assert.Equal(VehicleIdentity.Hash(0xF18C, new byte[] { 0x31, 0x32, 0x33 }), id);
            Assert.True(answered);
        }

        [Fact]
        public void TheDemoCarAnswersTheCheck()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var (id, answered) = VehicleIdentity.Check(scanner, catalog);
            Assert.True(answered);
            Assert.Equal(VehicleIdentity.Read(scanner, catalog), id);
        }

        [Fact]
        public void TheDemoCarGivesItsVin()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            Assert.Equal("VR7DEM00000000001", VehicleIdentity.ReadVin(scanner, catalog));
        }

        [Theory]
        [InlineData("VR7DEM00000000001", "VR7DEM00000000001")]
        [InlineData("\0VR7DEM00000000001  ", "VR7DEM00000000001")]
        [InlineData("VR7DEM0000000001", null)]          // 16 characters
        [InlineData("VR7DEMO0000000001", null)]         // O is never in a VIN
        [InlineData("vr7dem00000000001", null)]
        [InlineData("                 ", null)]
        public void OnlyAValidVinCounts(string reply, string? expected)
        {
            Assert.Equal(expected, VehicleIdentity.Vin(Encoding.Latin1.GetBytes(reply)));
        }
    }
}
