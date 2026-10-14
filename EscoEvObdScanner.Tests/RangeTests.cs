namespace EscoEvObdScanner.Tests
{
    public class RangeTests
    {
        static readonly DateTime Day = new(2026, 9, 1);

        [Fact]
        public void NoEstimateBeforeEnoughHistory()
        {
            var drives = new[] { new Drive(Day, 20, 3, 15) };
            Assert.Null(RangeEstimator.Consumption(drives, 15));
        }

        [Fact]
        public void UsesDrivesAtASimilarTemperature()
        {
            var drives = new[]
            {
                new Drive(Day, 100, 22, -10),                  // winter: 22 kWh/100 km
                new Drive(Day.AddDays(1), 50, 7, 14),          // summer: 14 kWh/100 km
            };

            var summer = RangeEstimator.Consumption(drives, 16)!;
            Assert.True(summer.SimilarTemperature);
            Assert.Equal(14, summer.KwhPer100, 3);

            var winter = RangeEstimator.Consumption(drives, -8)!;
            Assert.Equal(22, winter.KwhPer100, 3);
        }

        [Fact]
        public void FallsBackToAllDrivesWithoutASimilarOne()
        {
            var drives = new[] { new Drive(Day, 100, 20, -10), new Drive(Day.AddDays(1), 100, 14, 14) };
            var r = RangeEstimator.Consumption(drives, 30)!;
            Assert.False(r.SimilarTemperature);
            Assert.Equal(17, r.KwhPer100, 3);
        }

        [Fact]
        public void OnlyTheNewestDrivesCount()
        {
            var drives = new List<Drive> { new(Day, 1000, 250, 15) };               // an old, thirsty stretch
            for (int i = 1; i <= 7; i++) drives.Add(new(Day.AddDays(i), 100, 15, 15));

            Assert.Equal(15, RangeEstimator.Consumption(drives, 15)!.KwhPer100, 3);
        }

        [Fact]
        public void RangeIsEnergyOverConsumption()
        {
            Assert.Equal(164, RangeEstimator.Range(26.24, 16)!.Value, 3);
            Assert.Null(RangeEstimator.Range(null, 16));
            Assert.Null(RangeEstimator.Range(26, null));
        }
    }
}
