namespace EscoEvObdScanner.Tests
{
    public class LivePollerTests
    {
        [Fact]
        public void FirstRoundReadsEverything()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var s = new LivePoller(scanner, catalog).Poll();

            Assert.Equal(80.79, s.Soc!.Value, 2);
            Assert.Equal(425.625, s.Voltage!.Value, 3);
            Assert.Equal(14.492, s.AuxVoltage!.Value, 3);       // in Ready: the DC/DC charges the 12 V battery
            Assert.Equal(0.0, s.Speed!.Value);
            Assert.Equal(108, s.CellVoltages!.Length);
            Assert.Equal(54, s.ModuleTemps!.Length);
            Assert.Equal(108, s.CellSoh!.Length);
            Assert.InRange(s.CellVoltages.Min(), 3.9, 4.0);
            Assert.NotNull(s.Power);
            Assert.Null(s.Consumption);          // standing still
            Assert.Equal(59381, s.Odometer);     // VCU 0xD49C in the recorded data
        }

        [Fact]
        public void CellsAreReadAgainUnderLoad()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            var now = new DateTime(2026, 10, 4, 7, 0, 0);
            var poller = new LivePoller(scanner, catalog) { Clock = () => now };
            int CellReads() => car.Requests.Count(r => r == "22D440");

            poller.Poll();                                     // round 0 reads the cells
            Assert.Equal(1, CellReads());
            now = now.AddSeconds(2);
            poller.Poll();                                     // standing: the cells wait for their round
            Assert.Equal(1, CellReads());

            // 120 A: (120 + 1200) x 64, four bytes
            car.SetData(0x6B4, 0xD816, new byte[] { 0x00, 0x01, 0x4A, 0x00 });
            now = now.AddSeconds(2);
            poller.Poll();                                     // the current reads 120 A in this round
            now = now.AddSeconds(2);
            var s = poller.Poll();                             // under load, 6 s after the last cell read: read again
            Assert.Equal(2, CellReads());
            Assert.Equal(120, s.CellsCurrent!.Value, 1);
            now = now.AddSeconds(2);
            poller.Poll();                                     // only 2 s later: not yet
            Assert.Equal(2, CellReads());
        }

        [Fact]
        public void StandstillRpmIsNotReadAsAHugeValue()
        {
            // the VCU reports 0xFFFF while standing: a signed -1, as the MCU gives it, and never 65535
            var (car, scanner, catalog) = TestData.DemoCar();
            car.SetData(0x6A2, 0xD8CF, new byte[] { 0xFF, 0xFF });
            Assert.Equal(-1, new LivePoller(scanner, catalog).Poll().MotorRpm);
        }

        [Fact]
        public void ValueOutsideTheCarFilesLimitsIsDropped()
        {
            // the e-CMP's motor turns at most a few thousand rpm either way; the car file keeps it within 20000
            var (car, scanner, catalog) = TestData.DemoCar();
            car.SetData(0x6A2, 0xD8CF, new byte[] { 0x80, 0x00 });
            Assert.Null(new LivePoller(scanner, catalog).Poll().MotorRpm);
        }

        [Fact]
        public void LifetimeCountersKeepTheirLastValueWhenAReadFails()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            var poller = new LivePoller(scanner, catalog);
            var first = poller.Poll();                         // round 0 reads the rare values
            Assert.NotNull(first.LifetimeDischargedKwh);

            // the car switched off for a moment: the counters come back too short to read
            car.SetData(0x6B4, 0xD42E, new byte[] { 0x00 });
            VehicleState s = first;
            for (int i = 0; i < LivePoller.RareEvery; i++) s = poller.Poll();

            Assert.Equal(2, car.Requests.Count(r => r == "22D42E"));     // read again, and failed
            Assert.Equal(first.LifetimeDischargedKwh, s.LifetimeDischargedKwh);
            Assert.Equal(first.LifetimeChargedKwh, s.LifetimeChargedKwh);
        }

        [Fact]
        public void SlowValuesAreKeptBetweenRounds()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            var poller = new LivePoller(scanner, catalog);
            poller.Poll();

            car.Requests.Clear();
            var s = poller.Poll();                 // round 1: fast values only

            Assert.DoesNotContain("22D865", car.Requests);
            Assert.NotNull(s.Energy);              // still there from round 0
            Assert.NotNull(s.CellVoltages);
        }

        [Fact]
        public void CellsTimeMarksFreshCellReadings()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var poller = new LivePoller(scanner, catalog);

            var first = poller.Poll();          // round 0 reads the cells
            var second = poller.Poll();         // round 1 does not

            Assert.NotNull(first.CellsTime);
            Assert.Equal(first.CellsTime, second.CellsTime);
            Assert.NotNull(second.CellVoltages);   // still carried over
        }

        [Fact]
        public void DynamicCarMoves()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            car.Dynamic = true;
            var s = new LivePoller(scanner, catalog).Poll();

            Assert.True(s.Speed > 0);
            Assert.True(s.Current > 0);
            Assert.NotNull(s.Consumption);
        }

        [Fact]
        public void ArrayDecodingStopsAtPadding()
        {
            var data = new byte[] { 0x0E, 0x2D, 0x0E, 0x2E, 0x00, 0x00, 0x00, 0x00 };
            var cells = LivePoller.DecodeArray(data, 2, v => v / 1000.0, 2.0, 4.5);
            Assert.Equal(new[] { 3.629, 3.630 }, cells);
        }
    }
}
