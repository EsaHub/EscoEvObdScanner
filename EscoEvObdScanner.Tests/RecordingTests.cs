using System.Globalization;

namespace EscoEvObdScanner.Tests
{
    /// <summary>A recording reads back as what was written, so a report made from it shows the session.</summary>
    public class RecordingTests
    {
        static VehicleState Full(DateTime time) => new()
        {
            Time = time, Soc = 80.79, SocRaw = 80.8, Voltage = 412.5, Current = -12.25, Speed = 54.3,
            MotorRpm = 3640, Energy = 36.4, Soh = 96.625, CellMin = 3.817, CellMax = 3.832, TempMin = 6, TempAvg = 7.5,
            TempMax = 9, Ambient = -6, AuxVoltage = 14.59, MaxChargePower = 31.6, MaxDischargePower = 156.1, Charging = false,
            Odometer = 60288, GridVoltage = 0, Gear = 3, DriveMode = 6, LifetimeChargedKwh = 12964, LifetimeDischargedKwh = 11421.5,
            AcCharges = 604, DcCharges = 28, AcChargedKwh = 12179, DcChargedKwh = 504, MotorTemp = 5, InverterTemp = 2.688,
            ChargerTemp = 29, DcDcTemp = 3, CoolantTemp = 2.6, AuxBatteryTemp = 0, ContactorCloses = 4023, TimeAbove55 = 0,
            IsolationPos = 60000, IsolationNeg = 59000, LowestCell = 88
        };

        [Theory]
        [InlineData("fi-FI")]
        [InlineData("en-US")]
        public void EveryColumnReadsBackAsWritten(string culture)
        {
            var saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            try
            {
                var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "live_20261012_060129.csv");
                var written = Full(new DateTime(2026, 10, 12, 6, 1, 29, 250));
                File.WriteAllLines(file, new[] { string.Join(";", Recording.Columns), Recording.Line(written) });

                var read = Assert.Single(Recording.Read(file));

                Assert.Equal(Recording.Columns.Length, Recording.Line(written).Split(';').Length);
                Assert.Equal(written.Time, read.Time);
                foreach (var p in typeof(VehicleState).GetProperties().Where(p => p.CanWrite
                             && (p.PropertyType == typeof(double?) || p.PropertyType == typeof(int?) || p.PropertyType == typeof(bool?))))
                {
                    // the spread is the maximum less the minimum; the current at the cell read comes from the cells file
                    if (p.Name is "TempDelta" or "CellsCurrent") continue;
                    var value = p.GetValue(written);
                    Assert.True(value != null, $"{p.Name} is not in the test reading: add it, and to the recording");
                    Assert.True(Equals(value, p.GetValue(read)),
                        $"{p.Name}: wrote {value}, read {p.GetValue(read)}");
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
