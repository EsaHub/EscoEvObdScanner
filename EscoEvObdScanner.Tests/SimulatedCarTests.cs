namespace EscoEvObdScanner.Tests
{
    /// <summary>The scanner end to end against the simulated car.</summary>
    public class SimulatedCarTests
    {
        [Fact]
        public void InitAndVersion()
        {
            var (_, scanner, _) = TestData.DemoCar();
            Assert.Equal(SimulatedCar.Version, scanner.Version());
        }

        [Fact]
        public void ReadsRecordedValues()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var bms = catalog.Ecu("bms");
            scanner.Target(bms.Request, bms.Response);

            var soc = scanner.ReadDid(0xD410);
            Assert.Equal(ResponseKind.Positive, soc.Kind);
            Assert.Equal(80.79, Expr.Evaluate("[B4:B5]/512", soc.Data)!.Value, 2);
        }

        [Fact]
        public void LongReplyGoesThroughMultiFrame()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var bms = catalog.Ecu("bms");
            scanner.Target(bms.Request, bms.Response);

            var cells = scanner.ReadDid(0xD440, 2000);
            Assert.Equal(ResponseKind.Positive, cells.Kind);
            Assert.Equal(240, cells.Data.Length);
        }

        [Fact]
        public void UnknownIdentifierIsOutOfRange()
        {
            var (_, scanner, catalog) = TestData.DemoCar();
            var bms = catalog.Ecu("bms");
            scanner.Target(bms.Request, bms.Response);

            var r = scanner.ReadDid(0x1234);
            Assert.Equal(ResponseKind.Negative, r.Kind);
            Assert.Equal(0x31, r.Nrc);
        }

        [Fact]
        public void NobodyHomeIsNoData()
        {
            var (_, scanner, _) = TestData.DemoCar();
            scanner.Target(0x700, 0x708);
            Assert.Equal(ResponseKind.NoData, scanner.ReadDid(0xF190).Kind);
        }

        [Fact]
        public void DiscoveryFindsEcusThatIgnoreTesterPresent()
        {
            var (_, scanner, _) = TestData.DemoCar();
            var found = scanner.DiscoverEcus(0x680, 0x7EF);

            Assert.Contains((0x6A2, 0x682), found);      // VCU: ignores 0x3E, answers F186
            Assert.Contains((0x6B4, 0x694), found);
            Assert.Contains((0x74A, 0x64A), found);
            Assert.Equal(6, found.Count);
        }

        [Fact]
        public void DiscoveryCanBeCancelled()
        {
            var (_, scanner, _) = TestData.DemoCar();
            using var cts = new CancellationTokenSource();
            int probed = 0;
            scanner.DiscoverEcus(0x600, 0x7EF, probing: _ => { if (++probed == 10) cts.Cancel(); }, ct: cts.Token);
            Assert.Equal(10, probed);
        }

        [Fact]
        public void PendingIsRetried()
        {
            var (car, scanner, catalog) = TestData.DemoCar();
            car.PendingFirst.Add((0x6B4, 0xD410));
            var bms = catalog.Ecu("bms");
            scanner.Target(bms.Request, bms.Response);

            Assert.Equal(ResponseKind.Positive, scanner.ReadDid(0xD410).Kind);
            Assert.Equal(2, car.Requests.Count(r => r == "22D410"));
        }

        [Fact]
        public void ClosedPortThrows()
        {
            var car = SimulatedCar.Demo(TestData.Catalog());
            Assert.Throws<InvalidOperationException>(() => car.Send("ATI"));
        }

        [Theory]
        [InlineData("B4", 100.0, "6400")]
        [InlineData("B4/2", 45.0, "5A00")]
        [InlineData("B4/2", 45.3, "5B00")]          // nearest step: 45.5
        [InlineData("SB4", -5.0, "FB00")]
        [InlineData("S[B4:B5]/10", -12.3, "FF85")]
        [InlineData("[B4:B5]/16", 100.0, "0640")]
        public void EncodeWritesTheValueIntoItsBytes(string expression, double value, string expected)
        {
            var encoded = SimulatedCar.Encode(new LiveSource(), expression, new byte[2], value);
            Assert.Equal(expected, Convert.ToHexString(encoded!));
        }

        [Theory]
        [InlineData("[B4:B5]*[B4:B5]", 400.0)]      // not linear
        [InlineData("B4", 300.0)]                   // does not fit one byte
        [InlineData("SB4", 200.0)]                  // does not fit a signed byte
        public void EncodeGivesUpOnWhatItCannotGiveBack(string expression, double value)
        {
            Assert.Null(SimulatedCar.Encode(new LiveSource(), expression, new byte[2], value));
        }

        const string SharedReplyCar = """
            {
              "live": {
                "speed":   { "ecu": "bms", "did": "0x0101", "poll": "fast", "expression": "B4" },
                "current": { "ecu": "bms", "did": "0x0101", "poll": "fast", "expression": "S[B5:B6]/10" },
                "voltage": { "ecu": "bms", "did": "0x0101", "poll": "fast", "expression": "[B7:B8]/10" },
                "motor_rpm": { "ecu": "bms", "did": "0x0104", "poll": "fast", "expression": "[B4:B5]*[B4:B5]" },
                "cells":   { "ecu": "bms", "poll": "cells", "bytes": 1, "scale": 0.02,
                             "parts": [ { "did": "0x0102", "skip": 1, "count": 2 }, { "did": "0x0103" } ] }
              },
              "platform": { "demo_drive": { "rpm_per_kmh": 100 } },
              "ecus": [ { "id": "bms", "request": "0x7E4", "response": "0x7EC", "dids": [] } ]
            }
            """;

        static (Scanner Scanner, Catalog Catalog) Animated()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "shared.json");
            File.WriteAllText(path, SharedReplyCar);
            var catalog = Catalog.Load(path);
            var car = new SimulatedCar(catalog, new[]
            {
                """{"ecu":"bms","did":"0x0101","hex":"0000000FA0"}""",     // speed 0, current 0, voltage 400.0
                """{"ecu":"bms","did":"0x0102","hex":"FFA0A1A2"}""",       // a byte to skip, two cells, one byte that is not a cell
                """{"ecu":"bms","did":"0x0103","hex":"A3A4"}""",
                """{"ecu":"bms","did":"0x0104","hex":"0010"}"""
            }) { Dynamic = true };
            car.Open();
            var scanner = new Scanner(car);
            scanner.Init();
            scanner.Target(0x7E4, 0x7EC);
            return (scanner, catalog);
        }

        [Fact]
        public void EveryValueSharingOneReplyMoves()
        {
            // the drive starts at about 55 km/h and 50 A: speed, current and voltage all change in the one reply
            var (scanner, _) = Animated();
            var data = scanner.ReadDid(0x0101).Data;
            Assert.InRange(Expr.Evaluate("B4", data)!.Value, 40, 70);
            Assert.InRange(Expr.Evaluate("S[B5:B6]/10", data)!.Value, 25, 75);
            Assert.InRange(Expr.Evaluate("[B7:B8]/10", data)!.Value, 393, 398);
        }

        [Fact]
        public void CellsSagInEveryPartWithItsOwnLayout()
        {
            var (scanner, _) = Animated();
            var first = scanner.ReadDid(0x0102).Data;
            var second = scanner.ReadDid(0x0103).Data;
            Assert.Equal(0xFF, first[0]);                       // skipped
            Assert.True(first[1] < 0xA0 && first[2] < 0xA1);
            Assert.Equal(0xA2, first[3]);                       // past the part's count
            Assert.True(second[0] < 0xA3 && second[1] < 0xA4);
        }

        [Fact]
        public void AValueThatCannotBeEncodedKeepsItsRecording()
        {
            var (scanner, _) = Animated();
            Assert.Equal("0010", Convert.ToHexString(scanner.ReadDid(0x0104).Data));
        }

        [Fact]
        public void NeverSendsWritingServices()
        {
            // Everything the scanner can do, then check no request started with a
            // service that changes state: 0x10 session, 0x11 reset, 0x14 clear,
            // 0x27 security, 0x28 communication control, 0x2E write, 0x2F I/O control,
            // 0x31 routine, 0x34-0x37 download/upload, 0x3D write memory, 0x85 DTC setting;
            // and that every request is one of 0x22, 0x24, 0x19 and 0x3E.
            var (car, scanner, catalog) = TestData.DemoCar();
            scanner.DiscoverEcus(0x6A0, 0x6BF);
            foreach (var e in catalog.Ecus)
            {
                scanner.Target(e.Request, e.Response);
                scanner.ReadDid(0xF18C);
                scanner.ReadScaling(0xD410);
                scanner.Request(DtcRequests.List(), 0x19);
                scanner.Request(DtcRequests.Snapshot(0x108309), 0x19);
                scanner.Request(DtcRequests.Extended(0x108309), 0x19);
            }
            new LivePoller(scanner, catalog).Poll();
            Snapshot.Take(scanner, catalog, Snapshot.Targets(catalog), "", TextWriter.Null);

            var forbidden = new[] { "10", "11", "14", "27", "28", "2E", "2F", "31", "34", "35", "36", "37", "3D", "85" };
            Assert.DoesNotContain(car.Requests, r => forbidden.Contains(r.Substring(0, 2)));
            Assert.All(car.Requests, r => Assert.Contains(r.Substring(0, 2), new[] { "22", "24", "19", "3E" }));
        }
    }
}
