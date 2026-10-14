namespace EscoEvObdScanner.Tests
{
    /// <summary>The battery report's rough grading.</summary>
    public class BatteryChecksTests
    {
        [Theory]
        [InlineData(97.0, Grade.Good)]
        [InlineData(90.0, Grade.Good)]
        [InlineData(85.0, Grade.Watch)]
        [InlineData(79.9, Grade.Bad)]
        public void HealthIsGraded(double soh, Grade expected) => Assert.Equal(expected, BatteryChecks.Soh(soh));

        [Fact]
        public void CellSpreadIsGradedOnlyAtRest()
        {
            Assert.Equal(Grade.Good, BatteryChecks.CellSpread(12, 1.5));
            Assert.Equal(Grade.Watch, BatteryChecks.CellSpread(45, -2));
            Assert.Equal(Grade.Bad, BatteryChecks.CellSpread(120, 0));
            // under load, or with the current unknown, the spread is shown but not rated
            Assert.Equal(Grade.Info, BatteryChecks.CellSpread(120, 97));
            Assert.Equal(Grade.Info, BatteryChecks.CellSpread(12, -40));
            Assert.Equal(Grade.Info, BatteryChecks.CellSpread(12, null));
            Assert.Null(BatteryChecks.CellSpread(null, 0));
        }

        [Fact]
        public void TemperatureSpreadInsulationAndFaultsAreGraded()
        {
            Assert.Equal(Grade.Good, BatteryChecks.TempSpread(3));
            Assert.Equal(Grade.Bad, BatteryChecks.TempSpread(12));
            Assert.Equal(Grade.Good, BatteryChecks.Isolation(13150));
            Assert.Equal(Grade.Bad, BatteryChecks.Isolation(0));          // a dead short is a finding, not a missing value
            Assert.Equal(Grade.Good, BatteryChecks.Faults(0, 0));
            Assert.Equal(Grade.Watch, BatteryChecks.Faults(0, 1));
            Assert.Equal(Grade.Bad, BatteryChecks.Faults(1, 0));
        }

        [Fact]
        public void BoundariesAndNonNumbers()
        {
            Assert.Equal(Grade.Watch, BatteryChecks.Soh(80));
            Assert.Equal(Grade.Watch, BatteryChecks.CellSpread(30, 5));        // 5 A still counts as rest
            Assert.Equal(Grade.Info, BatteryChecks.CellSpread(30, 5.1));
            Assert.Equal(Grade.Bad, BatteryChecks.CellSpread(80, 0));
            Assert.Equal(Grade.Good, BatteryChecks.TempSpread(4.9));
            Assert.Equal(Grade.Watch, BatteryChecks.TempSpread(5));
            Assert.Equal(Grade.Bad, BatteryChecks.TempSpread(10));
            Assert.Equal(Grade.Good, BatteryChecks.Isolation(500));
            // below 500 ohms per volt is below the legal minimum: 200 kOhm for 400 V (assumed without a voltage), 190 for 380 V
            Assert.Equal(Grade.Watch, BatteryChecks.Isolation(200));
            Assert.Equal(Grade.Bad, BatteryChecks.Isolation(199));
            Assert.Equal(Grade.Watch, BatteryChecks.Isolation(190, 380));
            Assert.Equal(Grade.Bad, BatteryChecks.Isolation(189, 380));
            Assert.Equal(200, BatteryChecks.IsolationMinKOhm(double.NaN));
            // over 1000 V the legal minimum (600 kOhm for 1200 V) is above 500: Good starts there, below it is poor
            Assert.Equal(600, BatteryChecks.IsolationGoodFromKOhm(1200));
            Assert.Equal(Grade.Bad, BatteryChecks.Isolation(550, 1200));
            Assert.Equal(Grade.Good, BatteryChecks.Isolation(600, 1200));
            Assert.Equal(500, BatteryChecks.IsolationGoodFromKOhm(800));
            // a non-finite value is no reading, not a bad one
            Assert.Null(BatteryChecks.Soh(double.NaN));
            Assert.Null(BatteryChecks.CellSpread(double.NaN, 0));
            Assert.Null(BatteryChecks.TempSpread(double.PositiveInfinity));
            Assert.Null(BatteryChecks.Isolation(double.NaN));
            Assert.Null(BatteryChecks.Overall(Array.Empty<Grade?>()));
        }

        [Fact]
        public void TheWorstRealGradeDecides()
        {
            Assert.Equal(Grade.Watch, BatteryChecks.Overall(new Grade?[] { Grade.Good, Grade.Info, Grade.Watch, null }));
            Assert.Equal(Grade.Bad, BatteryChecks.Overall(new Grade?[] { Grade.Bad, Grade.Good }));
            Assert.Null(BatteryChecks.Overall(new Grade?[] { Grade.Info, null }));
        }
    }
}
