namespace EscoEvObdScanner.Tests
{
    /// <summary>Freeze frames read from the car on 2026-09-28.</summary>
    public class DtcSnapshotTests
    {
        // where the e-CMP car file says the clock and odometers are
        static readonly FaultTimeDef Time = TestData.Catalog().Platform.FaultTime!;

        const string VcuP1083 = "04108309280111D49C00CD10D4CA1E5B7A93D8CF0000D4660000D4020000D8CD0000D80B0000D80F0000D82204E2D82A00D8DE007DD8DF0000D8E001D8E800D8E900D8EA01D8EE02";
        const string SteeringU1313 = "04D31383280111D40100E667D4030000D4046CD40535D40701D40801D40900D40B018A00D40C03D40D0097D40FFFD41000D41200D41400D41500D4180000D41BFF";
        const string ChargerP1C02 = "041C021628010AD49C0000E5BFD4CA22351A0CD40700D80501EBD82500D8240000D8EB0192D82702D82204E1D83B00";

        [Fact]
        public void VcuFrameGivesOdometerAndClock()
        {
            var f = DtcSnapshot.Decode(VcuP1083, null, Time)!;
            Assert.Equal(17, f.Values.Count);
            Assert.Equal(52496, f.Km);
            Assert.Equal(0x1E5B7A93, f.Clock);
        }

        [Fact]
        public void KnownLengthsSteerTheSplit()
        {
            // the charger keeps the odometer in four bytes, the VCU in three
            var f = DtcSnapshot.Decode(ChargerP1C02, did => did == 0xD49C ? 4 : null, Time)!;
            Assert.Equal(10, f.Values.Count);
            Assert.Equal(58815, f.Km);
            Assert.Equal(0x22351A0C, f.Clock);
        }

        [Fact]
        public void PowerSteeringHasItsOwnOdometer()
        {
            var f = DtcSnapshot.Decode(SteeringU1313, null, Time)!;
            Assert.Equal(58983, f.Km);
            Assert.Null(f.Clock);
        }

        [Fact]
        public void OdometerIsReadThroughTheEcusExpression()
        {
            // the BMS keeps km x 1024: 0x039FD400 is 59 381 km
            var bms = TestData.Catalog().Ecu("bms");
            string? ExpressionOf(int did) => bms.Dids.Concat(bms.Discovered).FirstOrDefault(d => d.Did == did)?.Expression;
            const string frame = "04123456280101D49C039FD400";

            Assert.Equal(59381, DtcSnapshot.Decode(frame, did => did == 0xD49C ? 4 : null, Time, ExpressionOf)!.Km);
            Assert.Equal(0x039FD400, DtcSnapshot.Decode(frame, did => did == 0xD49C ? 4 : null, Time)!.Km);
        }

        [Fact]
        public void OdometerExpressionGivesWholeKilometres()
        {
            var f = DtcSnapshot.Decode("04123456280101D49C039FD7FF", did => 4, Time, _ => "[B4:B7]/1024")!;
            Assert.Equal(59381, f.Km);
        }

        [Fact]
        public void UnusableOdometerExpressionFallsBackToPlainKilometres()
        {
            var f = DtcSnapshot.Decode(VcuP1083, null, Time, _ => "[B4:B9]/2")!;
            Assert.Equal(52496, f.Km);
            Assert.Equal(52496, DtcSnapshot.Decode(VcuP1083, null, Time, _ => null)!.Km);
        }

        [Fact]
        public void EmptyOrOddFramesDoNotThrow()
        {
            Assert.Empty(DtcSnapshot.Decode("04520E00200111")!.Values);
            Assert.Null(DtcSnapshot.Decode(""));
            Assert.Null(DtcSnapshot.Decode("zz"));
        }

        [Fact]
        public void ClockTurnsIntoADate()
        {
            // the P17CC freeze frame, and the vehicle clock read at 2026-09-27 16:57:00 UTC
            var when = DtcSnapshot.When(0x22577754, 0x225DD6B7, new DateTime(2026, 9, 27, 16, 57, 0, DateTimeKind.Utc), 0.1)!.Value;
            Assert.Equal(new DateTime(2026, 9, 27, 5, 20, 56, DateTimeKind.Utc), when, TimeSpan.FromSeconds(1));
            Assert.Null(DtcSnapshot.When(10, 5, DateTime.UtcNow, 0.1));
            // a garbled clock or a bad tick from the car file is no time, not a crash
            Assert.Null(DtcSnapshot.When(0, long.MaxValue, DateTime.UtcNow, 0.1));
            Assert.Null(DtcSnapshot.When(5, 10, DateTime.UtcNow, 0));
            Assert.Null(DtcSnapshot.When(5, 10, DateTime.UtcNow, double.NaN));
        }

        [Fact]
        public void LaterRecordsDoNotSpoilTheFirst()
        {
            // record 01 with odometer and clock, then record 02 with one value
            var f = DtcSnapshot.Decode("04123456280102D49C00E667D4CA22577754" + "0201D8220540", null, Time)!;
            Assert.Equal(58983, f.Km);
            Assert.Equal(0x22577754, f.Clock);
            Assert.Equal(3, f.Values.Count);
        }
    }
}
