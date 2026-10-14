namespace EscoEvObdScanner
{
    /// <summary>
    /// What a live session shows that a single reading cannot: distance and
    /// energy, the highest loads, and how each cell behaves under load.
    ///
    /// A cell's offset from the pack average at rest comes from its state of
    /// charge; the extra it sags under load on top of that comes from its
    /// internal resistance. Only the extra says something about resistance, so
    /// cells are ranked by it, and only with enough reads both at rest and
    /// under load.
    /// </summary>
    public sealed class DriveStats
    {
        /// <summary>Cell reads at this discharge current or more count as under load.</summary>
        public const double LoadCurrentA = 40;

        /// <summary>Fewer reads under load, or at rest, than this say too little to rank cells.</summary>
        public const int MinLoadReads = 3;
        public const int MinRestReads = 3;

        /// <summary>
        /// Smaller differences are within the noise with cell voltages in 1 mV steps: the reads at rest and
        /// under load are taken at different states of charge, and cells that do not stand out vary by up to
        /// about 4 mV from drive to drive. Coarser steps raise it (see <see cref="NoiseFloorMv"/>).
        /// </summary>
        public const double NoiseMv = 5;

        /// <summary>
        /// One cell under load: mV below the pack average under load and at rest, the extra under
        /// load, and that extra scaled to 100 A (each load read by 100 A / its current), which
        /// compares drives at different loads.
        /// </summary>
        public sealed record CellLoad(int Cell, double LoadMv, double RestMv, double ExtraMv, double ExtraPer100A);

        DateTime? _lastTime;
        DateTime? _lastCellsTime;
        double[]? _loadOffset, _restOffset, _loadOffsetScaled;
        double _loadScale;
        double _restSpreadSum, _loadSpreadSum;

        public DateTime? Start { get; private set; }
        public DateTime? End { get; private set; }
        public double Km { get; private set; }
        public double MovingSeconds { get; private set; }
        public double KwhOut { get; private set; }
        public double KwhRegen { get; private set; }
        public double KwhStanding { get; private set; }
        public double? MaxCurrent { get; private set; }
        public double? MaxPower { get; private set; }
        public double? MaxRegenPower { get; private set; }
        public double? TempFirst { get; private set; }
        public double? TempLast { get; private set; }
        public double? TempHigh { get; private set; }

        public int CellReads { get; private set; }

        /// <summary>
        /// The step the car gives cell voltages in, mV: the smallest difference between two cells seen in a
        /// read (1 mV on the e-CMP, 20 mV on the Kia). Null until two cells have differed.
        /// </summary>
        public double? CellStepMv { get; private set; }

        /// <summary>
        /// The noise of this session's cell figures, mV: <see cref="NoiseMv"/>, or half the voltage step if
        /// that is more, since a cell can sit anywhere within one step of the value it is reported at.
        /// </summary>
        public double NoiseFloorMv => Math.Max(NoiseMv, (CellStepMv ?? 0) / 2);
        public int RestReads { get; private set; }
        public int LoadReads { get; private set; }
        public double? RestSpreadMin { get; private set; }
        public double? RestSpreadMax { get; private set; }
        public double? RestSpreadAvg => RestReads > 0 ? _restSpreadSum / RestReads : null;
        public double? LoadSpreadMax { get; private set; }
        public double? LoadSpreadAvg => LoadReads > 0 ? _loadSpreadSum / LoadReads : null;
        public double? LoadCurrentMax { get; private set; }

        /// <summary>The coldest pack temperature (°C) at any read under load; cold raises the cells' resistance unevenly.</summary>
        public double? LoadTempMin { get; private set; }

        /// <summary>
        /// The current the per-100 A figures act as if read at: the harmonic mean of the load reads'
        /// currents, since each read is scaled by 100 A / its current. Grades scale the noise with it.
        /// </summary>
        public double? LoadCurrentScaled => LoadReads > 0 && _loadScale > 0 ? 100 * LoadReads / _loadScale : null;

        /// <summary>The spread (mV) of the last cell read at rest, and when it was taken.</summary>
        public double? LastRestSpread { get; private set; }
        public DateTime? LastRestTime { get; private set; }

        public TimeSpan Duration => Start is DateTime s && End is DateTime e ? e - s : TimeSpan.Zero;

        /// <summary>kWh/100 km over the session, charging excluded; null below a kilometre.</summary>
        public double? Consumption => Km >= 1 ? (KwhOut - KwhRegen + KwhStanding) / Km * 100 : null;

        /// <summary>Whether there is anything worth a section of its own.</summary>
        public bool HasData => Km >= 0.5 || LoadReads > 0;

        /// <summary>Enough reads at rest and under load to compare the cells.</summary>
        public bool CanRankCells => LoadReads >= MinLoadReads && RestReads >= MinRestReads;

        public void Add(VehicleState s)
        {
            Start ??= s.Time;
            End = s.Time;
            if (_lastTime is DateTime last)
            {
                double hours = (s.Time - last).TotalHours;
                if (hours is > 0 and < 0.01)        // gaps (pauses, reconnects) are not driving
                {
                    double speed = Math.Max(0, s.Speed ?? 0);
                    bool moving = speed > 2;
                    Km += speed * hours;
                    if (moving) MovingSeconds += hours * 3600;
                    if (s.Power is double p)
                    {
                        // standing still, negative power is the charger, not regeneration
                        if (moving) { if (p >= 0) KwhOut += p * hours; else KwhRegen += -p * hours; }
                        else if (p > 0 && s.Charging != true) KwhStanding += p * hours;
                    }
                }
            }
            _lastTime = s.Time;

            if (s.Current is double i) MaxCurrent = Math.Max(MaxCurrent ?? i, i);
            if (s.Power is double pw)
            {
                MaxPower = Math.Max(MaxPower ?? pw, pw);
                if (s.Speed > 2 && pw < 0) MaxRegenPower = Math.Max(MaxRegenPower ?? 0, -pw);
            }
            double? temp = FiniteProbes(s)?.Max() ?? s.TempMax;
            if (temp is double tm)
            {
                TempFirst ??= tm;
                TempLast = tm;
                TempHigh = Math.Max(TempHigh ?? tm, tm);
            }

            AddCells(s);
        }

        void AddCells(VehicleState s)
        {
            if (s.CellVoltages is not { Length: > 1 } cells || s.CellsTime == null || s.CellsTime == _lastCellsTime) return;
            if (cells.Any(v => !double.IsFinite(v))) return;
            _lastCellsTime = s.CellsTime;
            // another cell count (the first read came short): every cell figure starts over, so none mixes the two
            if (_loadOffset == null || _loadOffset.Length != cells.Length) ResetCells(cells.Length);

            CellReads++;
            double min = cells.Min(), max = cells.Max(), avg = cells.Average();
            double spread = (max - min) * 1000;
            if (StepMv(cells) is double step) CellStepMv = Math.Min(CellStepMv ?? step, step);

            double? current = s.CellsCurrent ?? s.Current;
            if (current is double a && Math.Abs(a) <= BatteryChecks.RestCurrentA)
            {
                RestReads++;
                _restSpreadSum += spread;
                RestSpreadMin = Math.Min(RestSpreadMin ?? spread, spread);
                RestSpreadMax = Math.Max(RestSpreadMax ?? spread, spread);
                LastRestSpread = spread;
                LastRestTime = s.CellsTime;
                for (int k = 0; k < cells.Length; k++) _restOffset![k] += (avg - cells[k]) * 1000;
            }
            else if (current is double l && l >= LoadCurrentA)
            {
                LoadReads++;
                _loadSpreadSum += spread;
                LoadSpreadMax = Math.Max(LoadSpreadMax ?? spread, spread);
                LoadCurrentMax = Math.Max(LoadCurrentMax ?? l, l);
                if ((FiniteProbes(s)?.Min() ?? s.TempMin) is double cold)
                    LoadTempMin = Math.Min(LoadTempMin ?? cold, cold);
                double scale = 100 / l;
                _loadScale += scale;
                for (int k = 0; k < cells.Length; k++)
                {
                    double offset = (avg - cells[k]) * 1000;
                    _loadOffset![k] += offset;
                    _loadOffsetScaled![k] += offset * scale;
                }
            }
        }

        /// <summary>The probe temperatures that are numbers (a 0/0 probe is no reading), or null when there are none.</summary>
        static double[]? FiniteProbes(VehicleState s) =>
            s.ModuleTemps?.Where(double.IsFinite).ToArray() is { Length: > 0 } t ? t : null;

        void ResetCells(int count)
        {
            _loadOffset = new double[count];
            _restOffset = new double[count];
            _loadOffsetScaled = new double[count];
            _loadScale = 0;
            CellReads = RestReads = LoadReads = 0;
            _restSpreadSum = _loadSpreadSum = 0;
            RestSpreadMin = RestSpreadMax = LoadSpreadMax = LoadCurrentMax = LoadTempMin = LastRestSpread = CellStepMv = null;
            LastRestTime = null;
        }

        IEnumerable<CellLoad> Cells() => _loadOffset!.Select((l, k) =>
        {
            double load = l / LoadReads, rest = _restOffset![k] / RestReads;
            // the rest offset is charge, the same at any current: only the part above it scales
            return new CellLoad(k + 1, load, rest, load - rest, (_loadOffsetScaled![k] - rest * _loadScale) / LoadReads);
        });

        /// <summary>The cells that sag most under load beyond their offset at rest, per 100 A; empty with too few reads.</summary>
        public IReadOnlyList<CellLoad> WeakestUnderLoad(int count) =>
            CanRankCells ? Cells().OrderByDescending(c => c.ExtraPer100A).Take(count).ToList() : Array.Empty<CellLoad>();

        /// <summary>Every cell's extra under load in mV per 100 A, in cell order; null with too few reads.</summary>
        public double[]? ExtraPer100AByCell() => CanRankCells ? Cells().Select(c => c.ExtraPer100A).ToArray() : null;

        /// <summary>The cells furthest below the average at rest (lower charge, not resistance); empty with too few reads.</summary>
        public IReadOnlyList<(int Cell, double Mv)> LowestAtRest(int count) =>
            _restOffset == null || RestReads < MinRestReads ? Array.Empty<(int, double)>()
                : _restOffset.Select((v, k) => (Cell: k + 1, Mv: v / RestReads)).OrderByDescending(x => x.Mv).Take(count).ToList();

        /// <summary>How far a typical cell's extra under load is from zero (the median), for scale.</summary>
        public double? TypicalExtraMv =>
            CanRankCells ? Cells().Select(c => Math.Abs(c.ExtraMv)).OrderBy(v => v).ElementAt(_loadOffset!.Length / 2) : null;

        /// <summary>The smallest difference between two cells of one read, mV, to 0.1 mV; null when all are equal.</summary>
        public static double? StepMv(double[] cells)
        {
            var distinct = cells.Where(double.IsFinite).Select(v => Math.Round(v * 10000)).Distinct().Order().ToList();
            if (distinct.Count < 2) return null;
            return distinct.Zip(distinct.Skip(1), (a, b) => b - a).Min() / 10;
        }
    }
}
