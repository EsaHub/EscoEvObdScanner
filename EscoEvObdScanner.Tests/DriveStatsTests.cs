namespace EscoEvObdScanner.Tests
{
    /// <summary>What a session adds to the battery report: distance, energy and the cells under load.</summary>
    public class DriveStatsTests
    {
        static readonly DateTime T0 = new(2026, 10, 2, 17, 0, 0);

        // ten cells: cell 7 sits 4 mV low at rest and under load alike (less charge),
        // cell 4 sags 15 mV more than the others under load only (more resistance)
        static double[] Cells(bool load) =>
            Enumerable.Range(1, 10).Select(k => (load ? 3.800 : 3.900) - (k == 7 ? 0.004 : 0) - (load && k == 4 ? 0.015 : 0)).ToArray();

        // cellsAt: when the cells shown were read (the last read, as the poller keeps it)
        static VehicleState State(int second, double speed, double current, int cellsAt, bool load, int cellCount = 10) => new()
        {
            Time = T0.AddSeconds(second),
            Speed = speed,
            Voltage = 400,
            Current = current,
            ModuleTemps = new double[] { 14, 15 + second / 600.0 },
            CellVoltages = Cells(load).Take(cellCount).ToArray(),
            CellsTime = T0.AddSeconds(cellsAt),
            CellsCurrent = current
        };

        static DriveStats Drive()
        {
            var d = new DriveStats();
            for (int s = 0; s <= 180; s++)                              // three minutes standing, cells read every minute
                d.Add(State(s, 0, 1, s / 60 * 60, false));
            for (int s = 181; s <= 780; s++)                            // ten minutes at 60 km/h, 50 A (20 kW)
                d.Add(State(s, 60, 50, s < 240 ? 180 : s / 60 * 60, s >= 240));
            d.Add(State(781, 0, 2, 781, false));                        // stopped
            return d;
        }

        [Fact]
        public void DistanceAndEnergyComeFromSpeedAndPower()
        {
            var d = Drive();
            Assert.Equal(10, d.Km, 1);
            Assert.Equal(20.0 * 600 / 3600, d.KwhOut, 2);
            Assert.Equal(33.5, d.Consumption!.Value, 1);          // 20 kW for 600 s over 10 km, plus three minutes standing at 0.4 kW
            Assert.Equal(50, d.MaxCurrent);
            Assert.Equal(600, d.MovingSeconds, 0);
        }

        [Fact]
        public void OnlyTheExtraSagUnderLoadCountsAsResistance()
        {
            var d = Drive();
            Assert.Equal(10, d.LoadReads);
            Assert.Equal(5, d.RestReads);
            var weakest = d.WeakestUnderLoad(10);

            // cell 4: 13.1 mV below the average under load, 0.4 mV above it at rest: 13.5 mV extra
            Assert.Equal(4, weakest[0].Cell);
            Assert.Equal(13.5, weakest[0].ExtraMv, 1);
            Assert.Equal(13.1, weakest[0].LoadMv, 1);
            // at 50 A that is 27 mV per 100 A: clearly higher resistance
            Assert.Equal(27.0, weakest[0].ExtraPer100A, 1);
            Assert.Equal(Grade.Bad, BatteryChecks.CellUnderLoad(weakest[0].ExtraPer100A, d.LoadCurrentScaled, d.NoiseFloorMv));
            Assert.Equal(50, d.LoadCurrentScaled!.Value, 1);
            Assert.Equal(d.WeakestUnderLoad(99).Count, d.ExtraPer100AByCell()!.Length);

            // cell 7 is low at rest and under load alike: no extra, so not a weak cell
            var seven = weakest.Single(c => c.Cell == 7);
            Assert.True(seven.ExtraMv < DriveStats.NoiseMv, $"{seven.ExtraMv}");
            Assert.Equal(Grade.Good, BatteryChecks.CellUnderLoad(seven.ExtraPer100A, d.LoadCurrentScaled, d.NoiseFloorMv));
            Assert.Equal(7, d.LowestAtRest(1)[0].Cell);
            Assert.Equal(3.6, d.LowestAtRest(1)[0].Mv, 1);

            Assert.Equal(4, d.CellStepMv!.Value, 1);
            Assert.Equal(DriveStats.NoiseMv, d.NoiseFloorMv);
            Assert.Equal(4, d.RestSpreadMax!.Value, 1);
            Assert.Equal(T0.AddSeconds(781), d.LastRestTime);
        }

        [Fact]
        public void TheExtraIsScaledPerReadToOneHundredAmps()
        {
            // cell 2 sits 3 mV low in every load read, once at 40 A and twice at 120 A: each read is scaled by its own current
            var d = new DriveStats();
            var t = T0;
            VehicleState Read(double amps, double extraMv, int i) => new()
            {
                Time = t.AddSeconds(i), CellsTime = t.AddSeconds(i), Current = amps, CellsCurrent = amps,
                CellVoltages = new[] { 3.900, 3.900 - extraMv / 1000, 3.900, 3.900 }
            };
            int n = 0;
            for (int k = 0; k < 3; k++) d.Add(Read(0, 0, n++));
            d.Add(Read(40, 3, n++));            // 3 mV at 40 A  = 7.5 mV / 100 A
            d.Add(Read(120, 3, n++));           // 3 mV at 120 A = 2.5 mV / 100 A
            d.Add(Read(120, 3, n++));
            var cell2 = d.WeakestUnderLoad(4).Single(c => c.Cell == 2);
            // per read (7.5 + 2.5 + 2.5) / 3 = 4.17 of the cell's offset to the average, which is 3/4 of its sag
            Assert.Equal((7.5 + 2.5 + 2.5) / 3 * 0.75, cell2.ExtraPer100A, 3);
            // the plain average (3 mV) scaled by the average current (93.3 A) would give a different figure
            Assert.NotEqual(3 * 0.75 * 100 / ((40 + 120 + 120) / 3.0), cell2.ExtraPer100A, 2);
            Assert.Equal(3 * 100 / (100 / 40.0 + 100 / 120.0 * 2), d.LoadCurrentScaled!.Value, 2);
        }

        [Fact]
        public void LimitsRiseWithTheNoiseAtLowCurrents()
        {
            Assert.Equal(BatteryChecks.CellLoadWatchMv, BatteryChecks.CellLoadWatchAt(150));
            Assert.Equal(BatteryChecks.CellLoadBadMv, BatteryChecks.CellLoadBadAt(150));
            Assert.Equal(12.5, BatteryChecks.CellLoadWatchAt(40), 2);
            Assert.Equal(25, BatteryChecks.CellLoadBadAt(40), 2);
            // 20 mV / 100 A at 40 A is 8 mV of sag: watch, not poor
            Assert.Equal(Grade.Watch, BatteryChecks.CellUnderLoad(20, 40));
            Assert.Equal(Grade.Bad, BatteryChecks.CellUnderLoad(20, 150));
        }

        [Fact]
        public void AColdPackRatesACellUnderLoadAtMostWatch()
        {
            Assert.Equal(Grade.Watch, BatteryChecks.CellUnderLoad(30, 150, packTempC: 5));
            Assert.Equal(Grade.Bad, BatteryChecks.CellUnderLoad(30, 150, packTempC: 12));
            Assert.Equal(Grade.Bad, BatteryChecks.CellUnderLoad(30, 150));
            Assert.Equal(Grade.Good, BatteryChecks.CellUnderLoad(1, 150, packTempC: 5));
        }

        [Fact]
        public void TheLoadTemperatureIsTheColdestProbeUnderLoad()
        {
            var d = new DriveStats();
            VehicleState Read(int i, double amps, double[] probes) => new()
            {
                Time = T0.AddSeconds(i), CellsTime = T0.AddSeconds(i), Current = amps, CellsCurrent = amps,
                CellVoltages = new[] { 3.900, 3.901 }, ModuleTemps = probes
            };
            d.Add(Read(0, 0, new[] { -2.0, 4 }));       // at rest: does not count
            d.Add(Read(1, 80, new[] { 6.0, 9 }));
            d.Add(Read(2, 90, new[] { 7.0, 12 }));
            Assert.Equal(6, d.LoadTempMin);
            // a probe that is no number does not hide the cold ones
            d.Add(Read(3, 90, new[] { double.NaN, 4 }));
            Assert.Equal(4, d.LoadTempMin);
        }

        [Fact]
        public void CoarseCellStepsRaiseTheNoiseFloor()
        {
            // a car that gives cell voltages in 20 mV steps (the Kia): a cell 10 mV low under load shows in every other read
            var d = new DriveStats();
            var t = T0;
            VehicleState Read(double amps, bool low, int i) => new()
            {
                Time = t.AddSeconds(i), CellsTime = t.AddSeconds(i), Current = amps, CellsCurrent = amps,
                CellVoltages = new[] { 3.880, low ? 3.860 : 3.880, 3.880, 3.860 }
            };
            int n = 0;
            for (int k = 0; k < 4; k++) d.Add(Read(0, false, n++));
            for (int k = 0; k < 8; k++) d.Add(Read(50, k % 2 == 0, n++));
            Assert.Equal(20, d.CellStepMv!.Value, 1);
            Assert.Equal(10, d.NoiseFloorMv);
            var cell2 = d.WeakestUnderLoad(1)[0];
            Assert.Equal(2, cell2.Cell);
            // within one step of the others: not a cell to watch at this resolution, though it would be at 1 mV steps
            Assert.Equal(Grade.Watch, BatteryChecks.CellUnderLoad(cell2.ExtraPer100A, d.LoadCurrentScaled));
            Assert.Equal(Grade.Good, BatteryChecks.CellUnderLoad(cell2.ExtraPer100A, d.LoadCurrentScaled, d.NoiseFloorMv));
        }

        [Fact]
        public void TheStepIsTheSmallestDifferenceInARead()
        {
            Assert.Equal(20, DriveStats.StepMv(new[] { 3.88, 3.86, 3.88, 3.84 })!.Value, 1);
            Assert.Equal(1, DriveStats.StepMv(new[] { 3.901, 3.903, 3.900 })!.Value, 1);
            Assert.Null(DriveStats.StepMv(new[] { 3.96, 3.96 }));
        }

        [Fact]
        public void TooFewReadsRankNothing()
        {
            var d = new DriveStats();
            d.Add(State(0, 0, 1, 0, false));
            d.Add(State(1, 30, 60, 1, true));
            Assert.False(d.CanRankCells);
            Assert.Empty(d.WeakestUnderLoad(5));
            Assert.Null(d.Consumption);
        }

        [Fact]
        public void AnotherCellCountStartsEveryCellFigureOver()
        {
            var d = new DriveStats();
            d.Add(State(0, 0, 1, 0, false, cellCount: 8));              // a short first read
            d.Add(State(10, 0, 1, 10, false, cellCount: 8));
            d.Add(State(20, 0, 1, 20, false));                           // then the whole pack
            Assert.Equal(1, d.CellReads);
            Assert.Equal(1, d.RestReads);
            Assert.Equal(T0.AddSeconds(20), d.LastRestTime);
        }

        [Fact]
        public void ARecordingWithAHeaderNamedTwiceStillReads()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var live = Path.Combine(dir, "live_2.csv");
            // two empty column names after a hand edit
            File.WriteAllLines(live, new[] { "time;soc_pct;;", "2026-10-02 17:00:00.000;80;;" });
            var s = Assert.Single(Recording.Read(live));
            Assert.Equal(80, s.Soc);
        }

        [Fact]
        public void CellsWithoutProbesAndProbesLaterBothReadBack()
        {
            // the probes did not answer at first: the cells file has no probe columns, and the
            // probes that came later started a second cells file with them
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var live = Path.Combine(dir, "live_4.csv");
            File.WriteAllLines(live, new[] { "time;current_a", "2026-10-02 17:00:01.000;1", "2026-10-02 17:00:11.000;1" });
            File.WriteAllLines(Recording.CellsFile(live), new[] { "time;cell_1_v;cell_2_v", "2026-10-02 17:00:00.500;3.901;3.899" });
            File.WriteAllLines(Path.Combine(dir, "live_4_cells_2.csv"), new[] { "time;cell_1_v;cell_2_v;probe_1_c", "2026-10-02 17:00:10.500;3.902;3.900;14" });

            var states = Recording.Read(live);
            Assert.Equal(new[] { 3.901, 3.899 }, states[0].CellVoltages);
            Assert.Null(states[0].ModuleTemps);
            Assert.Equal(new double[] { 14 }, states[1].ModuleTemps);
            Assert.Equal(3.902, states[1].CellVoltages![0]);
        }

        [Fact]
        public void CellHealthComesFromItsOwnFileAsLastRead()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var live = Path.Combine(dir, "live_3.csv");
            File.WriteAllLines(live, new[] { "time;soc_pct", "2026-10-02 17:00:00.000;80", "2026-10-02 17:00:10.000;80", "2026-10-02 17:00:20.000;80" });
            // written when the health changed: read at 17:00:05, then one cell lower at 17:00:15
            File.WriteAllLines(Recording.HealthFile(live), new[] { "time;health_1_pct;health_2_pct;health_3_pct",
                "2026-10-02 17:00:05.000;100;96.625;99.5", "2026-10-02 17:00:15.000;100;96.5;99.5" });

            var states = Recording.Read(live);
            Assert.Null(states[0].CellSoh);                                 // before the first health read
            Assert.Equal(new[] { 100, 96.625, 99.5 }, states[1].CellSoh);
            Assert.Equal(96.5, states[2].CellSoh![1]);
            // the health file is part of the recording, not a recording of its own
            Assert.True(Recording.IsCellsFile(Recording.HealthFile(live)));
            Assert.Equal(live, Recording.MainFile(Recording.HealthFile(live)));
        }

        [Fact]
        public void ARecordingReadsBackAsTheReadingsItWasMadeOf()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var live = Path.Combine(dir, "live_1.csv");
            File.WriteAllLines(live, new[]
            {
                "time;soc_pct;voltage_v;current_a;power_kw;speed_kmh;temp_max_c;odometer_km;gear",
                "2026-10-02 17:00:00.000;80;425.5;1.5;0.6;0;15;59381;3",
                "2026-10-02 17:00:02.000;80;420.0;100;42;50;15;59381;3",
                "2026-10-02 17:00:04.000;79.9;419.0;-20;-8.4;52;15;59381;3",
                "",
                "2026-10-02 17:00:06.000;79.9;419.0;10;4.2;52;15;59381;3"
            });
            // the first read came short (blank columns), the full read went to a second file
            File.WriteAllLines(Recording.CellsFile(live), new[]
            {
                "time;cell_1_v;cell_2_v;probe_1_c",
                "2026-10-02 17:00:01.500;3.801;;14",
                "not a time;3.9;3.9;14"
            });
            File.WriteAllLines(Path.Combine(dir, "live_1_cells_2.csv"), new[]
            {
                "time;cell_1_v;cell_2_v;cell_3_v;probe_1_c;probe_2_c",
                "2026-10-02 17:00:05.000;3.801;3.790;3.802;14;15"
            });

            // read while the recording is still open for writing, as during a live session
            List<VehicleState> states;
            using (new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                states = Recording.Read(live);

            Assert.Equal(4, states.Count);
            Assert.Null(states[0].CellVoltages);                        // no cell read yet
            Assert.Equal(new[] { 3.801 }, states[1].CellVoltages);      // the short read stops at the blank
            Assert.Equal(100, states[1].CellsCurrent);                  // the round after the read
            Assert.Equal(new[] { 3.801, 3.790, 3.802 }, states[3].CellVoltages);
            Assert.Equal(new double[] { 14, 15 }, states[3].ModuleTemps);
            Assert.Equal(10, states[3].CellsCurrent);
            Assert.Equal(-20, states[2].Current);
            Assert.Equal(3, states[2].Gear);
            Assert.Equal(59381, states[0].Odometer);
            Assert.Equal(live, Recording.MainFile(Path.Combine(dir, "live_1_cells_2.csv")));
        }
    }
}
