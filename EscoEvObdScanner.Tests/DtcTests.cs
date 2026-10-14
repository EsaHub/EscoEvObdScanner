namespace EscoEvObdScanner.Tests
{
    public class DtcTests
    {
        [Theory]
        [InlineData(0x108309, "P1083", 0x09)]
        [InlineData(0xD18987, "U1189", 0x87)]
        [InlineData(0xD20829, "U1208", 0x29)]
        [InlineData(0x923800, "B1238", 0x00)]
        [InlineData(0x520E00, "C120E", 0x00)]
        public void Codes(int raw, string code, byte failureType)
        {
            var d = new Dtc { Raw = raw };
            Assert.Equal(code, d.Code);
            Assert.Equal(failureType, d.FailureType);
        }

        [Theory]
        [InlineData(0x89, "ACTIVE")]
        [InlineData(0x28, "stored")]
        [InlineData(0x08, "stored")]
        [InlineData(0x20, "stored")]
        [InlineData(0x04, "stored")]
        [InlineData(0x02, "stored")]
        [InlineData(0x80, "stored")]
        [InlineData(0x50, "no fault")]
        [InlineData(0x40, "no fault")]
        public void Severity(byte status, string severity)
        {
            Assert.Equal(severity, new Dtc { Raw = 0x108309, Status = status }.Severity);
        }

        [Fact]
        public void ParsesRecordList()
        {
            // echo 02, availability mask FF, then records of code + status, one padding record
            var data = new byte[] { 0x02, 0xFF, 0x10, 0x83, 0x09, 0x28, 0xD2, 0x08, 0x29, 0x89, 0, 0, 0, 0 };
            var list = Dtc.ParseList(data);

            Assert.Equal(2, list.Count);
            Assert.Equal("P1083", list[0].Code);
            Assert.Equal("ACTIVE", list[1].Severity);
        }

        [Fact]
        public void ReadFromSimulatedCar()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var ecu = catalog.Ecu("ecu74a");
            scanner.Target(ecu.Request, ecu.Response);

            var r = scanner.Request(DtcRequests.List(), 0x19);
            var faults = Dtc.ParseList(r.Data);

            Assert.Equal(ResponseKind.Positive, r.Kind);
            Assert.Equal(2, faults.Count);
            Assert.Contains(faults, f => $"{f.Code}-{f.FailureType:X2}" == "U1208-29" && f.Severity == "ACTIVE");
        }

        [Fact]
        public void TheMeaningColumnExplainsTheSameBits()
        {
            // a saved read holds the shorter names; both columns show the same three bits
            var row = new EscoEvObdScanner.AppLogic.ViewModels.DtcRow("Camera", "U1208-29", "ACTIVE", "0x89", "testFailed, confirmed, warningIndicator", "", "", "");
            Assert.Equal("testFailed, confirmedDTC, warningIndicatorRequested", row.StatusTextShown);
            var loc = EscoEvObdScanner.AppLogic.Infrastructure.Loc.I;
            Assert.Equal($"{loc["DtcBit0"]}, {loc["DtcBit3"]}, {loc["DtcBit7"]}", row.StatusMeaningShown);
            Assert.Equal("", (row with { StatusText = "clear" }).StatusMeaningShown);
        }

        [Fact]
        public void StatusBitsAreTheirIsoNames()
        {
            Assert.Equal("testFailed, confirmedDTC, warningIndicatorRequested", new Dtc { Raw = 0xC20829, Status = 0x89 }.StatusText);
            // a saved read in the shorter form shows the same names
            Assert.Equal("pendingDTC", Dtc.IsoBitName("pending"));
            Assert.Equal("testFailed", Dtc.IsoBitName("testFailed"));
        }

        [Theory]
        [InlineData("U1208-29", "Signal Invalid")]
        [InlineData("P12DF-13", "Circuit Open")]
        [InlineData("P166C-64", "Signal Plausibility Failure")]
        [InlineData("P1234-F3", null)]
        [InlineData("—", null)]
        public void FailureTypesAreTheirSaeNames(string code, string? name)
        {
            Assert.Equal(name, Dtc.FailureTypeName(code));
        }
    }
}
