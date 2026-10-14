using System.Globalization;

namespace EscoEvObdScanner.Tests
{
    /// <summary>The shipped car files, the read-only guarantee for every service byte, that reading the texts sends only their identifiers, and the recordings that older and newer versions write.</summary>
    public class ReleaseGuardTests
    {
        [Fact]
        public void ShippedCarFilesCarryNoUserChangesMark()
        {
            // a merge into the repo copy sets the mark; shipped, it would make every working copy look edited by its user
            string cars = Path.GetDirectoryName(TestData.CatalogPath)!;
            var files = Directory.GetFiles(cars, "*.json");
            Assert.NotEmpty(files);
            Assert.All(files, f => Assert.False(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))?["user_changes"] != null, Path.GetFileName(f)));
        }

        [Fact]
        public void OnlyTheFourReadingServicesExist()
        {
            Assert.Equal(new byte[] { 0x19, 0x22, 0x24, 0x3E }, Scanner.ReadOnlyServices.Order());
        }

        [Fact]
        public void EveryOtherServiceIsRefused()
        {
            var scanner = new Scanner(new ScriptedLink());
            for (int sid = 0; sid <= 0xFF; sid++)
            {
                if (Scanner.ReadOnlyServices.Contains((byte)sid)) continue;
                int s = sid;
                Assert.Throws<ArgumentException>(() => scanner.Request($"{s:X2}00", (byte)s));
            }
            // clearing faults, security access and routines by name
            Assert.Throws<ArgumentException>(() => scanner.Request("14FFFFFF", 0x14));
            Assert.Throws<ArgumentException>(() => scanner.Request("2701", 0x27));
            Assert.Throws<ArgumentException>(() => scanner.Request("3101FF00", 0x31));
        }

        [Fact]
        public void ReadingTheTextsSendsOnlyTheirIdentifiers()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            int before = car.Requests.Count;

            LivePoller.ReadTexts(scanner, catalog, new VehicleState());

            var sent = car.Requests.Skip(before).ToList();
            Assert.NotEmpty(sent);
            Assert.All(sent, r => Assert.Contains(r, new[] { "22F18B", "22F195", "22F193" }));
        }

        static string Dir()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void AnOldRecordingWithoutTheCountersReads()
        {
            var live = Path.Combine(Dir(), "live_old.csv");
            File.WriteAllLines(live, new[] { "time;soc_pct;aux_v", "2026-09-29 19:00:00.000;76.1;12.4" });

            var s = Assert.Single(Recording.Read(live));

            Assert.Equal(76.1, s.Soc);
            Assert.Null(s.ContactorCloses);
            Assert.Null(s.TimeAbove55);
            Assert.Null(s.LifetimeChargedKwh);
            Assert.False(s.HasLifetimeCounters);
        }

        [Fact]
        public void ANewRecordingReadsBackItsCountersInAnyCulture()
        {
            var live = Path.Combine(Dir(), "live_new.csv");
            File.WriteAllLines(live, new[]
            {
                "time;soc_pct;lifetime_charged_kwh;contactor_closes;time_above_55c",
                "2026-10-07 10:00:00.000;80.5;13729.4;3926;0"
            });
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");      // decimal comma
                var s = Assert.Single(Recording.Read(live));
                Assert.Equal(80.5, s.Soc);
                Assert.Equal(13729.4, s.LifetimeChargedKwh);
                Assert.Equal(3926, s.ContactorCloses);
                Assert.Equal(0, s.TimeAbove55);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
    }
}
