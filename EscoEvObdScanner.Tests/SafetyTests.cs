namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// What keeps the tool harmless and quick: only read-only requests, discovery
    /// kept off application message IDs, and no time wasted on a silent ECU.
    /// </summary>
    public class SafetyTests
    {
        [Fact]
        public void ReadingASilentEcuStopsEarly()
        {
            var (_, scanner, catalog) = TestData.DemoCarWithout("obc");
            var obc = catalog.Ecu("obc");
            var dids = obc.Dids.Concat(obc.Discovered).ToList();
            Assert.True(dids.Count > Snapshot.MaxSilent);

            var (readings, silent) = KnownValues.ReadEcu(scanner, obc, dids);

            Assert.True(silent);
            Assert.Equal(Snapshot.MaxSilent, readings.Count);
        }

        [Fact]
        public void ReadingAnAnsweringEcuReadsEverything()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var bms = catalog.Ecu("bms");

            var (readings, silent) = KnownValues.ReadEcu(scanner, bms, bms.Dids);

            Assert.False(silent);
            Assert.Equal(bms.Dids.Count(d => d.Did >= 0), readings.Count);
        }

        [Fact]
        public void SweepsStayAboveTheApplicationMessages()
        {
            // IDs below the floor can be application messages; a probe there can leave fault codes
            var scanner = new Scanner(new ScriptedLink());
            Assert.Throws<ArgumentOutOfRangeException>(() => scanner.DiscoverEcus(0x500, 0x5FF));
            Assert.Throws<ArgumentOutOfRangeException>(() => scanner.DiscoverEcus(0x5F0, 0x60F));
        }

        [Fact]
        public void KnownEcusAreProbedOneByOneEvenBelowTheSweepLimit()
        {
            var link = new ScriptedLink();
            var scanner = new Scanner(link);

            scanner.ProbeKnown(new[] { 0x6B4, 0x590, 0x6B4 });

            var headers = link.Sent.Where(c => c.StartsWith("ATSH")).ToList();
            Assert.Equal(new[] { "ATSH590", "ATSH6B4" }, headers);
        }

        [Fact]
        public void KnownEcuBelowTheSweepLimitIsFound()
        {
            // the charger listens on 0x590 and answers on 0x58F
            var (_, scanner, _) = TestData.DemoCar();
            var found = new List<(int, int)>();

            var r = scanner.ProbeKnown(new[] { 0x590, 0x6B4, 0x5B0 }, foundEcu: (req, resp) => found.Add((req, resp)));

            Assert.Equal(new[] { (0x590, 0x58F), (0x6B4, 0x694) }, r);
            Assert.Equal(r, found);
        }

        [Fact]
        public void KnownEcuThatIgnoresTesterPresentIsFoundByARead()
        {
            var (car, scanner, _) = TestData.DemoCar();
            Assert.Equal(new[] { (0x6A2, 0x682) }, scanner.ProbeKnown(new[] { 0x6A2 }));
            Assert.Equal(new[] { "3E00", "22F186" }, car.Requests);
        }

        [Theory]
        [InlineData("6947E00", "6947F2231", false)]                // no single-frame PCI: other traffic
        [InlineData("694017E00AAAAAAA", "NO DATA", false)]         // one byte, "7E": the 00 is padding
        [InlineData("694027E00AAAAAAAAA", "NO DATA", true)]        // two bytes, padding cut
        [InlineData("NO DATA", "694037F2231AAAAAAAA", true)]       // a refused read still proves an ECU
        public void ProbeTakesOnlySingleFramesAndCutsPadding(string testerPresent, string read, bool found)
        {
            var link = new ScriptedLink().On("3E00", testerPresent).On("22F186", read);
            var r = new Scanner(link).ProbeKnown(new[] { 0x6B4 });
            Assert.Equal(found ? new[] { (0x6B4, 0x694) } : Array.Empty<(int, int)>(), r);
        }

        [Fact]
        public void KnownProbeSkipsWhatMustNotBeProbed()
        {
            var link = new ScriptedLink();
            var probed = new List<int>();

            new Scanner(link).ProbeKnown(new[] { 0x7DF, 0x7E8, 0x0FF, 0x800, 0x6B4, 0x694 }, probing: probed.Add, skip: new[] { 0x694 });

            Assert.Equal(new[] { "ATSH6B4" }, link.Sent.Where(c => c.StartsWith("ATSH")));
            Assert.DoesNotContain(0x0FF, probed);       // outside 11-bit diagnostic IDs: not even considered
            Assert.DoesNotContain(0x800, probed);
        }

        [Fact]
        public void ResetAfterAKnownProbeIsNotPointedAtTheEcuBeforeIt()
        {
            // probing changed header and filter: the ECU targeted before it is not current any more
            var link = new ScriptedLink().On("1902FF", "ELM327 v1.4b");
            var scanner = new Scanner(link);
            scanner.Target(0x6B4, 0x694);
            scanner.ProbeKnown(new[] { 0x590 });
            link.Sent.Clear();

            scanner.Request("1902FF", 0x19);

            Assert.Contains("ATZ", link.Sent);
            Assert.DoesNotContain("ATSH6B4", link.Sent);
        }

        [Fact]
        public void KnownProbeCanBeCancelled()
        {
            var link = new ScriptedLink();
            using var cts = new CancellationTokenSource();
            new Scanner(link).ProbeKnown(new[] { 0x6A0, 0x6A1, 0x6A2 }, probing: id => { if (id == 0x6A0) cts.Cancel(); }, ct: cts.Token);
            Assert.Equal(new[] { "ATSH6A0" }, link.Sent.Where(c => c.StartsWith("ATSH")));
        }

        [Fact]
        public void RequestRefusesAnythingButReadOnlyServices()
        {
            var scanner = new Scanner(new ScriptedLink());
            Assert.Throws<ArgumentException>(() => scanner.Request("1101", 0x11));      // ECUReset
            Assert.Throws<ArgumentException>(() => scanner.Request("14FFFFFF", 0x19)); // service byte does not match
            Assert.Throws<ArgumentException>(() => scanner.Request("", 0x19));
        }

        [Fact]
        public void DiscoveryNeverTransmitsOnAReplyIdItFound()
        {
            // 0x710 answers on 0x718; the sweep must not later probe with header 0x718
            var link = new ScriptedLink().On("3E00", "718027E00");
            var scanner = new Scanner(link);
            scanner.DiscoverEcus(0x710, 0x71F);
            Assert.Contains("ATSH710", link.Sent);
            Assert.DoesNotContain("ATSH718", link.Sent);
        }
    }
}
