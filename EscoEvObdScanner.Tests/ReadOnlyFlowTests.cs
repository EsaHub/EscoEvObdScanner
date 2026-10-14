using System.Text.RegularExpressions;

namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The read-only guarantee over everything the app and the command line do with the adapter,
    /// not just the scanner's own methods: every diagnostic request is a read, and every adapter
    /// command is one of a known, harmless set (none that writes the adapter's own memory).
    /// </summary>
    public class ReadOnlyFlowTests
    {
        /// <summary>Passes everything on to the simulated car and keeps a list of what was sent.</summary>
        sealed class Recorder(IElmLink inner) : IElmLink
        {
            public List<string> Sent { get; } = new();
            public event Action<string>? Trace { add => inner.Trace += value; remove => inner.Trace -= value; }
            public bool IsOpen => inner.IsOpen;
            public void Open() => inner.Open();
            public void Dispose() => inner.Dispose();
            public bool LastReplyEchoed => inner.LastReplyEchoed;
            public string[] Send(string command, int timeoutMs = 5000)
            {
                Sent.Add(command);
                return inner.Send(command, timeoutMs);
            }
        }

        static readonly Regex AdapterCommand = new(
            "^AT(Z|E0|L0|S0|H0|H1|I|DP|RV|CAF1|AT0|SP[0-9A-C]|ST[0-9A-F]{2}|SH[0-9A-F]{3}|CRA([0-9A-F]{3})?|CF400|CM400|FCSH[0-9A-F]{3}|FCSD300000|FCSM1)$",
            RegexOptions.IgnoreCase);

        [Fact]
        public void EveryFlowSendsOnlyReadsAndKnownAdapterCommands()
        {
            var catalog = TestData.Catalog();
            var car = SimulatedCar.Demo(catalog);
            var link = new Recorder(car);
            link.Open();
            var scanner = new Scanner(link);
            scanner.Init();

            // what connecting, the dashboard, the ECUs tab, a snap, a scan, the ECU search and the fault codes do
            VehicleIdentity.Check(scanner, catalog);
            VehicleIdentity.ReadVin(scanner, catalog);
            new LivePoller(scanner, catalog).Poll();
            KnownValues.Read(scanner, catalog);
            Snapshot.Take(scanner, catalog, Snapshot.Targets(catalog), "", TextWriter.Null);
            DidScan.Run(scanner, catalog, catalog.Ecu("bms"), 0xD800, 0xD83F, TextWriter.Null);
            scanner.DiscoverEcus(0x680, 0x6BF);
            DtcReader.ReadClock(scanner, catalog);
            foreach (var ecu in catalog.Ecus) DtcReader.Read(scanner, ecu, deep: true);

            var requests = link.Sent.Where(c => !c.StartsWith("AT", StringComparison.OrdinalIgnoreCase)).ToList();
            var commands = link.Sent.Where(c => c.StartsWith("AT", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(requests);
            Assert.All(requests, r => Assert.Contains(r[..2], new[] { "22", "19", "3E", "24" }));
            Assert.All(commands, c => Assert.Matches(AdapterCommand, c));
        }
    }
}
