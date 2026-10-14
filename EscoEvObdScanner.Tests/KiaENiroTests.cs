namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The shipped e-Niro car file against real replies published with evDash
    /// (its CarKiaEniro test data), sent through the same parser as a live
    /// adapter would deliver them. The sample replies are evDash's (MIT, see
    /// THIRD-PARTY-NOTICES.md).
    /// </summary>
    public class KiaENiroTests
    {
        /// <summary>An adapter that answers 0x22 reads per ECU, split into ISO-TP lines the way an ELM327 prints them.</summary>
        internal sealed class ReplyLink : IElmLink
        {
            readonly Dictionary<(int Ecu, string Request), string> _replies = new();
            int _header;

            public List<string> Sent { get; } = new();
            public event Action<string>? Trace;
            public bool IsOpen => true;
            public void Open() { }
            public void Dispose() { }

            public ReplyLink On(int ecu, string request, string reply)
            {
                _replies[(ecu, request)] = reply;
                return this;
            }

            public string[] Send(string command, int timeoutMs = 5000)
            {
                Sent.Add(command);
                Trace?.Invoke(command);
                if (command.StartsWith("ATSH")) _header = Convert.ToInt32(command[4..], 16);
                if (command.StartsWith("AT")) return new[] { "OK" };
                return _replies.TryGetValue((_header, command), out var hex) ? Frames(hex) : new[] { "NO DATA" };
            }

            static string[] Frames(string hex)
            {
                int length = hex.Length / 2;
                if (length <= 7) return new[] { hex };
                var lines = new List<string> { length.ToString("X3"), "0:" + hex[..12] };
                for (int i = 12, n = 1; i < hex.Length; i += 14, n++)
                    lines.Add($"{n % 16:X}:" + hex.Substring(i, Math.Min(14, hex.Length - i)));
                return lines.ToArray();
            }
        }

        static Catalog Car() =>
            Catalog.Load(Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName, "kia-eniro.json"));

        static ReplyLink Samples() => new ReplyLink()
            .On(0x7E4, "220101", "620101FFF7E7FF99000000000300B10EFE120F11100F12000018C438C30B00008400003864000035850000153A00001374000647010D017F0BDA0BDA03E8")
            .On(0x7E4, "220102", "620102FFFFFFFFCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBAAAA")
            .On(0x7E4, "220103", "620103FFFFFFFFCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCACBCACACFCCCBCBCBCBCBCBCBCBAAAA")
            .On(0x7E4, "220104", "620104FFFFFFFFCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBAAAA")
            .On(0x7E4, "220105", "620105003FFF9000000000000000000F8A86012B4946500101500DAC03E800000000AC0000C7C701000F00000000AAAA")
            .On(0x7D1, "22C101", "62C1015FD7E7D0FFFF00FF04D0D400000000FF7EFF0030F5010000FFFF7F6307F207FE05FF00FF3FFFFFAAAAAAAAAAAA")
            .On(0x7C6, "22B002", "62B002E0000000FFB400330B0000000000000000")
            .On(0x7B3, "220100", "6201007E5027C8FF7F765D05B95AFFFF5AFF11FFFFFFFFFFFF6AFFFF2DF0757630FFFF00FFFF000000");

        [Fact]
        public void DashboardDecodesTheSampleReplies()
        {
            var s = new LivePoller(new Scanner(Samples()), Car()).Poll();

            Assert.Equal(86, s.Soc);
            Assert.Equal(383.8, s.Voltage!.Value, 1);
            Assert.Equal(17.7, s.Current!.Value, 1);
            Assert.Equal(100, s.Soh);
            Assert.Equal(3.92, s.CellMax!.Value, 2);
            Assert.Equal(3.90, s.CellMin!.Value, 2);
            Assert.Equal(11, s.LowestCell);
            Assert.Equal(18, s.TempMax);
            Assert.Equal(15, s.TempMin);
            Assert.Equal(13.2, s.AuxVoltage!.Value, 1);
            Assert.Equal(13067, s.Odometer);
            Assert.Equal(19, s.Ambient);
            Assert.Equal(0, s.Speed);
            Assert.Equal(4, s.Gear);
            Assert.Equal("D", Car().LiveOf("gear")!.Text(s.Gear));

            // 98 cells from four replies, and the four module probes
            Assert.Equal(98, s.CellVoltages!.Length);
            Assert.Equal(4.06, s.CellVoltages[0], 2);
            Assert.Equal(3.98, s.CellVoltages[97], 2);
            Assert.Equal(new double[] { 17, 16, 15, 18 }, s.ModuleTemps);
        }

        // a reply with the given payload bytes (after the DID) replaced
        static string Patch(string reply, int payloadByte, string hex) =>
            reply[..(6 + payloadByte * 2)] + hex + reply[(6 + payloadByte * 2 + hex.Length)..];

        const string Cells0104 = "620104FFFFFFFFCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBCBAAAA";
        const string Cells0105 = "620105003FFF9000000000000000000F8A86012B4946500101500DAC03E800000000AC0000C7C701000F00000000AAAA";
        const string Pack0101 = "620101FFF7E7FF99000000000300B10EFE120F11100F12000018C438C30B00008400003864000035850000153A00001374000647010D017F0BDA0BDA03E8";

        // the first round of a fresh poller reads the cells; the next cell read comes CellsEvery rounds later
        static VehicleState PollCells(LivePoller poller, int reads)
        {
            VehicleState s = null!;
            for (int i = 0; i < (reads - 1) * LivePoller.CellsEvery + 1; i++) s = poller.Poll();
            return s;
        }

        static KiaENiroTests.ReplyLink SmallerPack(string empty) => Samples()
            .On(0x7E4, "220104", Patch(Cells0104, 4 + 24, string.Concat(Enumerable.Repeat(empty, 8))))
            .On(0x7E4, "220105", Patch(Cells0105, 34, empty + empty));

        [Theory]
        [InlineData("00")]
        [InlineData("FF")]
        public void ASmallerPackInTheSameLayoutShowsItsOwnCells(string empty)
        {
            // a pack with 88 cells: the last 8 slots of 0x0104 and both of 0x0105 empty
            var poller = new LivePoller(new Scanner(SmallerPack(empty)), Car());

            // the short read counts once it has come twice
            Assert.Null(PollCells(poller, 1).CellVoltages);
            for (int i = 0; i < LivePoller.CellsEvery - 1; i++) poller.Poll();
            var s = poller.Poll();
            Assert.Equal(88, s.CellVoltages!.Length);
            Assert.All(s.CellVoltages, v => Assert.True(v > 2));
        }

        [Fact]
        public void AZeroFilledReadWhileTheBmsWakesDoesNotFixTheCellCount()
        {
            // the first read is short (the BMS not ready yet), the next one is the full pack
            var link = SmallerPack("00");
            var poller = new LivePoller(new Scanner(link), Car());
            Assert.Null(PollCells(poller, 1).CellVoltages);

            link.On(0x7E4, "220104", Cells0104).On(0x7E4, "220105", Cells0105);
            for (int i = 0; i < LivePoller.CellsEvery - 1; i++) poller.Poll();
            Assert.Equal(98, poller.Poll().CellVoltages!.Length);
        }

        [Fact]
        public void TheShortReadMustComeTwiceInARow()
        {
            // short, then a failed read, then short again: not yet twice in a row
            var link = SmallerPack("00");
            var poller = new LivePoller(new Scanner(link), Car());
            Assert.Null(PollCells(poller, 1).CellVoltages);
            link.On(0x7E4, "220104", "7F2231");                                  // refused this time
            for (int i = 0; i < LivePoller.CellsEvery; i++) poller.Poll();
            link.On(0x7E4, "220104", Patch(Cells0104, 4 + 24, "0000000000000000"));
            for (int i = 0; i < LivePoller.CellsEvery - 1; i++) poller.Poll();
            Assert.Null(poller.Poll().CellVoltages);
            for (int i = 0; i < LivePoller.CellsEvery - 1; i++) poller.Poll();
            Assert.Equal(88, poller.Poll().CellVoltages!.Length);
        }

        [Fact]
        public void TwoDeadLastCellsLookLikeASmallerPack()
        {
            // a known limit: two empty slots at the end cannot be told from a smaller pack
            var poller = new LivePoller(new Scanner(Samples().On(0x7E4, "220105", Patch(Cells0105, 34, "0000"))), Car());
            Assert.Null(PollCells(poller, 1).CellVoltages);
            for (int i = 0; i < LivePoller.CellsEvery - 1; i++) poller.Poll();
            Assert.Equal(96, poller.Poll().CellVoltages!.Length);
        }

        [Fact]
        public void ADeadLastCellIsShownNotDropped()
        {
            // one empty slot at the end is a dead cell, not a smaller pack
            var link = Samples().On(0x7E4, "220105", Patch(Cells0105, 35, "00"));
            var s = new LivePoller(new Scanner(link), Car()).Poll();

            Assert.Equal(98, s.CellVoltages!.Length);
            Assert.Equal(0, s.CellVoltages[97]);
        }

        [Fact]
        public void ADeadCellInsideThePackIsShownNotDropped()
        {
            var link = Samples().On(0x7E4, "220104", Patch(Cells0104, 4, "00"));
            var s = new LivePoller(new Scanner(link), Car()).Poll();

            Assert.Equal(98, s.CellVoltages!.Length);
            Assert.Equal(0, s.CellVoltages[64]);        // the first cell of 0x0104
        }

        [Fact]
        public void AProbeAtZeroDegreesStays()
        {
            var link = Samples().On(0x7E4, "220101", Patch(Pack0101, 19, "00"));
            var s = new LivePoller(new Scanner(link), Car()).Poll();

            Assert.Equal(new double[] { 17, 16, 15, 0 }, s.ModuleTemps);
        }

        [Fact]
        public void ReadsOnlyTheCarsEcusWithReadDataByIdentifier()
        {
            var link = Samples();
            var scanner = new Scanner(link, Car().Platform.Protocol!);
            scanner.Init();
            new LivePoller(scanner, Car()).Poll();

            var requests = link.Sent.Where(c => !c.StartsWith("AT")).ToList();
            Assert.All(requests, r => Assert.StartsWith("22", r));
            Assert.Single(requests, r => r == "220101");     // every value on 0x0101, one read
            Assert.Contains("ATSP6", link.Sent);
            // headers only of the car file's ECUs
            var ours = Car().Ecus.Select(e => $"ATSH{e.Request:X3}").ToHashSet();
            Assert.All(link.Sent.Where(c => c.StartsWith("ATSH")), h => Assert.Contains(h, ours));
        }
    }
}
