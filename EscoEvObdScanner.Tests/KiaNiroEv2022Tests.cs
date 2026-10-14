namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The shipped second-generation Niro EV car file against synthetic replies
    /// with the byte layout the file reads (and that published captures of
    /// these cars show), each value placed by hand, sent through the same
    /// parser as a live adapter would deliver them.
    /// </summary>
    public class KiaNiroEv2022Tests
    {
        static Catalog Car() =>
            Catalog.Load(Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName, "kia-niro-ev-2022.json"));

        // cell-present mask FF FF FF FF, then 32 cells of 0xB6 = 3.64 V
        const string Cells = "FFFFFFFFB6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6B6";

        // 0x0101: BMS SOC B8 = 100 (50 %), current [B14:B15] = 123 (12.3 A), voltage [B16:B17] = 3714 (371.4 V),
        // temperatures B18 17 / B19 15 and probes B20..B24, cells B27 194 (#12) / B29 193 (#57), 12 V B33 = 131,
        // rpm [B57:B58] = 2500, insulation [B61:B62] = 2600 kOhm
        const string Reply0101 = "620101FFFFFFFF640000000000007B0E82110F0F1010110F0000C20CC139000083000000000000000000000000000000000000000000000009C400000A28";

        // 0x0105: probes B13..B19, charge limit [B20:B21] = 7550 (75.5 kW), discharge limit [B22:B23] = 15000 (150 kW),
        // SOH [B29:B30] = 975 (97.5 %), [B32:B33] = 20000 (not read: meaning open), display SOC B35 = 104 (52 %), probes B43..B46
        const string Reply0105 = "6201050000000000000000000E0F0F100E0F0F1D7E3A98010000000003CF004E20006800000000000000100F0F10";

        static KiaENiroTests.ReplyLink Samples() => new KiaENiroTests.ReplyLink()
            .On(0x7E4, "220101", Reply0101)
            .On(0x7E4, "220105", Reply0105)
            .On(0x7E4, "220102", "620102" + Cells)
            .On(0x7E4, "220103", "620103" + Cells)
            .On(0x7E4, "220104", "620104" + Cells)
            .On(0x7C6, "22B002", "62B002E000000000000061A8000000")                                  // [B10:B12] = 25000 km
            .On(0x7B3, "220100", "62010000000000007A600000000000000000000000000000000000000000000000000000000000")   // ambient B10 = 96 (8 C), speed B33 = 0
            .On(0x7E2, "22E004", "62E0040000000000000000000000001E740000000000000000000000000000000000");          // selector B18 = 0 (P)

        [Fact]
        public void DashboardDecodesEveryPosition()
        {
            var s = new LivePoller(new Scanner(Samples()), Car()).Poll();

            Assert.Equal(52, s.Soc);                    // display SOC, 0x0105
            Assert.Equal(50, s.SocRaw);                 // BMS SOC, 0x0101
            Assert.Equal(371.4, s.Voltage!.Value, 1);
            Assert.Equal(12.3, s.Current!.Value, 1);
            Assert.Equal(2500, s.MotorRpm);
            Assert.Equal(97.5, s.Soh);
            Assert.Null(s.Energy);                      // [B32:B33] is not read: its meaning is not confirmed
            Assert.Equal(75.5, s.MaxChargePower!.Value, 1);
            Assert.Equal(150, s.MaxDischargePower);
            Assert.Equal(17, s.TempMax);
            Assert.Equal(15, s.TempMin);
            Assert.Equal(3.86, s.CellMin!.Value, 2);
            Assert.Equal(3.88, s.CellMax!.Value, 2);
            Assert.Equal(57, s.LowestCell);
            Assert.Equal(13.1, s.AuxVoltage!.Value, 1);
            Assert.Equal(2600, s.IsolationPos);
            Assert.Equal(25000, s.Odometer);
            Assert.Equal(8, s.Ambient);
            Assert.Equal(0, s.Speed);
            Assert.Equal("P", Car().LiveOf("gear")!.Text(s.Gear));

            // 96 cells from three replies, 16 probes from three places in two replies, in that order
            Assert.Equal(96, s.CellVoltages!.Length);
            Assert.All(s.CellVoltages, v => Assert.Equal(3.64, v, 2));
            Assert.Equal(new double[] { 15, 16, 16, 17, 15, 14, 15, 15, 16, 14, 15, 15, 16, 15, 15, 16 }, s.ModuleTemps);
        }

        [Fact]
        public void LifetimeCountersFromARealCar()
        {
            // replies of a 2024 EU Niro EV at 64 626 km
            var link = new KiaENiroTests.ReplyLink()
                .On(0x7E4, "220101", "620101EFFBE7ED8E000000000000880EDC0D0C0D0D0D0C0C0034C640C506000089000591DE0005828B0002184E0001FFF501C40D8100017B037200000ADE")
                .On(0x7E4, "220111", "620111F19FFFFF0000025C0000001C00002F93000001F84D8D000000000C0C00000500000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
            var s = new LivePoller(new Scanner(link), Car()).Poll();

            Assert.Equal(13729.4, s.LifetimeChargedKwh!.Value, 1);
            Assert.Equal(13106.1, s.LifetimeDischargedKwh!.Value, 1);
            Assert.Equal(604, s.AcCharges);
            Assert.Equal(28, s.DcCharges);
            Assert.Equal(12179, s.AcChargedKwh);
            Assert.Equal(504, s.DcChargedKwh);
            Assert.True(s.HasLifetimeCounters);
        }

        [Fact]
        public void ReadsOnlyWithReadDataByIdentifier()
        {
            var link = Samples();
            new LivePoller(new Scanner(link, Car().Platform.Protocol!), Car()).Poll();

            var requests = link.Sent.Where(c => !c.StartsWith("AT")).ToList();
            Assert.All(requests, r => Assert.StartsWith("22", r));
            Assert.Single(requests, r => r == "220105");     // display SOC, health, limits and probes: one read
            Assert.Single(requests, r => r == "220101");     // lifetime charged and discharged come with the main values
        }
    }
}
