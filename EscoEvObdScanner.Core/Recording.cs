using System.Globalization;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Reads a live recording back as the readings it was made of: the main
    /// file (live_*.csv, one row per round) merged with its cells file
    /// (live_*_cells.csv, one row per cell read), so a report can be made of a
    /// session afterwards. Cell health, which changes rarely, has a file of its own
    /// (live_*_health.csv, a row whenever it changed). Columns are found by name;
    /// a missing column, cells file or health file stays empty.
    /// </summary>
    public static class Recording
    {
        /// <summary>The main file's columns, in the order <see cref="Line"/> writes them; <see cref="Read"/> finds them by name.</summary>
        public static readonly string[] Columns =
        {
            "time", "soc_pct", "soc_raw_pct", "voltage_v", "current_a", "power_kw", "speed_kmh", "motor_rpm", "energy_kwh",
            "soh_pct", "cell_min_v", "cell_max_v", "temp_min_c", "temp_avg_c", "temp_max_c", "ambient_c", "aux_v",
            "max_charge_kw", "max_discharge_kw", "charging", "odometer_km", "grid_v", "gear", "drive_mode",
            "lifetime_charged_kwh", "lifetime_discharged_kwh", "ac_charges", "dc_charges", "ac_charged_kwh", "dc_charged_kwh",
            "motor_c", "inverter_c", "charger_c", "dcdc_c", "coolant_c", "aux_battery_c", "contactor_closes", "time_above_55c",
            "isolation_pos_kohm", "isolation_neg_kohm", "lowest_cell"
        };

        /// <summary>One row of the main file for a reading, in <see cref="Columns"/> order; a missing value is left blank.</summary>
        public static string Line(VehicleState s)
        {
            static string F(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
            static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
            return string.Join(";",
                s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                F(s.Soc), F(s.SocRaw), F(s.Voltage), F(s.Current), F(s.Power), F(s.Speed), F(s.MotorRpm), F(s.Energy),
                F(s.Soh), F(s.CellMin), F(s.CellMax), F(s.TempMin), F(s.TempAvg), F(s.TempMax), F(s.Ambient), F(s.AuxVoltage),
                F(s.MaxChargePower), F(s.MaxDischargePower), s.Charging switch { true => "1", false => "0", _ => "" },
                F(s.Odometer), F(s.GridVoltage), I(s.Gear), I(s.DriveMode),
                F(s.LifetimeChargedKwh), F(s.LifetimeDischargedKwh), F(s.AcCharges), F(s.DcCharges), F(s.AcChargedKwh), F(s.DcChargedKwh),
                F(s.MotorTemp), F(s.InverterTemp), F(s.ChargerTemp), F(s.DcDcTemp), F(s.CoolantTemp), F(s.AuxBatteryTemp),
                F(s.ContactorCloses), F(s.TimeAbove55),
                F(s.IsolationPos), F(s.IsolationNeg), I(s.LowestCell));
        }

        /// <summary>The cells file of a recording: _cells.csv for the first, _cells_2.csv ... for later ones.</summary>
        public static string CellsFile(string recording, int number = 1) =>
            Path.ChangeExtension(recording, null) + (number <= 1 ? "_cells.csv" : $"_cells_{number}.csv");

        /// <summary>Every cells file of a recording: _cells.csv, and _cells_2.csv ... when the cell or probe count grew mid-session.</summary>
        public static IEnumerable<string> CellsFiles(string recording)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(recording))!;
            var stem = Path.GetFileNameWithoutExtension(recording);
            return Directory.GetFiles(dir, stem + "_cells*.csv")
                .Where(f => IsCellsFile(f) && Path.GetFileName(f).StartsWith(stem + "_cells", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The cell health file that belongs to a recording.</summary>
        public static string HealthFile(string recording) => Path.ChangeExtension(recording, null) + "_health.csv";

        const string PartPattern = @"_(cells(_\d+)?|health)\.csv$";

        /// <summary>A cells or cell health file of some recording rather than a recording.</summary>
        public static bool IsCellsFile(string path) =>
            System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), PartPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The recording a cells or health file belongs to; a recording stays as it is.</summary>
        public static string MainFile(string path) =>
            IsCellsFile(path) ? System.Text.RegularExpressions.Regex.Replace(path, PartPattern, ".csv", System.Text.RegularExpressions.RegexOptions.IgnoreCase) : path;

        /// <summary>What a recording holds, for picking one: its span, distance and cell reads.</summary>
        public sealed record Info(string Path, DateTime? Start, DateTime? End, double Km, int LoadReads, int RestReads, int CellReads);

        /// <summary>Reads a recording (given by any of its files) and sums it up.</summary>
        public static Info Describe(string recording)
        {
            string main = MainFile(recording);
            var drive = new DriveStats();
            foreach (var s in Read(main)) drive.Add(s);
            return new Info(main, drive.Start, drive.End, drive.Km, drive.LoadReads, drive.RestReads, drive.CellReads);
        }

        public static List<VehicleState> Read(string recording)
        {
            var rows = ReadCsv(recording);
            // all cells files in time order; a row whose time cannot be read is skipped, not the end of the list
            var cells = CellsFiles(recording).SelectMany(ReadCsv)
                .Select(r => (Row: r, Time: Time(r))).Where(x => x.Time != null).OrderBy(x => x.Time).Select(x => x.Row).ToList();
            var health = File.Exists(HealthFile(recording))
                ? ReadCsv(HealthFile(recording)).Select(r => (Row: r, Time: Time(r))).Where(x => x.Time != null).OrderBy(x => x.Time).ToList()
                : new();
            var states = new List<VehicleState>();
            int c = 0, h = 0;
            double[]? volts = null, probes = null, cellSoh = null;
            DateTime? cellsTime = null;
            bool cellsNew = false;

            foreach (var row in rows)
            {
                if (Time(row) is not DateTime time) continue;
                // the cell reads up to this round
                while (c < cells.Count && Time(cells[c]) is DateTime ct && ct <= time)
                {
                    volts = Values(cells[c], "cell_");
                    probes = Values(cells[c], "probe_");
                    cellsTime = ct;
                    cellsNew = true;
                    c++;
                }
                // the cell health as last read up to this round
                while (h < health.Count && health[h].Time <= time) cellSoh = Values(health[h++].Row, "health_");
                var s = new VehicleState
                {
                    Time = time,
                    Soc = D(row, "soc_pct"),
                    SocRaw = D(row, "soc_raw_pct"),
                    Voltage = D(row, "voltage_v"),
                    Current = D(row, "current_a"),
                    Speed = D(row, "speed_kmh"),
                    MotorRpm = D(row, "motor_rpm"),
                    Energy = D(row, "energy_kwh"),
                    Soh = D(row, "soh_pct"),
                    CellMin = D(row, "cell_min_v"),
                    CellMax = D(row, "cell_max_v"),
                    TempMin = D(row, "temp_min_c"),
                    TempAvg = D(row, "temp_avg_c"),
                    TempMax = D(row, "temp_max_c"),
                    Ambient = D(row, "ambient_c"),
                    AuxVoltage = D(row, "aux_v"),
                    MaxChargePower = D(row, "max_charge_kw"),
                    MaxDischargePower = D(row, "max_discharge_kw"),
                    Charging = D(row, "charging") is double ch ? ch > 0 : null,
                    Odometer = D(row, "odometer_km"),
                    GridVoltage = D(row, "grid_v"),
                    Gear = (int?)D(row, "gear"),
                    DriveMode = (int?)D(row, "drive_mode"),
                    MotorTemp = D(row, "motor_c"),
                    InverterTemp = D(row, "inverter_c"),
                    ChargerTemp = D(row, "charger_c"),
                    DcDcTemp = D(row, "dcdc_c"),
                    CoolantTemp = D(row, "coolant_c"),
                    AuxBatteryTemp = D(row, "aux_battery_c"),
                    LifetimeChargedKwh = D(row, "lifetime_charged_kwh"),
                    LifetimeDischargedKwh = D(row, "lifetime_discharged_kwh"),
                    AcCharges = D(row, "ac_charges"),
                    DcCharges = D(row, "dc_charges"),
                    AcChargedKwh = D(row, "ac_charged_kwh"),
                    DcChargedKwh = D(row, "dc_charged_kwh"),
                    ContactorCloses = D(row, "contactor_closes"),
                    TimeAbove55 = D(row, "time_above_55c"),
                    IsolationPos = D(row, "isolation_pos_kohm"),
                    IsolationNeg = D(row, "isolation_neg_kohm"),
                    LowestCell = (int?)D(row, "lowest_cell"),
                    CellVoltages = volts,
                    ModuleTemps = probes,
                    CellSoh = cellSoh,
                    CellsTime = cellsTime
                };
                // the current of the first round after the cell read stands in for the current at the read
                s.CellsCurrent = cellsNew ? s.Current : states.LastOrDefault()?.CellsCurrent;
                cellsNew = false;
                states.Add(s);
            }
            return states;
        }

        static List<Dictionary<string, string>> ReadCsv(string path)
        {
            // shared read: the recording may still be written by a running live view
            var lines = new List<string>();
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
                for (string? line; (line = reader.ReadLine()) != null;)
                    if (line.Trim().Length > 0) lines.Add(line);
            if (lines.Count == 0) return new();
            var head = lines[0].Trim().Split(';');
            return lines.Skip(1)
                .Select(l => l.Trim().Split(';'))
                .Select(v =>
                {
                    // a header named twice (empty columns after a hand edit) keeps its first column
                    var row = new Dictionary<string, string>();
                    for (int i = 0; i < head.Length; i++) row.TryAdd(head[i], i < v.Length ? v[i] : "");
                    return row;
                })
                .ToList();
        }

        static DateTime? Time(Dictionary<string, string> row) =>
            row.TryGetValue("time", out var t) && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        // "NaN" or "Infinity" in a damaged or hand-edited file is no reading
        static double? D(Dictionary<string, string> row, string column) =>
            row.TryGetValue(column, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

        /// <summary>The columns cell_1_v, cell_2_v ... (or probe_1_c ...) in order; a blank, or a value that is no number, ends the list.</summary>
        static double[]? Values(Dictionary<string, string> row, string prefix)
        {
            var list = new List<double>();
            for (int i = 1; ; i++)
            {
                var key = row.Keys.FirstOrDefault(k => k.StartsWith($"{prefix}{i}_", StringComparison.Ordinal));
                if (key == null || !double.TryParse(row[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v)) break;
                list.Add(v);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }
    }
}
