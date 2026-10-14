using System.Globalization;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Reports;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>
    /// The live view behind three tabs: dashboard, battery and charts. Polls
    /// the car in a loop, keeps the latest state, a trip computer, rolling
    /// chart series and a CSV recording of every round.
    /// </summary>
    public sealed class LiveViewModel : ObservableObject
    {
        const double KeepSeconds = 30 * 60;
        const int MinRoundMs = 250;

        readonly AppState _state;
        CancellationTokenSource? _cts;
        StreamWriter? _csv;
        StreamWriter? _cellsCsv;
        StreamWriter? _healthCsv;
        bool _recordPending;
        double[]? _lastHealth;
        DateTime? _lastCellsTime;
        (int Cells, int Probes) _cellsColumns;
        int _cellsFiles;
        DateTime _started;
        DateTime? _lastTime;
        VehicleState? _now;
        string? _recordingFile;
        bool _isRunning;
        int _revision;
        double _windowMinutes = 5;
        bool _showCellHealth;
        bool _showCellLoad;
        DriveStats? _shownDrive;
        string? _shownRecording;
        string _reportNote = "";
        DriveStats _drive = new();
        double? _tripSocStart;
        string? _lastReport;
        DriveHistory _history;
        DateTime _driveStart;
        double _driveKm, _driveKwh, _driveAmbientKm, _driveAmbientWeight;
        int _driveSavedKm;

        public LiveViewModel(AppState state)
        {
            _state = state;
            _history = DriveHistory.Load(state.CarId);
            Start = new AsyncCommand(RunAsync, () => _state.Adapter.CanRun);
            Stop = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
            ClearCharts = new RelayCommand(ClearChartPoints);
            CreateReport = new RelayCommand(MakeReport, () => Now != null);
            ReportFromRecording = new RelayCommand(ChooseRecording);
            OpenRecording = new RelayCommand(ChooseRecordingToShow);
            BackToLive = new RelayCommand(() => ShowRecording(null), () => _shownDrive != null);
            Loc.I.PropertyChanged += (_, _) => { RenameSeries(); RaiseDerived(); RaiseLifetime(); };
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppState.CatalogPath))
                {
                    // another car: its own drive history, and nothing of the last car's readings
                    _history = DriveHistory.Load(state.CarId);
                    _driveStart = default;
                    _driveKm = _driveKwh = _driveAmbientKm = _driveAmbientWeight = 0;
                    _driveSavedKm = 0;
                    Now = null;
                    ClearSeries();
                    // nothing of the last car in this car's report or on its dashboard
                    ReportNote = "";
                    LastReport = null;
                    RecordingFile = null;
                    // the taken-into-use date and odometer are this car's; a rejection of the last car's input is gone
                    InputError = "";
                    RaiseLifetime();
                }
                if (e.PropertyName == nameof(AppState.Vehicle)) RaiseLifetime();
                if (e.PropertyName != nameof(AppState.Catalog)) return;
                foreach (var p in new[] { nameof(PowerMin), nameof(PowerMax), nameof(CurrentMin), nameof(CurrentMax),
                                          nameof(SpeedMax), nameof(CellColumns), nameof(ProbeColumns), nameof(HasCellHealth), nameof(HasMaxCharge), nameof(HasMaxDischarge),
                                          nameof(HasMotorTemp), nameof(HasInverterTemp), nameof(HasChargerTemp), nameof(HasDcDcTemp), nameof(HasCoolantTemp), nameof(HasAuxBatteryTemp) }) Raise(p);
                if (!HasCellHealth) ShowCellHealth = false;
            };
            RenameSeries();
        }

        public AsyncCommand Start { get; }
        public RelayCommand Stop { get; }
        public RelayCommand ClearCharts { get; }
        public RelayCommand CreateReport { get; }
        public RelayCommand OpenRecording { get; }
        public RelayCommand BackToLive { get; }
        public RelayCommand ReportFromRecording { get; }

        /// <summary>Start the live view as soon as a connection is made (see <see cref="ConnectionViewModel.Connected"/>).</summary>
        public bool AutoStart
        {
            get => Settings.AutoStartLive;
            set
            {
                if (Settings.AutoStartLive == value) return;
                Settings.AutoStartLive = value;
                Settings.Save();
                Raise(nameof(AutoStart));
            }
        }

        public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) Raise(nameof(WaitingForCells)); } }

        /// <summary>The latest reading. Everything on the dashboard binds to this.</summary>
        public VehicleState? Now { get => _now; private set { if (Set(ref _now, value)) RaiseDerived(); } }

        public string? RecordingFile { get => _recordingFile; private set => Set(ref _recordingFile, value); }

        // ---- trip computer -------------------------------------------------

        public double TripKm { get; private set; }
        public double TripKwh { get; private set; }
        public double TripRegenKwh { get; private set; }
        public double PeakPowerKw { get; private set; }
        public double PeakRegenKw { get; private set; }
        public double? TripAverage => TripKm > 0.5 ? (TripKwh - TripRegenKwh) / TripKm * 100 : null;

        /// <summary>Taken from the battery less what regeneration put back.</summary>
        public double TripNetKwh => TripKwh - TripRegenKwh;

        /// <summary>Into the battery standing still, i.e. from the charger, this session.</summary>
        public double ChargedKwh { get; private set; }

        /// <summary>Percentage points the charge has fallen since the session started; negative when it rose.</summary>
        public double? TripSocUsed => _tripSocStart is double a && Now?.Soc is double b ? a - b : null;

        public string TripSocNote => _tripSocStart is double a && Now?.Soc is double b
            ? $"{a.ToString("F1", CultureInfo.CurrentCulture)} → {b.ToString("F1", CultureInfo.CurrentCulture)} %" : "";

        // ---- lifetime --------------------------------------------------------

        Settings Settings => _state.Settings;

        public bool HasLifetime => Now?.LifetimeDischargedKwh != null || Now?.LifetimeChargedKwh != null;
        public double? LifeIn => Now?.LifetimeChargedKwh;
        // some cars report this counter only after a drive or a charge since waking
        public string LifeInNote => Loc.I[HasLifetime && LifeIn == null ? "LifeInPending" : "LifeInNote"];
        public double? LifeOut => Now?.LifetimeDischargedKwh;
        public double? LifeAverage => EnergyCost.Per100Km(LifeOut, Now?.Odometer);
        public double? LifeFromGrid => EnergyCost.FromGrid(LifeOut, Settings.ChargeLossPercent);
        public double? LifeCost => EnergyCost.Euros(LifeOut, Settings.PriceCents, Settings.ChargeLossPercent);

        // the vehicle keeps when it was taken into use; a car file used without one (the demo car too) keeps it in the settings under its name
        DateTime? InUseSince => _state.Vehicle is { } v ? v.InUseSince : Settings.InUse.GetValueOrDefault(_state.CarId)?.Since;
        double InUseKm => (_state.Vehicle is { } v ? v.InUseKm : Settings.InUse.GetValueOrDefault(_state.CarId)?.Km) ?? 0;

        /// <summary>Taken from the battery over the kilometres since the car was taken into use, at the lifetime average.</summary>
        double? OwnOut => InUseSince != null ? EnergyCost.Since(LifeOut, Now?.Odometer, InUseKm) : null;

        public double? LifeKwhYear => EnergyCost.PerYear(EnergyCost.FromGrid(OwnOut, Settings.ChargeLossPercent), InUseSince, Now?.Time ?? DateTime.Now);
        public double? LifeCostYear => EnergyCost.PerYear(EnergyCost.Euros(OwnOut, Settings.PriceCents, Settings.ChargeLossPercent), InUseSince, Now?.Time ?? DateTime.Now);

        public string LifeCostNote => Settings.PriceCents is double c
            ? Loc.I.Format("LifeCostNote", c.ToString("0.##", CultureInfo.CurrentCulture), Settings.ChargeLossPercent.ToString("0.#", CultureInfo.CurrentCulture))
            : Loc.I["LifeCostNoPrice"];

        public string LifeYearNote => InUseSince is DateTime d && Now?.Odometer is double km
            ? Loc.I.Format("LifeYearNote", Math.Max(0, km - InUseKm).ToString("N0", CultureInfo.CurrentCulture),
                Math.Max(0, (Now.Time - d).TotalDays / 365.25).ToString("F1", CultureInfo.CurrentCulture))
            : Loc.I["LifeYearNoStart"];

        /// <summary>The date taken into use, shown in the language's date format and typed in one ParseDate reads; empty clears it.</summary>
        public string InUseText
        {
            get => InUseSince?.ToString("d", CultureInfo.CurrentCulture) ?? "";
            set
            {
                DateTime? date = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (ParseDate(value) is not DateTime d || d > DateTime.Now || d.Year < 1990) { Reject(nameof(InUseText), value, "InUseDateRule"); return; }
                    date = d.Date;
                }
                SaveInUse(date, InUseKm);
            }
        }

        /// <summary>The odometer when the car was taken into use, km; empty is 0, a new car.</summary>
        public string InUseKmText
        {
            get => InUseKm.ToString("0", CultureInfo.CurrentCulture);
            set
            {
                double km = 0;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (ParseKm(value) is not double k) { Reject(nameof(InUseKmText), value, "InUseKmRule"); return; }
                    km = k;
                }
                SaveInUse(InUseSince, km);
            }
        }

        void SaveInUse(DateTime? since, double km)
        {
            if (_state.Vehicle is { } v)
            {
                (v.InUseSince, v.InUseKm) = (since, km);
                try { _state.SaveVehicles(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _state.Log(Loc.I.Format("VehicleSaveFailed", ex.Message)); }
            }
            else
            {
                Settings.InUse[_state.CarId] = new InUseEntry { Since = since, Km = km };
                Settings.Save();
            }
            InputError = "";
            RaiseLifetime();
        }

        /// <summary>Why the last value typed was not taken; empty once one is.</summary>
        public string InputError { get => _inputError; private set => Set(ref _inputError, value); }
        string _inputError = "";

        void Reject(string property, string? typed, string rule)
        {
            InputError = Loc.I.Format("InputRejected", (typed ?? "").Trim(), Loc.I[rule]);
            Raise(property);        // the box shows the value kept again
        }

        /// <summary>
        /// Whole kilometres, 0 to 2 000 000. Spaces (no-break ones too) are thousands separators, and so are a comma
        /// or a full stop before groups of three digits ("29,690", "29.690"); anything else with them is refused,
        /// so "45,000" never becomes 45 km.
        /// </summary>
        internal static double? ParseKm(string text)
        {
            string s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9]{1,3}([.,][0-9]{3})+$")) s = s.Replace(",", "").Replace(".", "");
            return System.Text.RegularExpressions.Regex.IsMatch(s, @"^[0-9]{1,7}$") && double.Parse(s, CultureInfo.InvariantCulture) is var km and <= 2_000_000 ? km : null;
        }

        /// <summary>
        /// A date without a time, whatever the language: with full stops day.month.year ("4.12.2024", "1.5.23"),
        /// with hyphens ISO ("2024-12-04"), anything else in the language's own short date format; null otherwise.
        /// </summary>
        internal static DateTime? ParseDate(string text)
        {
            string s = text.Trim();
            var (formats, culture) = s.Contains('.') ? (new[] { "d.M.yyyy", "d.M.yy" }, CultureInfo.InvariantCulture)
                : s.Contains('-') ? (new[] { "yyyy-MM-dd" }, CultureInfo.InvariantCulture)
                : (new[] { CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern }, CultureInfo.CurrentCulture);
            if (DateTime.TryParseExact(s, formats, culture, DateTimeStyles.None, out var d)) return d;
            // the box shows the date in the culture's own short form (4-12-2024 in nl-NL, 2024. 12. 04. in hu-HU): that reads back too
            string own = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
            return DateTime.TryParseExact(s, new[] { own, own.Replace("dd", "d").Replace("MM", "M") }, CultureInfo.CurrentCulture,
                DateTimeStyles.None, out d) ? d : null;
        }

        /// <summary>Cents per kWh as typed; empty clears the price. Text, so a decimal comma works in any culture.</summary>
        public string PriceText
        {
            get => Settings.PriceCents?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
            set
            {
                if (string.IsNullOrWhiteSpace(value)) Settings.PriceCents = null;
                else if (ParseNumber(value) is double c and >= 0 and < 1000) Settings.PriceCents = c;
                else { Reject(nameof(PriceText), value, "PriceRule"); return; }
                Settings.Save();
                InputError = "";
                RaiseLifetime();
            }
        }

        /// <summary>Charging loss in percent as typed.</summary>
        public string LossText
        {
            get => Settings.ChargeLossPercent.ToString("0.#", CultureInfo.CurrentCulture);
            set
            {
                if (ParseNumber(value ?? "") is double l and >= 0 and < 50) Settings.ChargeLossPercent = l;
                else { Reject(nameof(LossText), value, "LossRule"); return; }
                Settings.Save();
                InputError = "";
                RaiseLifetime();
            }
        }

        static double? ParseNumber(string text) =>
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        void RaiseLifetime()
        {
            foreach (var p in new[] { nameof(PriceText), nameof(LossText), nameof(InUseText), nameof(InUseKmText), nameof(LifeFromGrid), nameof(LifeCost),
                                      nameof(LifeKwhYear), nameof(LifeCostYear), nameof(LifeCostNote), nameof(LifeYearNote), nameof(LifeInNote) }) Raise(p);
        }

        // ---- battery ---------------------------------------------------------

        public bool ShowCellHealth
        {
            get => _showCellHealth;
            set { if (Set(ref _showCellHealth, value)) { if (value) ShowCellLoad = false; RaiseDerived(); } }
        }

        /// <summary>The cells' extra sag under load, mV per 100 A, in this session or an opened recording.</summary>
        public bool ShowCellLoad
        {
            get => _showCellLoad;
            set { if (Set(ref _showCellLoad, value)) { if (value) ShowCellHealth = false; RaiseDerived(); } }
        }

        /// <summary>The drive the under-load view shows: a recording opened for it, else this session.</summary>
        DriveStats LoadDrive => _shownDrive ?? _drive;

        /// <summary>True while the under-load view shows an opened recording rather than this session.</summary>
        public bool HasShownRecording => _shownDrive != null;

        public double[]? CellValues => ShowCellLoad ? LoadDrive.ExtraPer100AByCell() : ShowCellHealth ? Now?.CellSoh : Now?.CellVoltages;

        /// <summary>Under load the colours are the grades (BatteryChecks), not the spread of the values; NaN otherwise.</summary>
        public double CellWarnAt => ShowCellLoad ? BatteryChecks.CellLoadWatchAt(LoadDrive.LoadCurrentScaled, LoadDrive.NoiseFloorMv) : double.NaN;
        // in a cold pack no cell is shown red (see BatteryChecks.CellLoadColdC)
        public double CellBadAt => !ShowCellLoad ? double.NaN
            : BatteryChecks.TooColdForLoad(LoadDrive.LoadTempMin) ? double.PositiveInfinity
            : BatteryChecks.CellLoadBadAt(LoadDrive.LoadCurrentScaled, LoadDrive.NoiseFloorMv);

        /// <summary>What the under-load view rests on, or what it still needs.</summary>
        public string CellLoadStatus
        {
            get
            {
                if (!ShowCellLoad) return "";
                var d = LoadDrive;
                string text = d.CanRankCells
                    ? Loc.I.Format("CellLoadReady", d.LoadReads, d.RestReads, (d.LoadCurrentScaled ?? 0).ToString("F0", CultureInfo.CurrentCulture))
                    : _shownDrive != null
                        ? Loc.I.Format("CellLoadTooFew", d.LoadReads, DriveStats.MinLoadReads, DriveStats.LoadCurrentA.ToString("F0", CultureInfo.CurrentCulture),
                            d.RestReads, DriveStats.MinRestReads, BatteryChecks.RestCurrentA.ToString("F0", CultureInfo.CurrentCulture))
                        : Loc.I.Format("CellLoadWaiting", d.LoadReads, DriveStats.MinLoadReads, DriveStats.LoadCurrentA.ToString("F0", CultureInfo.CurrentCulture),
                            d.RestReads, DriveStats.MinRestReads, BatteryChecks.RestCurrentA.ToString("F0", CultureInfo.CurrentCulture));
                return _shownRecording == null ? text : Loc.I.Format("CellLoadFromRecording", _shownRecording) + " " + text;
            }
        }

        /// <summary>The Battery tab's hint to start the live view: shown while it is stopped, with no cells and no recording to show.</summary>
        public bool WaitingForCells => !IsRunning && Now?.CellVoltages == null && _shownDrive == null;

        /// <summary>The car file says where the cells' health is; without it the toggle is hidden.</summary>
        public bool HasCellHealth => _state.Catalog.LiveOf("cell_soh") != null;

        /// <summary>The power limit tiles show only when the car file names them.</summary>
        public bool HasMaxCharge => _state.Catalog.LiveOf("max_charge_power") != null;
        public bool HasMaxDischarge => _state.Catalog.LiveOf("max_discharge_power") != null;
        // a temperature tile only where the car file reads that temperature
        public bool HasMotorTemp => _state.Catalog.LiveOf("motor_temp") != null;
        public bool HasInverterTemp => _state.Catalog.LiveOf("inverter_temp") != null;
        public bool HasChargerTemp => _state.Catalog.LiveOf("charger_temp") != null;
        public bool HasDcDcTemp => _state.Catalog.LiveOf("dcdc_temp") != null;
        public bool HasCoolantTemp => _state.Catalog.LiveOf("coolant_temp") != null;
        public bool HasAuxBatteryTemp => _state.Catalog.LiveOf("aux_battery_temp") != null;

        public double? CellMin => CellValues?.Min();
        public double? CellMax => CellValues?.Max();
        public double? CellAvg => CellValues?.Average();
        public int? CellMinNo => CellValues is { } v ? Array.IndexOf(v, v.Min()) + 1 : null;
        public int? CellMaxNo => CellValues is { } v ? Array.IndexOf(v, v.Max()) + 1 : null;
        /// <summary>What the colours of the cell map mean: limits under load, the order of the cells otherwise.</summary>
        public string CellLegend => ShowCellLoad
            ? BatteryChecks.TooColdForLoad(LoadDrive.LoadTempMin)
                ? Loc.I.Format("LegendLoadCold", CellWarnAt.ToString("F0", CultureInfo.CurrentCulture),
                    (LoadDrive.LoadTempMin ?? 0).ToString("F0", CultureInfo.CurrentCulture), BatteryChecks.CellLoadColdC.ToString("F0", CultureInfo.CurrentCulture))
                : Loc.I.Format("LegendLoad", CellWarnAt.ToString("F0", CultureInfo.CurrentCulture), CellBadAt.ToString("F0", CultureInfo.CurrentCulture))
            : ShowCellHealth ? Loc.I["LegendHealth"] : Loc.I["LegendCells"];

        /// <summary>mV for voltages, percentage points for health, mV / 100 A under load.</summary>
        public double? CellSpreadShown => CellValues is { } v ? (v.Max() - v.Min()) * (ShowCellHealth || ShowCellLoad ? 1 : 1000) : null;
        public string CellFormat => ShowCellHealth || ShowCellLoad ? "F1" : "F3";
        public string CellUnit => ShowCellLoad ? "mV/100 A" : ShowCellHealth ? "%" : "V";
        public string CellSpreadUnit => ShowCellLoad ? "mV/100 A" : ShowCellHealth ? "%" : "mV";
        public string CellTitle => Loc.I[ShowCellLoad ? "CellsUnderLoadTitle" : ShowCellHealth ? "CellHealth" : "CellVoltages"];
        public double CellMinimumSpan => ShowCellHealth ? 2 : 0.02;

        public double? ProbeMin => Now?.ModuleTemps?.Min();
        public double? ProbeMax => Now?.ModuleTemps?.Max();
        public double? ProbeAvg => Now?.ModuleTemps?.Average();

        /// <summary>
        /// The range for the charge gauge: at the drive history's consumption (this drive included) and at this trip's, lowest first,
        /// or the one there is; empty with neither.
        /// </summary>
        public string RangeGaugeText
        {
            get
            {
                var known = new[] { RangeHistory, RangeTrip }.OfType<double>().Select(km => Math.Round(km)).Distinct().Order().ToList();
                string F(double km) => km.ToString("F0", CultureInfo.CurrentCulture);
                return known.Count switch
                {
                    0 => "",
                    1 => Loc.I.Format("RangeGauge", F(known[0])),
                    _ => Loc.I.Format("RangeGaugeSpan", F(known[0]), F(known[^1]))
                };
            }
        }

        /// <summary>The motor speed for the speed gauge, e.g. "5 763 rpm"; empty without a reading.</summary>
        public string RpmText => Now?.MotorRpm is double rpm ? $"{rpm.ToString("N0", CultureInfo.CurrentCulture)} rpm" : "";

        public string ChargingText => Now?.Charging switch
        {
            true => Loc.I["Yes"],
            false => Loc.I["No"],
            _ => "—"
        };

        /// <summary>Selector position as the car file names it; a code it does not name shows as a number so it can be looked up.</summary>
        public string GearText => CodeText("gear", Now?.Gear);

        public string DriveModeText => CodeText("drive_mode", Now?.DriveMode);

        string CodeText(string live, int? code) =>
            code is int c ? _state.Catalog.LiveOf(live)?.Text(c) ?? c.ToString(CultureInfo.CurrentCulture) : "—";

        // ---- report ------------------------------------------------------------

        /// <summary>Printed under the car's name, e.g. whose car it is.</summary>
        public string ReportNote { get => _reportNote; set => Set(ref _reportNote, value); }

        /// <summary>The last report made; for the self-test.</summary>
        internal string? LastReport { get => _lastReport; private set => Set(ref _lastReport, value); }

        /// <summary>The fault codes on the Fault codes tab, or null if none have been read for this car.</summary>
        public Func<FaultRead?>? FaultCodes { get; set; }

        /// <summary>What the report is made of; null before the first reading. Public for the self-test.</summary>
        public BatteryReport.Input? ReportInput()
        {
            if (Now is not { } now) return null;
            var c = _state.Catalog;
            return new BatteryReport.Input(_state.CarTitle, c.Platform.Cars, Path.GetFileName(_state.CatalogPath),
                ReportNote, WithKept(now.Clone()), CellColumns, ProbeColumns, FaultCodes?.Invoke(), BatteryEcus(), _state.IsDemoCar,
                _drive, null, Vin.Value, Vin.Asked, Vin.Read, c.Platform.Warranty);
        }

        /// <summary>The battery management's date and versions, when the reading has none (a recording), from the vehicle, with when they were read.</summary>
        VehicleState WithKept(VehicleState s)
        {
            if (_state.Vehicle is { } v && s.BatteryMade == null && s.BmsSoftware == null && s.BmsHardware == null
                && (v.BatteryMade != null || v.BmsSoftware != null || v.BmsHardware != null))
            {
                (s.BatteryMade, s.BmsSoftware, s.BmsHardware) = (v.BatteryMade, v.BmsSoftware, v.BmsHardware);
                s.BatteryInfoRead = v.BatteryInfoRead;
            }
            return s;
        }

        /// <summary>The VIN read at this connection, else the one kept with the vehicle (with when it was read).</summary>
        (string? Value, bool Asked, DateTime? Read) Vin =>
            _state.Vin != null ? (_state.Vin, true, null)
            : _state.Vehicle?.Vin is string kept ? (kept, true, _state.Vehicle.VinRead)
            : (null, _state.VinAsked || _state.Vehicle?.VinNotGiven == true, null);

        void ChooseRecording()
        {
            if (PickRecording() is string recording) ReportFromFile(recording);
        }

        /// <summary>This car's recordings, newest first, each described by its time, distance and cell reads.</summary>
        public IReadOnlyList<RecordingChoice> RecordingChoices() =>
            Results.Files(Results.Live, "live_*.csv").Where(f => !Recording.IsCellsFile(f)).Select(f =>
            {
                try
                {
                    var i = Recording.Describe(f);
                    string from = i.Start?.ToString("g", CultureInfo.CurrentCulture) ?? "?";
                    string to = i.End?.ToString("t", CultureInfo.CurrentCulture) ?? "?";
                    double minutes = i.Start is DateTime s && i.End is DateTime e ? (e - s).TotalMinutes : 0;
                    return new RecordingChoice(f, Loc.I.Format("RecordingLine", from, to, minutes.ToString("F0", CultureInfo.CurrentCulture),
                        i.Km.ToString("F1", CultureInfo.CurrentCulture), i.LoadReads, i.RestReads));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // one unreadable recording is listed by its name, the others as usual
                    return new RecordingChoice(f, Path.GetFileName(f));
                }
            }).ToList();

        string? PickRecording()
        {
            var choices = RecordingChoices();
            if (choices.Count == 0)
            {
                AsyncCommand.Report(new InvalidOperationException(Loc.I["RecordingNone"]));
                return null;
            }
            return AppHost.I.PickRecording(choices);
        }

        /// <summary>
        /// A report of a recorded session, as if it had just been driven: the
        /// last reading of the file and the drive statistics of all of it. The
        /// fault codes are the ones on the Fault codes tab, with their own date.
        /// Public for the self-test; returns the PDF written, or null.
        /// </summary>
        public string? ReportFromFile(string recording)
        {
            try
            {
                // either file of a recording may be picked
                recording = Recording.MainFile(recording);
                var (states, drive) = ReadRecording(recording);
                var c = _state.Catalog;
                var input = new BatteryReport.Input(_state.CarTitle, c.Platform.Cars, Path.GetFileName(_state.CatalogPath),
                    ReportNote, WithKept(states[^1]), CellColumns, ProbeColumns, FaultCodes?.Invoke(), BatteryEcus(), _state.IsDemoCar,
                    drive, Path.GetFileName(recording), Vin.Value, Vin.Asked, Vin.Read, c.Platform.Warranty);
                return WriteReport(input);
            }
            catch (Exception ex)
            {
                AsyncCommand.Report(ex);
                return null;
            }
        }

        /// <summary>
        /// A recording of this car (either file of it may be given) as readings and the drive
        /// statistics of all of them. Throws when it belongs to another car or holds nothing.
        /// </summary>
        (IReadOnlyList<VehicleState> States, DriveStats Drive) ReadRecording(string recording)
        {
            recording = Recording.MainFile(recording);
            // the results are this car's: a recording from another car's folder would be shown as this one
            var folder = Path.GetFullPath(Path.GetDirectoryName(recording)!).TrimEnd('\\');
            if (!string.Equals(folder, Path.GetFullPath(Results.Folder(Results.Live)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.I.Format("ReportRecordingOtherCar", Path.GetFileName(recording),
                    Path.GetFileName(Path.GetDirectoryName(folder)) ?? folder));
            var states = Recording.Read(recording);
            if (states.Count == 0) throw new InvalidDataException(Loc.I.Format("ReportRecordingEmpty", Path.GetFileName(recording)));
            var drive = new DriveStats();
            foreach (var s in states) drive.Add(s);
            return (states, drive);
        }

        void ChooseRecordingToShow()
        {
            if (PickRecording() is string recording) ShowRecording(recording);
        }

        /// <summary>
        /// Shows a recording in the cells-under-load view, or this session again with null.
        /// Public for the self-test; false when the recording could not be read.
        /// </summary>
        public bool ShowRecording(string? recording)
        {
            try
            {
                if (recording == null) { _shownDrive = null; _shownRecording = null; }
                else
                {
                    _shownDrive = ReadRecording(recording).Drive;
                    _shownRecording = Path.GetFileName(Recording.MainFile(recording));
                    ShowCellLoad = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                AsyncCommand.Report(ex);
                return false;
            }
            finally
            {
                RaiseDerived();
            }
        }

        string WriteReport(BatteryReport.Input input)
        {
            var path = Results.NewFile(Results.Reports, "battery_report", ".pdf");
            BatteryReport.Write(input, path);
            LastReport = path;
            _state.Log(Loc.I.Format("ReportSaved", path));
            // the report is saved and logged; without a PDF viewer it just does not open
            try { AppHost.I.Open(path); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return path;
        }

        static readonly string[] BatteryValues = { "soc", "voltage", "current", "soh", "cells", "cell_min", "cell_max", "probes" };

        /// <summary>The ECUs the car file reads the battery values from: their fault codes are the battery's.</summary>
        HashSet<string> BatteryEcus() => BatteryValues
            .Select(_state.Catalog.LiveOf).OfType<LiveSource>()
            .Select(l => _state.Catalog.TryEcu(l.Ecu)?.Id).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>A PDF of what the battery and dashboard show now, opened when written.</summary>
        void MakeReport()
        {
            if (ReportInput() is not { } input) return;
            try
            {
                WriteReport(input);
            }
            catch (Exception ex)
            {
                AsyncCommand.Report(ex);
            }
        }

        // ---- what the car file says about the layout --------------------------

        GaugeLimits Gauges => _state.Catalog.Platform.Gauges;
        static readonly GaugeLimits Defaults = new();

        // a range the car file gives as anything but [min, max] with min < max falls back to the default
        static (double Min, double Max) Range(double[] given, double[] fallback) =>
            given is [var a, var b, ..] && a < b ? (a, b) : (fallback[0], fallback[1]);

        public double PowerMin => Range(Gauges.PowerKw, Defaults.PowerKw).Min;
        public double PowerMax => Range(Gauges.PowerKw, Defaults.PowerKw).Max;
        public double CurrentMin => Range(Gauges.CurrentA, Defaults.CurrentA).Min;
        public double CurrentMax => Range(Gauges.CurrentA, Defaults.CurrentA).Max;
        public double SpeedMax => Range(Gauges.SpeedKmh, Defaults.SpeedKmh).Max;

        /// <summary>Cells and temperature probes per row in the battery view.</summary>
        public int CellColumns => _state.Catalog.LiveOf("cells")?.Columns ?? 12;
        public int ProbeColumns => _state.Catalog.LiveOf("probes")?.Columns ?? 12;

        // ---- range -----------------------------------------------------------

        Drive CurrentDrive => new(_driveStart, _driveKm, _driveKwh,
            _driveAmbientWeight > 0 ? _driveAmbientKm / _driveAmbientWeight : null);

        void SaveDrive()
        {
            // the demo car's simulated drive must not end up in the real history
            if (_driveKm >= 1 && !_state.Adapter.IsDemo && !_state.IsDemoCar) _history.Save(CurrentDrive);
        }

        /// <summary>Consumption from the chosen car's own drives, the running one included.</summary>
        RangeEstimator.Result? HistoryConsumption =>
            RangeEstimator.Consumption(_history.Drives.Where(d => d.Start != _driveStart).Append(CurrentDrive), Now?.Ambient);

        public double? RangeHistory => RangeEstimator.Range(Now?.Energy, HistoryConsumption?.KwhPer100);

        /// <summary>Only after a few km; the first minutes of a drive (cold start, a hill) mislead.</summary>
        public double? RangeTrip => TripKm >= 5 ? RangeEstimator.Range(Now?.Energy, TripAverage) : null;

        /// <summary>The consumption behind RangeHistory, or how far the history still has to grow.</summary>
        public string HistoryValue => HistoryConsumption is { } h
            ? h.KwhPer100.ToString("F1", CultureInfo.CurrentCulture)
            : $"{HistoryKm:F0}/{RangeEstimator.MinKm:F0}";

        public string HistoryUnit => HistoryConsumption != null ? "kWh/100 km" : "km";

        public string HistoryTitle => Loc.I[HistoryConsumption != null ? "HistoryTitle" : "HistoryCollecting"];

        /// <summary>What the consumption behind the range rests on, or what it still needs.</summary>
        public string HistoryNote => HistoryConsumption is { } h
            ? Loc.I.Format(h.SimilarTemperature ? "HistorySimilar" : "HistoryAll", h.Km.ToString("F0", CultureInfo.CurrentCulture))
            : Loc.I.Format("HistoryCollectingNote", RangeEstimator.MinKm.ToString("F0", CultureInfo.CurrentCulture));

        double HistoryKm => _history.Drives.Where(d => d.Start != _driveStart).Sum(d => d.Km) + _driveKm;

        enum Flow { Idle, Charging, Regen, Driving, Standing }

        /// <summary>
        /// Which way energy is going. Power is negative into the battery, which
        /// is either the charger (standing still) or regeneration (moving).
        /// </summary>
        Flow CurrentFlow
        {
            get
            {
                if (Now?.Power is not double p) return Flow.Idle;
                bool moving = Now.Speed > 2;
                if (p < -0.2) return moving ? Flow.Regen : Flow.Charging;
                // on the charger but the car draws more than it gets, e.g. preheating
                if (p > 0.2) return moving ? Flow.Driving : Flow.Standing;
                return Flow.Idle;
            }
        }

        public string PowerTitle => Loc.I[CurrentFlow switch
        {
            Flow.Charging => "PowerCharging",
            Flow.Regen => "PowerRegen",
            Flow.Driving => "PowerDriving",
            Flow.Standing => "PowerStanding",
            _ => "Power"
        }];

        public string CurrentTitle => Loc.I[CurrentFlow switch
        {
            Flow.Charging or Flow.Regen => "CurrentIn",
            Flow.Driving or Flow.Standing => "CurrentOut",
            _ => "Current"
        }];

        /// <summary>
        /// Rough time to 100 % at the present rate. Charging slows near full,
        /// so the real time is somewhat longer.
        /// </summary>
        public string ChargeEta
        {
            get
            {
                if (CurrentFlow != Flow.Charging || Now is not { Power: double p, Soc: double soc, Energy: double kwh }
                    || soc < 1 || soc >= 99.5 || p > -0.2) return "—";
                double hours = kwh * (100 - soc) / soc / -p;
                if (hours > 48) return "—";
                var t = TimeSpan.FromHours(hours);
                return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
            }
        }

        // ---- charts ----------------------------------------------------------

        public ChartSeries PowerSeries { get; } = new("", 0x22D3EE);
        public ChartSeries SpeedSeries { get; } = new("", 0xA78BFA, "F0");
        public ChartSeries SocSeries { get; } = new("", 0x34D399);
        public ChartSeries CurrentSeries { get; } = new("", 0xFBBF24);
        public ChartSeries VoltageSeries { get; } = new("", 0x60A5FA);
        public ChartSeries SpreadSeries { get; } = new("", 0xF87171, "F0");
        public ChartSeries TempMinSeries { get; } = new("", 0x60A5FA, "F0");
        public ChartSeries TempMaxSeries { get; } = new("", 0xF87171, "F0");
        public ChartSeries AmbientSeries { get; } = new("", 0x8B98A5, "F0");

        public IList<ChartSeries> PowerChart => new[] { PowerSeries };
        public IList<ChartSeries> SpeedChart => new[] { SpeedSeries };
        public IList<ChartSeries> SocChart => new[] { SocSeries };
        public IList<ChartSeries> CurrentChart => new[] { CurrentSeries };
        public IList<ChartSeries> VoltageChart => new[] { VoltageSeries };
        public IList<ChartSeries> SpreadChart => new[] { SpreadSeries };
        public IList<ChartSeries> TempChart => new[] { TempMinSeries, TempMaxSeries, AmbientSeries };

        /// <summary>Bumped after new points so the charts redraw.</summary>
        public int Revision { get => _revision; private set => Set(ref _revision, value); }

        public double WindowMinutes
        {
            get => _windowMinutes;
            set { if (Set(ref _windowMinutes, value)) Raise(nameof(WindowSeconds)); }
        }

        public double WindowSeconds => _windowMinutes * 60;
        public double[] WindowChoices { get; } = { 1, 2, 5, 10, 30 };

        // ---------------------------------------------------------------------

        async Task RunAsync()
        {
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _driveStart = DateTime.Now;
            _driveKm = _driveKwh = _driveAmbientKm = _driveAmbientWeight = 0;
            _driveSavedKm = 0;
            _drive = new DriveStats();      // the report's drive section covers this session, as its recording does
            _shownDrive = null;             // and the under-load view follows it
            _shownRecording = null;
            TripKm = TripKwh = TripRegenKwh = ChargedKwh = PeakPowerKw = PeakRegenKw = 0;
            _tripSocStart = null;
            _lastTime = null;               // the pause before this session is no driving
            RaiseDerived();
            _recordPending = true;          // the file is made with the first reading, so a start that fails leaves none
            var progress = new Progress<VehicleState>(OnState);

            try
            {
                await _state.Adapter.RunAsync("TabDashboard", (scanner, ct) =>
                {
                    var poller = new LivePoller(scanner, _state.Catalog);
                    while (!ct.IsCancellationRequested)
                    {
                        var started = DateTime.UtcNow;
                        ((IProgress<VehicleState>)progress).Report(poller.Poll());
                        int left = MinRoundMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                        if (left > 0) ct.WaitHandle.WaitOne(left);
                    }
                }, _cts.Token);
            }
            finally
            {
                SaveDrive();
                StopRecording();
                IsRunning = false;
                _cts.Dispose();
                _cts = null;
            }
        }

        /// <summary>Negative power is the charger's: the car is known to stand (2 km/h or less) or says it charges.</summary>
        static bool OnCharger(VehicleState s) => s.Charging == true || s.Speed is double sp && sp <= 2;

        void OnState(VehicleState s)
        {
            if (_lastTime is DateTime last)
            {
                double hours = (s.Time - last).TotalHours;
                if (hours is > 0 and < 0.01)        // ignore gaps (pauses, reconnects)
                {
                    bool moving = s.Speed > 2;
                    TripKm += Math.Max(0, s.Speed ?? 0) * hours;
                    if (s.Power is double p)
                    {
                        // negative power is the charger when the car is known to stand or says it charges;
                        // with no speed reading and no charging it counts as regeneration
                        if (p >= 0) TripKwh += p * hours; else if (OnCharger(s)) ChargedKwh += -p * hours; else TripRegenKwh += -p * hours;
                    }
                    if (s.Power is double dp)
                    {
                        if (moving)
                        {
                            double km = s.Speed!.Value * hours;
                            _driveKm += km;
                            _driveKwh += dp * hours;
                            if (s.Ambient is double a) { _driveAmbientKm += a * km; _driveAmbientWeight += km; }
                            if ((int)_driveKm > _driveSavedKm) { _driveSavedKm = (int)_driveKm; SaveDrive(); }
                        }
                        // heating at a red light or before setting off is part of what a drive costs;
                        // charging is not
                        else if (dp > 0 && s.Charging != true) _driveKwh += dp * hours;
                    }
                }
            }
            _lastTime = s.Time;
            _tripSocStart ??= s.Soc;
            if (s.Power is double pw)
            {
                PeakPowerKw = Math.Max(PeakPowerKw, pw);
                if (!OnCharger(s)) PeakRegenKw = Math.Max(PeakRegenKw, -pw);   // on the charger, negative power is not regeneration
            }

            // the charts' time runs from the first point
            if (_started == default || AllSeries.All(x => x.Points.Count == 0)) _started = s.Time;
            double t = (s.Time - _started).TotalSeconds;
            PowerSeries.Add(t, s.Power, KeepSeconds);
            SpeedSeries.Add(t, s.Speed, KeepSeconds);
            SocSeries.Add(t, s.Soc, KeepSeconds);
            CurrentSeries.Add(t, s.Current, KeepSeconds);
            VoltageSeries.Add(t, s.Voltage, KeepSeconds);
            SpreadSeries.Add(t, s.CellSpread * 1000, KeepSeconds);
            TempMinSeries.Add(t, s.TempMin, KeepSeconds);
            TempMaxSeries.Add(t, s.TempMax, KeepSeconds);
            AmbientSeries.Add(t, s.Ambient, KeepSeconds);

            _drive.Add(s);
            Now = s;
            Revision++;
            WriteCsv(s);
        }

        void RaiseDerived()
        {
            foreach (var p in new[]
            {
                nameof(TripKm), nameof(TripKwh), nameof(TripRegenKwh), nameof(TripAverage), nameof(PeakPowerKw), nameof(PeakRegenKw),
                nameof(TripNetKwh), nameof(TripSocUsed), nameof(TripSocNote), nameof(ChargedKwh),
                nameof(HasLifetime), nameof(LifeIn), nameof(LifeInNote), nameof(LifeOut), nameof(LifeAverage), nameof(LifeFromGrid), nameof(LifeCost),
                nameof(LifeKwhYear), nameof(LifeCostYear), nameof(LifeCostNote), nameof(LifeYearNote),
                nameof(CellValues), nameof(WaitingForCells), nameof(CellMin), nameof(CellMax), nameof(CellAvg), nameof(CellMinNo), nameof(CellMaxNo),
                nameof(CellSpreadShown), nameof(CellFormat), nameof(CellUnit), nameof(CellSpreadUnit), nameof(CellTitle),
                nameof(CellMinimumSpan), nameof(CellWarnAt), nameof(CellBadAt), nameof(CellLoadStatus), nameof(CellLegend), nameof(HasShownRecording), nameof(ProbeMin), nameof(ProbeMax), nameof(ProbeAvg), nameof(ChargingText), nameof(RpmText), nameof(RangeGaugeText),
                nameof(PowerTitle), nameof(CurrentTitle), nameof(ChargeEta), nameof(GearText), nameof(DriveModeText),
                nameof(RangeHistory), nameof(RangeTrip), nameof(HistoryValue), nameof(HistoryUnit), nameof(HistoryTitle), nameof(HistoryNote)
            }) Raise(p);
        }

        /// <summary>Empties the charts; the drive, trip and cells under load go on.</summary>
        void ClearChartPoints()
        {
            foreach (var s in AllSeries) s.Points.Clear();
            _started = DateTime.Now;
            Revision++;
        }

        /// <summary>Everything of the session: the charts, the drive and trip figures, cells under load and an opened recording.</summary>
        void ClearSeries()
        {
            foreach (var s in AllSeries) s.Points.Clear();
            _drive = new DriveStats();
            _shownDrive = null;
            _shownRecording = null;
            _started = DateTime.Now;
            TripKm = TripKwh = TripRegenKwh = ChargedKwh = PeakPowerKw = PeakRegenKw = 0;
            _tripSocStart = null;
            _lastTime = null;
            RaiseDerived();
            Revision++;
        }

        IEnumerable<ChartSeries> AllSeries => new[]
        {
            PowerSeries, SpeedSeries, SocSeries, CurrentSeries, VoltageSeries, SpreadSeries, TempMinSeries, TempMaxSeries, AmbientSeries
        };

        void RenameSeries()
        {
            PowerSeries.Title = Loc.I["Power"];
            SpeedSeries.Title = Loc.I["Speed"];
            SocSeries.Title = Loc.I["Soc"];
            CurrentSeries.Title = Loc.I["Current"];
            VoltageSeries.Title = Loc.I["Voltage"];
            SpreadSeries.Title = Loc.I["CellSpread"];
            TempMinSeries.Title = Loc.I["Min"];
            TempMaxSeries.Title = Loc.I["Max"];
            AmbientSeries.Title = Loc.I["Ambient"];
            Raise(nameof(CellTitle));
            Raise(nameof(ChargingText));
            Revision++;
        }

        // ---- CSV recording ---------------------------------------------------

        void StartRecording()
        {
            try
            {
                var path = Results.NewFile(Results.Live, "live", ".csv");
                _csv = new StreamWriter(path) { AutoFlush = true };
                _csv.WriteLine(string.Join(";", Recording.Columns));
                RecordingFile = path;
                _state.Log(Loc.I.Format("Recording", path));
            }
            catch (Exception ex)
            {
                _csv = null;
                RecordingFile = null;     // not the previous session's file
                _state.Log($"{Loc.I["Error"]}: {ex.Message}");
            }
        }

        void WriteCsv(VehicleState s)
        {
            if (_recordPending)
            {
                _recordPending = false;
                StartRecording();
            }
            if (_csv == null) return;
            try
            {
                WriteCsvLine(s);
            }
            catch (Exception ex)
            {
                // e.g. Documents on a drive that went away: stop recording, keep the live view
                StopRecording();
                RecordingFile = null;
                AsyncCommand.Report(ex);
            }
        }

        void WriteCsvLine(VehicleState s)
        {
            _csv!.WriteLine(Recording.Line(s));

            WriteCellsLine(s);
            WriteHealthLine(s);
        }

        /// <summary>Cell health changes rarely: a row only when it changed, so a report made from the recording has it.</summary>
        void WriteHealthLine(VehicleState s)
        {
            if (s.CellSoh is not { Length: > 0 } health || RecordingFile == null) return;
            if (_lastHealth != null && health.SequenceEqual(_lastHealth)) return;
            // fewer cells than the file's columns (a short read): left out rather than misplaced;
            // more (the first read came short): the file starts over with the full count
            if (_lastHealth != null && health.Length < _lastHealth.Length) return;
            if (_lastHealth != null && health.Length > _lastHealth.Length)
            {
                _healthCsv?.Dispose();
                _healthCsv = null;
            }
            if (_healthCsv == null)
            {
                _healthCsv = new StreamWriter(Recording.HealthFile(RecordingFile)) { AutoFlush = true };
                _healthCsv.WriteLine(string.Join(";", new[] { "time" }.Concat(Enumerable.Range(1, health.Length).Select(i => $"health_{i}_pct"))));
            }
            _lastHealth = (double[])health.Clone();
            _healthCsv.WriteLine(string.Join(";", new[] { s.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) }
                .Concat(health.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))));
        }

        /// <summary>
        /// Every cell voltage and temperature probe, one row per fresh reading
        /// (every ten rounds, more often under load), in a file of its own next to the main one:
        /// live_..._cells.csv. Created with the first cell reading; probes that come
        /// later, or more cells than the first read held, start a new file
        /// (live_..._cells_2.csv) with their columns.
        /// </summary>
        void WriteCellsLine(VehicleState s)
        {
            if (s.CellsTime == null || s.CellsTime == _lastCellsTime || RecordingFile == null) return;
            // the file starts with the cells; probes that come later (or never) get a new file with their columns
            if (_cellsCsv == null && s.CellVoltages == null) return;
            _lastCellsTime = s.CellsTime;

            var volts = s.CellVoltages ?? Array.Empty<double>();
            var temps = s.ModuleTemps ?? Array.Empty<double>();

            // more cells or probes than the file has columns for (the first read came short): a new file, so none are lost
            if (_cellsCsv != null && (volts.Length > _cellsColumns.Cells || temps.Length > _cellsColumns.Probes))
            {
                _cellsCsv.Dispose();
                _cellsCsv = null;
                _cellsFiles++;
            }
            if (_cellsCsv == null)
            {
                _cellsColumns = (volts.Length, temps.Length);
                var path = Recording.CellsFile(RecordingFile!, _cellsFiles + 1);
                _cellsCsv = new StreamWriter(path) { AutoFlush = true };
                _cellsCsv.WriteLine(string.Join(";", new[] { "time" }
                    .Concat(Enumerable.Range(1, volts.Length).Select(i => $"cell_{i}_v"))
                    .Concat(Enumerable.Range(1, temps.Length).Select(i => $"probe_{i}_c"))));
            }

            // keep the columns stable even if one read came back short
            static IEnumerable<string> Cols(double[] v, int n, string format) =>
                Enumerable.Range(0, n).Select(i => i < v.Length ? v[i].ToString(format, CultureInfo.InvariantCulture) : "");

            _cellsCsv.WriteLine(string.Join(";", new[] { s.CellsTime.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) }
                .Concat(Cols(volts, _cellsColumns.Cells, "0.000"))
                .Concat(Cols(temps, _cellsColumns.Probes, "0"))));
        }

        public void StopRecording()
        {
            _csv?.Dispose();
            _csv = null;
            _cellsCsv?.Dispose();
            _cellsCsv = null;
            _healthCsv?.Dispose();
            _healthCsv = null;
            _lastHealth = null;
            _recordPending = false;
            _cellsFiles = 0;
            _lastCellsTime = null;
        }
    }
}
