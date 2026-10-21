namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The Bluetooth LE settings file: the shipped file loads, the pairing PIN is read, a missing or broken file gives
    /// the defaults, a list left out keeps its default, and firmware update services are never
    /// used whatever a file says.
    /// </summary>
    public class BleAdaptersTests
    {
        static readonly Guid NordicDfu = BleAdapters.Uuid("FE59");

        static string Temp(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"ble-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void ShippedFileLoadsAsTheDefaults()
        {
            string shipped = Path.Combine(AppContext.BaseDirectory, BleAdapters.FileName);
            var ble = BleAdapters.Load(out string? problem, shipped);

            Assert.Null(problem);
            Assert.Equal(BleAdapters.Default.PreferredServices, ble.PreferredServices);
            Assert.Equal(BleAdapters.Default.NeverUsedServices.OrderBy(g => g), ble.NeverUsedServices.OrderBy(g => g));
            Assert.Equal(BleAdapters.Default.AdapterName.ToString(), ble.AdapterName.ToString());
        }

        [Fact]
        public void ShortIdIsABluetoothBaseUuid()
        {
            Assert.Equal(new Guid("0000fff0-0000-1000-8000-00805f9b34fb"), BleAdapters.Uuid("FFF0"));
            Assert.Equal(new Guid("0000fff0-0000-1000-8000-00805f9b34fb"), BleAdapters.Uuid(" fff0 "));
            Assert.Equal(new Guid("00001530-1212-efde-1523-785feabcd123"), BleAdapters.Uuid("00001530-1212-efde-1523-785feabcd123"));
        }

        [Fact]
        public void MissingFileGivesTheDefaults()
        {
            var ble = BleAdapters.Load(out string? problem, Path.Combine(Path.GetTempPath(), "no-such-ble-file.json"));
            Assert.Null(problem);
            Assert.Same(BleAdapters.Default, ble);
        }

        [Theory]
        [InlineData("{ not json")]
        [InlineData("{ \"preferred_services\": [ \"not a uuid\" ] }")]
        [InlineData("{ \"adapter_names\": \"(unclosed\" }")]
        public void BrokenFileGivesTheDefaultsAndSaysWhy(string json)
        {
            string path = Temp(json);
            try
            {
                var ble = BleAdapters.Load(out string? problem, path);
                Assert.Same(BleAdapters.Default, ble);
                Assert.NotNull(problem);
                Assert.Contains(path, problem);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void FirstExistingFileWinsAndListsLeftOutKeepTheirDefaults()
        {
            string user = Temp("{ \"preferred_services\": [ \"ABCD\" ] }");
            try
            {
                var ble = BleAdapters.Load(out _, user, Path.Combine(AppContext.BaseDirectory, BleAdapters.FileName));
                Assert.Equal(new[] { BleAdapters.Uuid("ABCD") }, ble.PreferredServices);
                Assert.Equal(BleAdapters.Default.AdapterName.ToString(), ble.AdapterName.ToString());
                Assert.Contains(BleAdapters.Uuid("1800"), ble.NeverUsedServices);
            }
            finally { File.Delete(user); }
        }

        [Fact]
        public void FirmwareUpdateServicesAreNeverUsedWhateverTheFileSays()
        {
            // a file that empties the list, or names the update service as preferred
            string path = Temp("{ \"never_used_services\": [], \"preferred_services\": [ \"FE59\" ] }");
            try
            {
                var ble = BleAdapters.Load(out _, path);
                Assert.Contains(NordicDfu, ble.NeverUsedServices);
                Assert.Contains(BleAdapters.Uuid("00001530-1212-efde-1523-785feabcd123"), ble.NeverUsedServices);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PairingPinComesFromTheFileOrTheDefault()
        {
            Assert.Equal("123456", BleAdapters.Load(out _, Path.Combine(AppContext.BaseDirectory, BleAdapters.FileName)).PairingPin);
            string path = Temp("{ \"pairing_pin\": \" 0000 \" }");
            try { Assert.Equal("0000", BleAdapters.Load(out _, path).PairingPin); }
            finally { File.Delete(path); }
        }

        [Fact]
        public void AdapterNamesMatchWithoutCase()
        {
            Assert.Matches(BleAdapters.Default.AdapterName, "OBDLink CX");
            Assert.Matches(BleAdapters.Default.AdapterName, "vLinker MC-IOS");
            Assert.DoesNotMatch(BleAdapters.Default.AdapterName, "Galaxy Buds2");
        }
    }
}
