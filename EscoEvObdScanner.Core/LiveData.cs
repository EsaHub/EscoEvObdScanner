namespace EscoEvObdScanner
{
    /// <summary>
    /// Everything the dashboard shows, decoded. Null means "not read yet" or
    /// "the car did not answer" - never zero, which would be a real value.
    /// </summary>
    public sealed class VehicleState
    {
        public DateTime Time { get; set; }

        // battery
        public double? Soc { get; set; }                 // %
        public double? SocRaw { get; set; }              // %
        public double? Voltage { get; set; }             // V
        public double? Current { get; set; }             // A, positive = discharge
        public double? Energy { get; set; }              // kWh remaining
        public double? Soh { get; set; }                 // %
        public double? CellMin { get; set; }             // V
        public double? CellMax { get; set; }             // V
        public int? LowestCell { get; set; }             // 1-based
        public double? TempMin { get; set; }             // C
        public double? TempAvg { get; set; }             // C
        public double? TempMax { get; set; }             // C, read, or min + delta where the car gives that
        public double? TempDelta { get; set; }           // C, read, or max - min where the car gives those
        public double? MaxChargePower { get; set; }      // kW
        public double? MaxDischargePower { get; set; }   // kW
        public double? IsolationPos { get; set; }        // kOhm
        public double? IsolationNeg { get; set; }        // kOhm
        public double[]? CellVoltages { get; set; }      // V, one per cell
        public double[]? CellSoh { get; set; }           // %, one per cell
        public double[]? ModuleTemps { get; set; }       // C, one per probe

        /// <summary>When the cell voltages were last read in full; they are read less often than the rest.</summary>
        public DateTime? CellsTime { get; set; }

        /// <summary>Pack current in the round the cells were last read: their spread means something only at rest.</summary>
        public double? CellsCurrent { get; set; }

        // vehicle
        public double? Speed { get; set; }               // km/h
        public double? MotorRpm { get; set; }
        public double? AuxVoltage { get; set; }          // V, 12 V battery
        public double? Ambient { get; set; }             // C
        public bool? Charging { get; set; }               // charge cable connected; current may have stopped
        public int? Gear { get; set; }                   // selector code; the car file maps it to P, R, N, D
        public int? DriveMode { get; set; }              // mode code; the car file maps it to a name

        public double? GridVoltage { get; set; }         // V, AC at the charger; 0 when not charging
        public double? Odometer { get; set; }            // km

        // drivetrain temperatures, C: how the cooling keeps up while driving and fast charging
        public double? MotorTemp { get; set; }
        public double? InverterTemp { get; set; }
        public double? ChargerTemp { get; set; }
        public double? DcDcTemp { get; set; }
        public double? CoolantTemp { get; set; }
        public double? AuxBatteryTemp { get; set; }

        /// <summary>Whether the car gave any drivetrain temperature.</summary>
        public bool HasDrivetrainTemps =>
            MotorTemp != null || InverterTemp != null || ChargerTemp != null || DcDcTemp != null || CoolantTemp != null || AuxBatteryTemp != null;

        // the car's own counters over its whole life
        public double? LifetimeChargedKwh { get; set; }      // into the battery, regeneration included
        public double? LifetimeDischargedKwh { get; set; }   // out of the battery
        public double? AcCharges { get; set; }               // charging sessions
        public double? DcCharges { get; set; }
        public double? AcChargedKwh { get; set; }            // energy charged
        public double? DcChargedKwh { get; set; }

        public double? ContactorCloses { get; set; }         // HV contactor closings, one per start
        public double? TimeAbove55 { get; set; }             // time the pack has spent above 55 C, in the car's own unit; 0 = never

        /// <summary>Whether the car gave any of its lifetime counters.</summary>
        public bool HasLifetimeCounters =>
            LifetimeChargedKwh != null || LifetimeDischargedKwh != null || AcCharges != null || DcCharges != null || AcChargedKwh != null || DcChargedKwh != null
            || ContactorCloses != null || TimeAbove55 != null;

        // the battery management unit's identification
        public DateTime? BatteryMade { get; set; }           // its date of manufacture
        public string? BmsSoftware { get; set; }
        public string? BmsHardware { get; set; }
        public DateTime? BatteryInfoRead { get; set; }       // set when these come from an earlier connection, not from this reading

        /// <summary>kW, positive while driving (discharge), negative while charging or regenerating.</summary>
        public double? Power => Voltage * Current / 1000.0;

        /// <summary>kWh/100 km, only meaningful while moving.</summary>
        public double? Consumption => Speed > 5 && Power.HasValue ? Power / Speed * 100.0 : null;

        public double? CellSpread => CellMax - CellMin;

        /// <summary>The lower of the two insulation resistances, kOhm: the one that matters.</summary>
        public double? IsolationMin => IsolationPos is double p ? (IsolationNeg is double n ? Math.Min(p, n) : p) : IsolationNeg;

        public VehicleState Clone()
        {
            var c = (VehicleState)MemberwiseClone();
            c.CellVoltages = (double[]?)CellVoltages?.Clone();
            c.CellSoh = (double[]?)CellSoh?.Clone();
            c.ModuleTemps = (double[]?)ModuleTemps?.Clone();
            return c;
        }
    }

    /// <summary>
    /// Reads the dashboard values in rounds, as the car file's "live" section
    /// says: fast-changing values every round, slow ones every few rounds and
    /// the big per-cell arrays less often still, so one round stays well under
    /// a second while the ECUs answer (each unanswered read waits out its
    /// timeout). A value the car file does not list stays empty.
    /// </summary>
    public sealed class LivePoller
    {
        public const int SlowEvery = 5;
        public const int CellsEvery = 10;
        public const int RareEvery = 60;

        /// <summary>
        /// Trailing empty slots needed before they count as slots a smaller pack
        /// does not use. One alone could be a dead last cell, which must show.
        /// </summary>
        public const int MinEmptySlots = 2;

        /// <summary>
        /// Under load (DriveStats.LoadCurrentA or more) the cells are read again once this many
        /// seconds have passed since the last cell read, instead of waiting for their round:
        /// reads under load are what the cells-under-load figures need, and accelerations are short.
        /// </summary>
        public const double LoadCellsGapSeconds = 5;

        DateTime? _lastCellsAttempt;

        /// <summary>The time of each reading; replaceable for tests.</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

        /// <summary>The dashboard value names a car file can use, and what each one sets.</summary>
        public static readonly IReadOnlyDictionary<string, Action<VehicleState, double?>> Scalars =
            new Dictionary<string, Action<VehicleState, double?>>(StringComparer.OrdinalIgnoreCase)
            {
                ["soc"] = (s, v) => s.Soc = v,
                ["soc_raw"] = (s, v) => s.SocRaw = v,
                ["voltage"] = (s, v) => s.Voltage = v,
                ["current"] = (s, v) => s.Current = v,
                ["energy"] = (s, v) => s.Energy = v,
                ["soh"] = (s, v) => s.Soh = v,
                ["aux_voltage"] = (s, v) => s.AuxVoltage = v,
                ["temp_min"] = (s, v) => s.TempMin = v,
                ["temp_avg"] = (s, v) => s.TempAvg = v,
                ["temp_max"] = (s, v) => s.TempMax = v,
                ["temp_delta"] = (s, v) => s.TempDelta = v,
                ["max_charge_power"] = (s, v) => s.MaxChargePower = v,
                ["max_discharge_power"] = (s, v) => s.MaxDischargePower = v,
                ["isolation_pos"] = (s, v) => s.IsolationPos = v,
                ["isolation_neg"] = (s, v) => s.IsolationNeg = v,
                ["cell_min"] = (s, v) => s.CellMin = v,
                ["cell_max"] = (s, v) => s.CellMax = v,
                ["lowest_cell"] = (s, v) => s.LowestCell = (int?)v,
                ["speed"] = (s, v) => s.Speed = v,
                ["motor_rpm"] = (s, v) => s.MotorRpm = v,
                ["ambient"] = (s, v) => s.Ambient = v,
                ["odometer"] = (s, v) => s.Odometer = v,
                ["gear"] = (s, v) => s.Gear = (int?)v,
                ["motor_temp"] = (s, v) => s.MotorTemp = v,
                ["inverter_temp"] = (s, v) => s.InverterTemp = v,
                ["charger_temp"] = (s, v) => s.ChargerTemp = v,
                ["dcdc_temp"] = (s, v) => s.DcDcTemp = v,
                ["coolant_temp"] = (s, v) => s.CoolantTemp = v,
                ["aux_battery_temp"] = (s, v) => s.AuxBatteryTemp = v,
                ["lifetime_charged_kwh"] = (s, v) => s.LifetimeChargedKwh = v,
                ["lifetime_discharged_kwh"] = (s, v) => s.LifetimeDischargedKwh = v,
                ["ac_charges"] = (s, v) => s.AcCharges = v,
                ["dc_charges"] = (s, v) => s.DcCharges = v,
                ["ac_charged_kwh"] = (s, v) => s.AcChargedKwh = v,
                ["dc_charged_kwh"] = (s, v) => s.DcChargedKwh = v,
                ["contactor_closes"] = (s, v) => s.ContactorCloses = v,
                ["time_above_55c"] = (s, v) => s.TimeAbove55 = v,
                ["drive_mode"] = (s, v) => s.DriveMode = (int?)v,
                ["charging"] = (s, v) => s.Charging = v.HasValue ? v.Value > 0 : null,
                ["grid_voltage"] = (s, v) => s.GridVoltage = v,
            };

        /// <summary>The text value names a car file can use (read from the whole reply), and what each one sets.</summary>
        public static readonly IReadOnlyDictionary<string, Action<VehicleState, byte[]?, LiveSource>> Texts =
            new Dictionary<string, Action<VehicleState, byte[]?, LiveSource>>(StringComparer.OrdinalIgnoreCase)
            {
                // the date's byte order comes from the car file ("format"), day first if it does not say
                ["battery_made"] = (s, d, src) => s.BatteryMade = d == null ? null : Bytes.BcdDate(d, src.Format),
                ["bms_software"] = (s, d, _) => s.BmsSoftware = d == null ? null : Bytes.Text(d),
                ["bms_hardware"] = (s, d, _) => s.BmsHardware = d == null ? null : Bytes.Text(d),
            };

        /// <summary>
        /// The text values the car file lists (battery management date and versions), read once
        /// outside the live view; an ECU that cannot be addressed leaves its values empty.
        /// </summary>
        public static void ReadTexts(Scanner scanner, Catalog catalog, VehicleState into)
        {
            foreach (var (name, src) in catalog.Live)
            {
                if (!Texts.TryGetValue(name, out var set) || src.Dids is not [int did, ..] || catalog.TryEcu(src.Ecu) is not { } ecu) continue;
                try
                {
                    scanner.Target(ecu.Request, ecu.Response);
                    var r = scanner.ReadDid(did, 1000);
                    set(into, r.Kind == ResponseKind.Positive ? r.Data : null, src);
                }
                catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException) { }
            }
        }

        /// <summary>The per-cell array names a car file can use.</summary>
        public static readonly IReadOnlySet<string> Arrays =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cells", "probes", "cell_soh" };

        readonly Scanner _scanner;
        readonly Catalog _catalog;
        readonly VehicleState _state = new();
        readonly Dictionary<(string Ecu, int Did), UdsResponse> _replies = new();
        readonly Dictionary<string, int> _shortSeen = new(StringComparer.OrdinalIgnoreCase);
        string? _targeted;
        int _round;

        public LivePoller(Scanner scanner, Catalog catalog)
        {
            _scanner = scanner;
            _catalog = catalog;
        }

        /// <summary>One round of reads. Returns a copy of the state after it.</summary>
        public VehicleState Poll()
        {
            var due = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fast" };
            if (_round % SlowEvery == 0) due.Add("slow");
            if (_round % CellsEvery == 0) due.Add("cells");
            if (_round % RareEvery == 0) due.Add("rare");
            _round++;
            // the last round's current: pulling hard, and the last cell read a while ago
            // spaced from the last attempt, not the last complete read: a failing read must not repeat every round
            bool cellsUnderLoad = !due.Contains("cells") && _state.Current >= DriveStats.LoadCurrentA
                                  && (_lastCellsAttempt is not DateTime last || (Clock() - last).TotalSeconds >= LoadCellsGapSeconds);
            _replies.Clear();
            _targeted = null;       // something else may have pointed the adapter elsewhere between rounds

            // one ECU after another, in the order they first appear, to keep re-targeting down;
            // ECU ids as the catalogue spells them, so "BMS" and "bms" are one ECU
            var entries = _catalog.Live
                .Where(l => due.Contains(l.Value.Poll) || cellsUnderLoad && l.Key.Equals("cells", StringComparison.OrdinalIgnoreCase))
                .Select(l => (Name: l.Key, Src: l.Value, Ecu: _catalog.TryEcu(l.Value.Ecu)))
                .Where(l => l.Ecu != null)
                .ToList();
            var order = entries.Select(e => e.Ecu!.Id).Distinct().ToList();
            var unreachable = new HashSet<string>();
            foreach (var (name, src, ecu) in entries.OrderBy(e => order.IndexOf(e.Ecu!.Id)))
            {
                try
                {
                    if (unreachable.Contains(ecu!.Id)) { Clear(name); continue; }
                    if (Scalars.TryGetValue(name, out var set)) set(_state, YesNoValues.Contains(name) ? YesNo(src, Scalar(src, ecu)) : Scalar(src, ecu));
                    else if (Texts.TryGetValue(name, out var setText)) setText(_state, Reply(src, ecu), src);
                    else if (Arrays.Contains(name))
                    {
                        if (name.Equals("cells", StringComparison.OrdinalIgnoreCase)) _lastCellsAttempt = Clock();
                        var values = Array(src, ecu, out bool trimmed);
                        if (!trimmed) _shortSeen.Remove(name);      // "twice in a row": anything else in between starts over
                        if (!trimmed || SeenTwice(name, values!.Length)) StoreArray(name, values);
                    }
                }
                catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException)
                {
                    // the adapter refused to address this ECU: skip it this round, keep the others
                    unreachable.Add(ecu!.Id);
                    _targeted = null;
                    Clear(name);
                }
            }

            if (_catalog.LiveOf("temp_max") == null) _state.TempMax = _state.TempMin + _state.TempDelta;
            if (_catalog.LiveOf("temp_delta") == null) _state.TempDelta = _state.TempMax - _state.TempMin;
            _state.Time = Clock();
            return _state.Clone();
        }

        /// <summary>Empties a scalar or text value whose ECU could not be reached.</summary>
        void Clear(string name)
        {
            if (Scalars.TryGetValue(name, out var scalar)) scalar(_state, null);
            else if (Texts.TryGetValue(name, out var text) && _catalog.LiveOf(name) is { } src) text(_state, null, src);
        }

        /// <summary>The value names that are yes/no: only these take a car file's "on" codes.</summary>
        public static readonly IReadOnlySet<string> YesNoValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "charging" };

        /// <summary>A yes/no value read as codes: 1 for the codes the car file lists as yes ("on"), else 0; unchanged without a list.</summary>
        static double? YesNo(LiveSource src, double? value) =>
            src.On is { Count: > 0 } on && value is double v ? (on.Contains(v) ? 1 : 0) : value;

        void StoreArray(string name, double[]? values)
        {
            // A read shorter than the last complete one ended early (a piece
            // that came short, or a value outside the range that marks the end
            // of the data); keep the complete one.
            switch (name.ToLowerInvariant())
            {
                case "cells" when Complete(values, _state.CellVoltages):
                    _state.CellVoltages = values;
                    _state.CellsTime = Clock();         // when the last complete cell read was taken
                    // read before the cells when the car file lists current first on the same ECU, as the shipped ones do
                    _state.CellsCurrent = _state.Current;
                    break;
                case "probes" when Complete(values, _state.ModuleTemps):
                    _state.ModuleTemps = values;
                    break;
                case "cell_soh" when Complete(values, _state.CellSoh):
                    _state.CellSoh = values;
                    break;
            }
        }

        /// <summary>
        /// A read with empty slots dropped counts only once the same length has
        /// come twice in a row: a waking BMS can answer with zero-filled replies,
        /// which would otherwise fix the cell count (and the recording's columns)
        /// too short.
        /// </summary>
        bool SeenTwice(string name, int length)
        {
            bool again = _shortSeen.TryGetValue(name, out int last) && last == length;
            _shortSeen[name] = length;
            return again;
        }

        // -------------------------------------------------------------------

        static bool Complete(double[]? fresh, double[]? previous) =>
            fresh != null && fresh.Length >= (previous?.Length ?? 0);

        UdsResponse Read(EcuDef ecu, int did, int timeoutMs)
        {
            // several dashboard values may come from one reply: read it once per round
            if (_replies.TryGetValue((ecu.Id, did), out var cached)) return cached;
            if (_targeted != ecu.Id)
            {
                _scanner.Target(ecu.Request, ecu.Response);
                _targeted = ecu.Id;
            }
            var r = _scanner.ReadDid(did, timeoutMs);
            _replies[(ecu.Id, did)] = r;
            return r;
        }

        /// <summary>The whole data of a positive reply, for a text value; null otherwise.</summary>
        byte[]? Reply(LiveSource src, EcuDef ecu) =>
            src.Dids is [int did, ..] && Read(ecu, did, 1000) is { Kind: ResponseKind.Positive } r ? r.Data : null;

        /// <summary>
        /// One value, decoded with the live entry's own expression, else the
        /// identifier's catalogue expression, else as the whole reply read as one
        /// unsigned number (status codes such as a gear), then scaled. Values
        /// outside the plausible range are dropped: 0xFFFF "not available"
        /// markers otherwise show up as 65535 rpm.
        /// </summary>
        double? Scalar(LiveSource src, EcuDef ecu)
        {
            if (src.Dids is not [int did, ..]) return null;
            string? expr = src.Expression ?? _catalog.Find(ecu.Id, did)?.Expression;

            var r = Read(ecu, did, 1000);
            if (r.Kind != ResponseKind.Positive) return null;

            double? decoded = !string.IsNullOrEmpty(expr) ? Expr.Evaluate(expr, r.Data)
                : r.Data.Length is > 0 and <= 8 ? r.Data.Aggregate(0.0, (v, b) => v * 256 + b) : null;
            if (decoded is not double raw) return null;
            double v = raw * src.Scale + src.Offset;
            return (src.Min is double min && v < min) || (src.Max is double max && v > max) ? null : v;
        }

        /// <summary>
        /// Per-cell data: equal-sized elements, from one reply or several read in order.
        ///
        /// A piece with a count must deliver that many elements, whatever their
        /// values, or the whole read fails: a sagging cell is shown, and no later
        /// cell is numbered wrong. A run of at least <see cref="MinEmptySlots"/>
        /// elements at the very end that are all zero or all 0xFF bytes and
        /// outside min/max are slots the pack does not use (a smaller pack in the
        /// same layout) and are dropped, which <paramref name="trimmed"/> says; a
        /// single one is a dead last cell and stays, as does a 0 C probe. Without
        /// a count the reply is read until a value falls outside min/max, which
        /// marks the padding after the data.
        /// </summary>
        double[]? Array(LiveSource src, EcuDef ecu, out bool trimmed)
        {
            trimmed = false;
            int size = Math.Clamp(src.Bytes, 1, 4);
            long allOnes = (1L << (8 * size)) - 1;
            Func<double, double> convert = raw => Signed(raw, size, src.Signed) * src.Scale + src.Offset;
            var all = new List<double>();
            int used = 0;                       // elements up to the last one that is not an empty slot
            foreach (var (did, skip, count) in src.Pieces)
            {
                var r = Read(ecu, did, 2000);
                if (r.Kind != ResponseKind.Positive) return null;
                var bytes = r.Data.Skip(skip).ToArray();

                if (count is int n && n > 0)
                {
                    if (bytes.Length < n * size) return null;
                    for (int i = 0; i < n; i++)
                    {
                        long raw = 0;
                        for (int k = 0; k < size; k++) raw = (raw << 8) | bytes[i * size + k];
                        double v = convert(raw);
                        all.Add(v);
                        bool outside = (src.Min is double lo && v < lo) || (src.Max is double hi && v > hi);
                        if (!(outside && (raw == 0 || raw == allOnes))) used = all.Count;
                    }
                }
                else
                {
                    var part = DecodeArray(bytes, size, convert, src.Min ?? double.MinValue, src.Max ?? double.MaxValue);
                    if (part == null) break;
                    all.AddRange(part);
                    used = all.Count;
                }
            }
            if (used == 0) return null;
            if (all.Count - used < MinEmptySlots) return all.ToArray();
            trimmed = true;
            return all.Take(used).ToArray();
        }

        static double Signed(double raw, int size, bool signed)
        {
            double range = Math.Pow(256, size);
            return signed && raw >= range / 2 ? raw - range : raw;
        }

        public static double[]? DecodeArray(byte[] data, int size, Func<double, double> convert, double min, double max)
        {
            var values = new List<double>();
            for (int i = 0; i + size <= data.Length; i += size)
            {
                long raw = 0;
                for (int k = 0; k < size; k++) raw = (raw << 8) | data[i + k];
                double v = convert(raw);
                if (v < min || v > max) break;      // padding or end of the real data
                values.Add(v);
            }
            return values.Count > 0 ? values.ToArray() : null;
        }
    }
}
