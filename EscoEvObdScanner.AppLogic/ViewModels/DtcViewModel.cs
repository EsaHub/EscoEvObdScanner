using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>
    /// One fault code row. Occurred: odometer and date from the freeze frame, when
    /// there is one. NotRun: the ECU only says the check has not run yet, which is
    /// not a fault. EcuId: the catalogue id, empty if the ECU is not in it.
    /// Answered: false on the row of an ECU that gave no fault list at all.
    /// </summary>
    public sealed record DtcRow(string Ecu, string Code, string Severity, string Status, string StatusText,
        string Occurred, string Snapshot, string Extended, bool NotRun = false, string EcuId = "", bool Answered = true)
    {
        /// <summary>The state as shown: ACTIVE, stored, no-data ... in the UI language.</summary>
        public string SeverityShown => Severity switch
        {
            "ACTIVE" => Loc.I["DtcActive"],
            "stored" => Loc.I["DtcStored"],
            "no fault" => Loc.I["DtcNotRun"],
            "no-data" => Loc.I["DtcNoData"],
            "adapter-error" => Loc.I["DtcAdapterError"],
            "unreachable" => Loc.I["DtcUnreachable"],
            "pending" => Loc.I["DtcPendingReply"],
            "none" => Loc.I["NoFaults"],
            _ when !Answered => Loc.I.Format("DtcRefused", Severity),   // a negative reply, by its NRC name
            _ => Severity
        };

        /// <summary>The failure type (the code's last byte) by its SAE J2012-DA name, untranslated; empty where the standard names none.</summary>
        public string FailureTypeShown => Dtc.FailureTypeName(Code) ?? "";

        /// <summary>The status bits as shown: their ISO 14229-1 names, untranslated ("testFailed, confirmedDTC").</summary>
        public string StatusTextShown => StatusText == "clear" ? Loc.I["DtcBitsNone"]
            : string.Join(", ", StatusText.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(Dtc.IsoBitName));

        /// <summary>The same status bits in plain words in the UI language ("failing now, confirmed fault"); empty without bits.</summary>
        public string StatusMeaningShown => StatusText == "clear" ? ""
            : string.Join(", ", StatusText.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(Dtc.IsoBitName)
                .Select(b => Dtc.IsoBitNames.ToList().IndexOf(b)).Where(i => i >= 0).Select(i => Loc.I[$"DtcBit{i}"]));
    }

    /// <summary>
    /// The fault codes on the tab as the battery report sees them: every row,
    /// whether a read from the car ran to the end, and the saved file shown
    /// instead, if any. Saved files hold only the fault codes, not who answered.
    /// </summary>
    public sealed record FaultRead(IReadOnlyList<DtcRow> Rows, bool Complete, string? File, DateTime When);

    /// <summary>A saved fault code read, for the history list.</summary>
    public sealed record DtcFile(string Path, string Text);

    public sealed class DtcViewModel : ObservableObject
    {
        readonly AppState _state;
        bool _deep;
        bool _showNotRun;
        string _summary = "";
        DtcFile? _selectedFile;
        bool _complete;
        bool _hasRead;
        string? _shownFile;
        DateTime _readAt;
        CancellationTokenSource? _cts;
        Func<string>? _summaryText;

        public DtcViewModel(AppState state)
        {
            _state = state;
            Read = new AsyncCommand(ReadAsync, () => _state.Adapter.CanRun);
            Stop = new RelayCommand(() => _cts?.Cancel(), () => _cts != null);
            ShowFile = new RelayCommand(LoadSelected, () => SelectedFile != null && !_state.Adapter.IsBusy);
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(AppState.CatalogPath)) return;
                Rows.Clear();
                _hasRead = false;
                SetSummary(null);
                RefreshFiles();         // the new car's earlier reads
            };
            // the rows, the summary and the file list in the new language
            Loc.I.PropertyChanged += (_, _) =>
            {
                var rows = Rows.ToList();
                Rows.Clear();
                foreach (var row in rows) Rows.Add(row);
                SetSummary(_summaryText);
                RefreshFiles();
            };
            RowsView = AppHost.I.RowView(Rows, o => o is DtcRow r && (ShowNotRun || !r.NotRun));
            RefreshFiles();
        }

        public AsyncCommand Read { get; }
        public RelayCommand Stop { get; }
        public RelayCommand ShowFile { get; }
        public ObservableCollection<DtcRow> Rows { get; } = new();

        /// <summary>Rows on screen: real faults, plus the checks not yet run when asked for.</summary>
        public IRowView RowsView { get; }

        /// <summary>Earlier reads saved in the results folder, newest first.</summary>
        public ObservableCollection<DtcFile> Files { get; } = new();

        public DtcFile? SelectedFile { get => _selectedFile; set => Set(ref _selectedFile, value); }

        public bool Deep { get => _deep; set => Set(ref _deep, value); }

        /// <summary>Also asks the ECUs the car file says have not answered.</summary>
        public bool AllEcus { get => _allEcus; set => Set(ref _allEcus, value); }
        bool _allEcus;

        public bool ShowNotRun
        {
            get => _showNotRun;
            set { if (Set(ref _showNotRun, value)) RowsView.Refresh(); }
        }

        public string Summary { get => _summary; private set => Set(ref _summary, value); }

        /// <summary>The summary, kept as how to word it so that a language switch words it again.</summary>
        void SetSummary(Func<string>? text)
        {
            _summaryText = text;
            Summary = text?.Invoke() ?? "";
        }

        /// <summary>What the tab shows, for the battery report; null if nothing has been read for this car.</summary>
        public FaultRead? CurrentRead() => _hasRead ? new FaultRead(Rows.ToList(), _complete, _shownFile, _readAt) : null;

        async Task ReadAsync()
        {
            // the read shown until now, back on the tab if this one fails before the first ECU's result
            var path = Results.NewFile(Results.Dtc, "dtc");
            var previous = (Rows: Rows.ToList(), Text: _summaryText, _complete, _shownFile, _readAt, _hasRead);
            bool reported = false;
            Rows.Clear();
            SetSummary(null);
            _complete = false;
            _shownFile = null;
            _readAt = DateTime.Now;
            _hasRead = true;            // even cut short: what was read is shown and reported as incomplete
            // an ECU the car file says has not answered only when asked for: a read of it only waits for the time-out
            var ecus = _state.Catalog.Ecus.Where(e => AllEcus || e.Answered != false).ToList();
            bool deep = Deep;
            int active = 0, stored = 0, notFaults = 0;
            (long Value, DateTime Utc)? clock = null;
            var snapClock = new Lazy<(long Value, DateTime Utc)?>(ClockFromSnaps);   // only if a frame needs dating

            var progress = new Progress<(DtcReader.EcuFaults Result, bool Unreachable)>(x =>
            {
                var r = x.Result;
                reported = true;
                if (!r.Answered)
                {
                    string problem = x.Unreachable ? "unreachable" : r.Problem;
                    Rows.Add(new DtcRow(r.Ecu.Name, "—", problem, "", "", "", "", "", EcuId: r.Ecu.Id, Answered: false));
                    return;
                }
                // an ECU with only checks not yet run has no faults, and says so even with those hidden
                if (r.Faults.All(f => f.Dtc.Severity is not ("ACTIVE" or "stored")))
                    Rows.Add(new DtcRow(r.Ecu.Name, "—", "none", "", "", "", "", "", EcuId: r.Ecu.Id));
                foreach (var f in r.Faults)
                {
                    var d = f.Dtc;
                    switch (d.Severity) { case "ACTIVE": active++; break; case "stored": stored++; break; default: notFaults++; break; }
                    Rows.Add(ToRow(r.Ecu, $"{d.Code}-{d.FailureType:X2}", d.Severity, $"0x{d.Status:X2}", d.StatusText,
                        f.Snapshot, f.Extended, () => clock ?? snapClock.Value));
                }
            });

            // Written under a temporary name and renamed when complete, so a read
            // cut short (stopped, port lost) never shows up among earlier reads.
            string part = path + ".part";
            try
            {
                using (var output = new StreamWriter(part) { AutoFlush = true })
                {
                    _cts = new CancellationTokenSource();
                    await _state.Adapter.RunAsync("TabDtc", (scanner, ct) =>
                    {
                        // read first so freeze-frame clocks can be turned into dates; without it the frames stay undated
                        if (deep)
                            try { clock = DtcReader.ReadClock(scanner, _state.Catalog); }
                            catch (Exception ex) when (ex is AdapterException { Problem: AdapterProblem.CommandRefused } or ArgumentOutOfRangeException) { clock = null; }
                        int refused = 0;
                        bool reached = false;
                        // ECUs refused before any was reached are reported once one is: if none is, the adapter is gone
                        // and the read shown until now comes back
                        var held = new List<DtcReader.EcuFaults>();
                        void Report(DtcReader.EcuFaults r, bool unreachable = false) =>
                            ((IProgress<(DtcReader.EcuFaults, bool)>)progress).Report((r, unreachable));
                        foreach (var e in ecus)
                        {
                            ct.ThrowIfCancellationRequested();
                            DtcReader.EcuFaults r;
                            try { r = DtcReader.Read(scanner, e, deep, ct); }
                            // an ECU the adapter cannot address is listed as not reachable; the others are still read
                            catch (Exception ex) when (ex is AdapterException { Problem: AdapterProblem.CommandRefused } or ArgumentOutOfRangeException)
                            {
                                // none of the ECUs so far could be addressed: the adapter is gone, not one ECU
                                if (++refused >= Snapshot.MaxUnreachable && !reached)
                                    throw new AdapterException(AdapterProblem.NoEcuAddressable,
                                        $"The adapter would not address {refused} ECUs in a row. Disconnect, check the adapter and connect again.", count: refused);
                                var none = new DtcReader.EcuFaults(e, new UdsResponse { Kind = ResponseKind.AdapterError }, Array.Empty<DtcReader.Fault>());
                                if (reached) Report(none, true); else held.Add(none);
                                continue;
                            }
                            if (!reached)
                            {
                                reached = true;
                                foreach (var h in held) Report(h, true);
                                held.Clear();
                            }
                            foreach (var f in r.Faults) output.WriteLine(DtcReader.ToJson(e, f, clock));
                            Report(r);
                        }
                        foreach (var h in held) Report(h, true);
                    }, _cts.Token);
                }
                File.Move(part, path);
                _complete = true;
            }
            catch
            {
                File.Delete(part);
                if (!reported)
                {
                    foreach (var row in previous.Rows) Rows.Add(row);
                    (_complete, _shownFile, _readAt, _hasRead) = (previous._complete, previous._shownFile, previous._readAt, previous._hasRead);
                    SetSummary(previous.Text);
                }
                // what was read stays on the tab, said to be incomplete
                else SetSummary(() => Loc.I.Format("DtcIncomplete", active, stored));
                throw;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }

            SetSummary(() => Loc.I.Format("DtcSummary", active, stored, notFaults) + "   " + Loc.I.Format("SavedTo", Path.GetFileName(path)));
            _state.Log(Summary);
            RefreshFiles();
        }

        DtcRow ToRow(EcuDef? ecu, string code, string severity, string status, string statusText,
            string? snapshot, string? extended, Func<(long Value, DateTime Utc)?> clockRef)
        {
            string occurred = "", values = snapshot ?? "";
            var time = _state.Catalog.Platform.FaultTime;
            if (DtcSnapshot.Decode(snapshot, did => KnownLength(ecu, did), time, did => ExpressionOf(ecu, did)) is { } frame)
            {
                var clock = frame.Clock != null ? clockRef() : null;
                occurred = DtcSnapshot.Describe(frame, DtcSnapshot.When(frame.Clock, clock?.Value, clock?.Utc ?? default, time?.TickSeconds ?? 1));
                if (frame.Values.Count > 0) values = string.Join("  ", frame.Values);
            }
            bool notRun = severity is not ("ACTIVE" or "stored");
            return new DtcRow(ecu?.Name ?? "?", code, severity, status, statusText, occurred, values, extended ?? "", notRun, ecu?.Id ?? "");
        }

        static string? ExpressionOf(EcuDef? ecu, int did) =>
            ecu?.Dids.FirstOrDefault(d => d.Did == did)?.Expression;

        static int? KnownLength(EcuDef? ecu, int did) =>
            ecu?.Dids.Concat(ecu.Discovered).FirstOrDefault(d => d.Did == did)?.Length;

        // ---- history ---------------------------------------------------------

        void RefreshFiles()
        {
            Files.Clear();
            try
            {
                foreach (var f in Results.Files(Results.Dtc, "dtc_*.jsonl"))
                    Files.Add(new DtcFile(f, Describe(f)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        static string Describe(string path)
        {
            var when = File.GetLastWriteTime(path).ToString("g", CultureInfo.CurrentCulture);
            int faults = 0;
            try
            {
                faults = File.ReadLines(path).Count(l => l.Contains("\"severity\":\"stored\"") || l.Contains("\"severity\":\"ACTIVE\""));
            }
            catch (IOException)
            {
            }
            return Loc.I.Format("DtcFileItem", when, faults);
        }

        /// <summary>Shows a saved read. Only fault codes were saved, so ECUs that reported none, or did not answer, do not appear.</summary>
        void LoadSelected()
        {
            if (SelectedFile is not { } file) return;
            Rows.Clear();
            _hasRead = false;
            int active = 0, stored = 0, notFaults = 0;
            var snapClock = new Lazy<(long Value, DateTime Utc)?>(ClockFromSnaps);
            try
            {
                foreach (var line in File.ReadLines(file.Path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonObject? o;
                    try { o = JsonNode.Parse(line)?.AsObject(); }
                    catch { continue; }
                    if (o == null) continue;

                    string S(string k) => o[k] is JsonValue jv && jv.TryGetValue(out string? t) ? t : "";   // a wrong type reads as empty
                    var ecu = _state.Catalog.TryEcu(S("ecu"));
                    string severity = S("severity");
                    switch (severity) { case "ACTIVE": active++; break; case "stored": stored++; break; default: notFaults++; break; }

                    (long, DateTime)? saved = o["clock_ref"] is JsonValue v && v.TryGetValue(out long c)
                        && DateTime.TryParse(S("clock_ref_ts"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                        ? (c, at.ToUniversalTime()) : null;

                    string ft = S("failure_type");
                    Rows.Add(ToRow(ecu, S("dtc") + (ft.Length > 2 ? "-" + ft[2..] : ""), severity, S("status"), S("status_text"),
                        S("snapshot"), S("extended"), () => saved ?? snapClock.Value));
                }
            }
            catch (IOException ex)
            {
                // part of a file is not the read it was: show nothing of it
                Rows.Clear();
                SetSummary(() => ex.Message);
                return;
            }
            // a file with no lines is a read that found no faults
            _hasRead = true;
            _complete = true;
            _shownFile = file.Path;
            _readAt = File.GetLastWriteTime(file.Path);
            SetSummary(() => Loc.I.Format("DtcSummary", active, stored, notFaults) + "   " + Loc.I.Format("DtcShowingFile", Path.GetFileName(file.Path)));
        }

        /// <summary>
        /// A clock reference from one of this car's snaps, for reads saved
        /// without one. Any reading of the same car works: the clock runs on at a
        /// steady rate.
        /// </summary>
        (long Value, DateTime Utc)? ClockFromSnaps()
        {
            if (_state.Catalog.Platform.FaultTime?.Clock is not { } clock || clock.Dids is not [int did, ..]) return null;
            string ecuKey = $"\"ecu\":\"{clock.Ecu}\"", didKey = $"\"did\":\"0x{did:X4}\"";
            try
            {
                foreach (var f in Results.Files(Results.Snaps, "snap_*.jsonl"))
                    foreach (var line in File.ReadLines(f))
                    {
                        if (!line.Contains(didKey) || !line.Contains(ecuKey)) continue;
                        var o = JsonNode.Parse(line)!.AsObject();
                        if (o["hex"]?.GetValue<string>() is { Length: > 0 and <= 16 } hex
                            && DateTime.TryParse(o["ts"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                            return (Convert.ToInt64(hex, 16), at.ToUniversalTime());
                    }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
            {
            }
            return null;
        }
    }
}
