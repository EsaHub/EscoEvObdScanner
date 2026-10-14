namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The program knows no car: everything comes from the car file. These use
    /// a made-up car laid out the way some makes do it - several values packed
    /// into one reply, cells split over two - to show that nothing of e-CMP is
    /// assumed.
    /// </summary>
    public class CarFileTests
    {
        const string OtherCar = """
            {
              "platform": { "name": "Test EV", "probe_from": "0x700", "identifying": ["0x0200"],
                            "gauges": { "power_kw": [-100, 200] } },
              "live": {
                "soc":     { "ecu": "bms", "did": "0x0101", "poll": "fast", "expression": "B4/2" },
                "voltage": { "ecu": "bms", "did": "0x0101", "poll": "fast", "expression": "[B5:B6]/10" },
                "cells":   { "ecu": "bms", "dids": ["0x0102", "0x0103"], "poll": "cells", "bytes": 1, "scale": 0.02,
                             "min": 2, "max": 4.5, "columns": 8 },
                "gear":    { "ecu": "vcu", "did": "0x0104", "poll": "slow", "map": { "1": "D" } }
              },
              "ecus": [
                { "id": "bms", "name": { "fi": "Akunhallinta", "en": "BMS" }, "request": "0x7E4", "response": "0x7EC", "dids": [], "discovered": [] },
                { "id": "vcu", "name": { "fi": "VCU" }, "request": "0x7E2", "response": "0x7EA", "dids": [], "discovered": [] }
              ],
              "blocks": []
            }
            """;

        static Catalog Load(string json)
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "test-ev.json");
            File.WriteAllText(path, json);
            return Catalog.Load(path);
        }

        [Fact]
        public void DashboardComesFromTheCarFile()
        {
            var link = new ScriptedLink()
                .On("220101", "6201015A0F3C")       // soc 0x5A / 2 = 45 %, voltage 0x0F3C / 10 = 390 V
                .On("220102", "620102A0A1A2")       // cells 3.20, 3.22, 3.24 V
                .On("220103", "620103A3A4")         // cells 3.26, 3.28 V
                .On("220104", "62010401");          // gear code 1
            var catalog = Load(OtherCar);

            var s = new LivePoller(new Scanner(link), catalog).Poll();

            Assert.Equal(45, s.Soc);
            Assert.Equal(390, s.Voltage);
            Assert.Equal(new[] { 3.20, 3.22, 3.24, 3.26, 3.28 }, s.CellVoltages!.Select(v => Math.Round(v, 2)));
            Assert.Equal(1, s.Gear);
            Assert.Equal("D", catalog.LiveOf("gear")!.Text(s.Gear));
            Assert.Equal(8, catalog.LiveOf("cells")!.Columns);

            // two values from one reply: asked for once, with the ECU each lives on
            Assert.Single(link.Sent, c => c == "220101");
            Assert.Contains("ATSH7E4", link.Sent);
            Assert.Contains("ATSH7E2", link.Sent);
        }

        [Fact]
        public void ValuesTheCarFileDoesNotListStayEmpty()
        {
            // the car answers everything it is asked; the values it has no entry for still stay empty
            var link = new ScriptedLink()
                .On("220101", "6201015A0F3C")
                .On("220102", "620102A0A1A2")
                .On("220103", "620103A3A4")
                .On("220104", "62010401");
            var s = new LivePoller(new Scanner(link), Load(OtherCar)).Poll();
            Assert.Equal(45, s.Soc);
            Assert.Null(s.Current);
            Assert.Null(s.Speed);
            Assert.Null(s.Soh);
        }

        [Fact]
        public void PlatformSettingsComeFromTheFile()
        {
            var c = Load(OtherCar);
            Assert.Equal("Test EV", c.Name);
            Assert.Equal(0x700, c.Platform.ProbeFrom);
            Assert.Equal(new[] { -100.0, 200 }, c.Platform.Gauges.PowerKw);
            Assert.Equal(new[] { -150.0, 300 }, c.Platform.Gauges.CurrentA);       // not given: the default
            Assert.Equal(new[] { 0x0200, 0xF18C, 0xF190 }.Order(), c.Identifying.Order());
        }

        [Fact]
        public void AnOddFileLoadsAsFarAsItMakesSense()
        {
            // nulls, a typo in an identifier, an ECU without an address, a "poll" value with a capital, one live name spelt twice
            var c = Load("""
                {
                  "platform": { "name": "Odd", "identifying": null, "fault_time": { "odometer_dids": null } },
                  "live": {
                    "soc":  { "ecu": "bms", "did": "0x1F190", "poll": "Fast" },
                    "SOC":  { "ecu": "bms", "did": "0x0101" },
                    "voltage": null,
                    "cells": { "ecu": "bms", "parts": [ null, { "did": "0x0102", "skip": 4, "count": 2 } ] }
                  },
                  "ecus": [
                    { "id": "bms", "request": "0x7E4", "response": "0x7EC",
                      "dids": [ null, { "did": "0x1F190" }, { "did": "0x0105" } ], "discovered": null },
                    { "id": "broken", "request": "", "response": "0x7EA", "dids": [] },
                    null
                  ],
                  "blocks": null
                }
                """);
            Assert.Equal(new[] { "bms" }, c.Ecus.Select(e => e.Id));          // no address, no ECU
            Assert.Equal("fast", c.LiveOf("soc")!.Poll);                      // of "soc" and "SOC" the first wins; "Fast" reads as "fast"
            Assert.Empty(c.LiveOf("soc")!.Dids);                               // 0x1F190 is no identifier
            Assert.Null(c.LiveOf("voltage"));
            Assert.Single(c.LiveOf("cells")!.Pieces);
            Assert.Empty(c.Platform.FaultTime!.Odometers);
            Assert.Equal(new[] { -1, 0x0105 }, c.Dids.Select(d => d.Did));
            // and polling it reads only what can be read
            new LivePoller(new Scanner(new ScriptedLink()), c).Poll();
        }

        [Fact]
        public void OnCodesApplyOnlyToYesNoValues()
        {
            var c = Load("""
                {
                  "live": {
                    "charging": { "ecu": "bms", "did": "0x0101", "expression": "B4", "on": [2] },
                    "soc":      { "ecu": "bms", "did": "0x0101", "expression": "B5", "on": [2] }
                  },
                  "ecus": [ { "id": "bms", "request": "0x7E4", "response": "0x7EC", "dids": [] } ]
                }
                """);
            var s = new LivePoller(new Scanner(new ScriptedLink().On("220101", "620101023C")), c).Poll();
            Assert.True(s.Charging);
            Assert.Equal(60, s.Soc);
        }

        [Fact]
        public void AnEcuWithARequestBelow0x100IsLeftOut()
        {
            // the scanner would refuse to target it; an ECU search reaching past 0x7FF is no request range
            var c = Load("""
                {
                  "ecus": [
                    { "id": "bms", "request": "0x7E4", "response": "0x7EC", "dids": [] },
                    { "id": "low", "request": "0x0A2", "response": "0x0A3", "dids": [] }
                  ],
                  "ecu_search": [ { "range": "0x700-0x7DF" }, { "range": "0x700-0x8FF" } ]
                }
                """);
            Assert.Equal(new[] { "bms" }, c.Ecus.Select(e => e.Id));
            Assert.Equal(new[] { 0x7DF }, c.EcuSearch.Select(s => s.To));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Scanner(new ScriptedLink()).Target(0x0A2, 0x0A3));
        }

        [Fact]
        public void NamesComePerLanguage()
        {
            // per language, a plain string (English), or something that is no name
            var c = Load("""
                {
                  "ecus": [
                    { "id": "bms", "name": { "fi": "Akunhallinta", "en": "Battery management" }, "request": "0x7E4", "response": "0x7EC",
                      "dids": [ { "did": "0x0101", "name": { "fi": "Varaus", "en": "Charge", "sv": 3 } },
                                { "did": "0x0102", "name": "Cell voltages" },
                                { "did": "0x0103", "name": [ "odd" ] },
                                { "did": "0x0104", "name": null },
                                { "did": "0x0105", "name": { "en": " ", "EN": "Second" } },
                                { "did": "0x0106", "name": { } } ] }
                  ]
                }
                """);
            Assert.Equal("Battery management", CarText.English(c.Ecus[0].Names));
            Assert.Equal(new Dictionary<string, string> { ["fi"] = "Varaus", ["en"] = "Charge" }, c.Dids[0].Names);
            Assert.Equal("Cell voltages", c.Dids[1].EnglishName);
            Assert.Null(c.Dids[2].EnglishName);
            Assert.Equal("", c.Dids[3].Name);
            Assert.Single(c.Dids[4].Names!);                     // the blank text is no text, so the second spelling counts
            Assert.Equal("Second", c.Dids[4].EnglishName);
            Assert.Null(c.Dids[5].Names);
        }

        [Fact]
        public void WithoutASpreadValueTheSpreadIsMaxMinusMin()
        {
            var c = Load("""
                {
                  "live": {
                    "temp_min": { "ecu": "bms", "did": "0x0001", "expression": "B4" },
                    "temp_max": { "ecu": "bms", "did": "0x0002", "expression": "B4" }
                  },
                  "ecus": [ { "id": "bms", "request": "0x7E4", "response": "0x7EC", "dids": [] } ]
                }
                """);
            var car = new SimulatedCar(c, new[] { """{"ecu":"bms","did":"0x0001","hex":"0A"}""", """{"ecu":"bms","did":"0x0002","hex":"0E"}""" });
            car.Open();
            var scanner = new Scanner(car);
            scanner.Init();
            var s = new LivePoller(scanner, c).Poll();
            Assert.Equal(10, s.TempMin);
            Assert.Equal(14, s.TempMax);
            Assert.Equal(4, s.TempDelta);
        }

        [Fact]
        public void AFileWithoutPlatformGetsSafeDefaults()
        {
            var c = Load("""{ "ecus": [], "blocks": [] }""");
            Assert.Equal(Scanner.DefaultProbeFloor, c.Platform.ProbeFrom);
            Assert.Null(c.Platform.Protocol);
            Assert.Null(c.Platform.Demo);
            Assert.Empty(c.Live);
            Assert.Equal(new[] { 0xF18C, 0xF190 }.Order(), c.Identifying.Order());
        }

        [Fact]
        public void TheProtocolComesFromTheCarFile()
        {
            var link = new ScriptedLink();
            new Scanner(link, "8").Init();
            Assert.Contains("ATSP8", link.Sent);

            // 29-bit CAN and automatic search are not supported, and nothing else may reach the adapter
            Assert.Throws<ArgumentException>(() => new Scanner(new ScriptedLink(), "7"));
            Assert.Throws<ArgumentException>(() => new Scanner(new ScriptedLink(), "0"));
            Assert.Throws<ArgumentException>(() => new Scanner(new ScriptedLink(), "6\r1101"));

            var defaults = new ScriptedLink();
            new Scanner(defaults).Init();
            Assert.Contains("ATSP6", defaults.Sent);
        }

        [Fact]
        public void DiscoveryKeepsAboveTheCarsFloor()
        {
            var scanner = new Scanner(new ScriptedLink()) { ProbeFloor = 0x700 };
            Assert.Throws<ArgumentOutOfRangeException>(() => scanner.DiscoverEcus(0x680, 0x6FF));
            scanner.DiscoverEcus(0x700, 0x701);
        }

        [Fact]
        public void CarsAreFoundByNameInTheCarsFolders()
        {
            var a = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            var b = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            File.WriteAllText(Path.Combine(a, "one.json"), "{}");
            File.WriteAllText(Path.Combine(b, "one.json"), "{}");
            File.WriteAllText(Path.Combine(b, "two.json"), "{}");

            var cars = CarFiles.In(a, b);
            Assert.Equal(new[] { "one", "two" }, cars.Select(CarFiles.NameOf));
            Assert.StartsWith(a, cars[0]);                              // the first folder wins a clash
            Assert.Equal(Path.Combine(b, "two.json"), CarFiles.Find("two", a, b));
            Assert.Equal(Path.Combine(b, "two.json"), CarFiles.Find("two.json", a, b));
            Assert.Null(CarFiles.Find("three", a, b));
        }
    }
}
