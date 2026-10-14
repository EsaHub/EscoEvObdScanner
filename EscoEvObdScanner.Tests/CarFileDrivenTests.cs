using System.Text.Json.Nodes;

namespace EscoEvObdScanner.Tests
{
    /// <summary>What the car file decides rather than the code: the demo car's drive, date order, yes/no codes and the warranty.</summary>
    public class CarFileDrivenTests
    {
        [Fact]
        public void DemoDriveMovesTheDashboardSourcesOfTheCarFile()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            car.Dynamic = true;

            var s = new LivePoller(scanner, catalog).Poll();

            // speed, current and state of charge are encoded with the car file's own expressions
            Assert.InRange(s.Speed!.Value, 4, 106);
            Assert.InRange(s.Current!.Value, -20, 120);
            Assert.InRange(s.Soc!.Value, 70, 90);
            // the motor follows the speed at the car file's ratio (both read within a fraction of a second)
            Assert.InRange(s.MotorRpm!.Value, s.Speed.Value * 67 * 0.97 - 5, s.Speed.Value * 67 * 1.03 + 5);
        }

        [Fact]
        public void DemoCarWithoutADriveRatioKeepsTheRecordedRpm()
        {
            var path = TestData.CatalogCopy();
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root["platform"]!.AsObject().Remove("demo_drive");
            File.WriteAllText(path, root.ToJsonString());
            var catalog = Catalog.Load(path);
            var car = SimulatedCar.Demo(catalog);
            car.Open();
            car.Dynamic = true;
            var scanner = new Scanner(car);
            scanner.Init();

            var vcu = catalog.Ecu("vcu");
            scanner.Target(vcu.Request, vcu.Response);
            var rpm = scanner.ReadDid(0xD8CF);

            car.Dynamic = false;
            Assert.Equal(Bytes.Hex(scanner.ReadDid(0xD8CF).Data), Bytes.Hex(rpm.Data));
            // and the VCU, no longer named as ignoring TesterPresent, answers it
            Assert.Equal(ResponseKind.Positive, scanner.Request("3E00", 0x3E).Kind);
        }

        [Theory]
        [InlineData(null, 2023, 1, 8)]
        [InlineData("dmy", 2023, 1, 8)]
        [InlineData("ymd", 2008, 1, 23)]
        public void BatteryDateFollowsTheCarFilesByteOrder(string? order, int year, int month, int day)
        {
            Assert.Equal(new DateTime(year, month, day), Bytes.BcdDate(new byte[] { 0x08, 0x01, 0x23 }, order));
        }

        [Fact]
        public void YesNoCodesComeFromTheCarFile()
        {
            var path = TestData.CatalogCopy();
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var charging = root["live"]!["charging"]!.AsObject();
            charging["on"] = new JsonArray(2);
            File.WriteAllText(path, root.ToJsonString());
            var catalog = Catalog.Load(path);
            var car = SimulatedCar.Demo(catalog);
            car.Open();
            var scanner = new Scanner(car);
            scanner.Init();
            var obc = catalog.Ecu("obc");
            int did = Catalog.ParseDid(catalog.LiveOf("charging")!.Did);

            car.SetData(obc.Request, did, new byte[] { 0x01 });
            Assert.False(new LivePoller(scanner, catalog).Poll().Charging);
            car.SetData(obc.Request, did, new byte[] { 0x02 });
            Assert.True(new LivePoller(scanner, catalog).Poll().Charging);
        }

        [Fact]
        public void WithoutYesCodesAnyValueAboveZeroIsYes()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            var obc = catalog.Ecu("obc");
            int did = Catalog.ParseDid(catalog.LiveOf("charging")!.Did);
            car.SetData(obc.Request, did, new byte[] { 0x01 });
            Assert.True(new LivePoller(scanner, catalog).Poll().Charging);
            car.SetData(obc.Request, did, new byte[] { 0x00 });
            Assert.False(new LivePoller(scanner, catalog).Poll().Charging);
        }

        [Fact]
        public void EachCarFileGivesItsOwnWarranty()
        {
            string Warranty(string file) =>
                Catalog.Load(Path.Combine(Path.GetDirectoryName(TestData.CatalogPath)!, file)).Platform.Warranty ?? "";
            Assert.Contains("160", Warranty("stellantis-ecmp.json"));
            Assert.DoesNotContain("Kia", Warranty("stellantis-ecmp.json"));
            Assert.Contains("Kia", Warranty("kia-niro-ev-2022.json"));
            Assert.Contains("Kia", Warranty("kia-eniro.json"));
        }
    }
}
