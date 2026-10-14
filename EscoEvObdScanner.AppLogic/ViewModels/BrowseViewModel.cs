using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>One ECU in the list, with how much of it is worked out and whether it answered; checked ECUs are listed and read.</summary>
    public sealed class BrowseEcu : ObservableObject
    {
        bool _isChecked = true;
        bool? _answered;

        public BrowseEcu(EcuDef def) => Def = def;

        public EcuDef Def { get; }
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

        /// <summary>Whether it answered the last check; null before any.</summary>
        public bool? Answered { get => _answered; set { if (Set(ref _answered, value)) Raise(nameof(Mark)); } }

        /// <summary>✓ answered, ✗ did not, empty before a check.</summary>
        public string Mark => Answered switch { true => "✓", false => "✗", null => "" };
        public string Title => Def.Name;
        public string Counts => Def.RequestHex + "  ·  " + Loc.I.Format("BrowseCounts", Def.Dids.Count, Def.Discovered.Count);

        /// <summary>The car file says it has not answered this car.</summary>
        public bool NeverAnswered => Def.Answered == false;

        /// <summary>The first of the ECUs that have not answered, which carries their group's heading.</summary>
        public bool FirstNeverAnswered { get; init; }

        /// <summary>After a language switch.</summary>
        public void Refresh() { Raise(nameof(Title)); Raise(nameof(Counts)); }
    }

    /// <summary>One identifier of a checked ECU: catalogued or only discovered, plus the last value read.</summary>
    public sealed class BrowseRow : ObservableObject
    {
        string _value = "", _raw = "";

        public BrowseRow(EcuDef ecu, DidDef def, bool known)
        {
            Ecu = ecu;
            Def = def;
            Known = known;
            _raw = def.Samples?.LastOrDefault()?.Hex ?? "";
        }

        public EcuDef Ecu { get; }
        public DidDef Def { get; }
        public bool Known { get; }

        public string EcuName => Ecu.Name;

        public string Did => Def.DidHex.Replace("0x", "");
        public string Label => Def.Name;
        public string Unit => Def.Unit ?? "";
        public string Source => Def.Source ?? "";

        public string Status => StatusText(Def.Confidence, Known);

        /// <summary>The catalogue confidence in words: confirmed, candidate, reported or unknown.</summary>
        public static string StatusText(string? confidence, bool known) => Loc.I[confidence switch
        {
            "confirmed" => "StatusConfirmed",
            "unverified" => "StatusCandidate",
            "reported" => "StatusReported",
            _ => known ? "StatusCandidate" : "StatusUnknown"
        }];

        /// <summary>Decoded value after a read; empty until then.</summary>
        public string Value { get => _value; set => Set(ref _value, value); }

        /// <summary>Raw reply: the last read, or the last sample from a scan before any read.</summary>
        public string Raw { get => _raw; set { if (Set(ref _raw, value)) Raise(nameof(Readable)); } }

        /// <summary>The raw reply as plain numbers or text, to help guess what an unknown identifier is.</summary>
        public string Readable => Bytes.Readable(Bytes.FromHex(Raw));

        /// <summary>After a language switch.</summary>
        public void Refresh()
        {
            Raise(nameof(EcuName));
            Raise(nameof(Label));
            Raise(nameof(Status));
            Raise(nameof(Details));
        }

        public string Details
        {
            get
            {
                var lines = new List<string>();
                if (!string.IsNullOrEmpty(Def.Expression)) lines.Add(Loc.I["Expression"] + ": " + Def.Expression);
                if (!string.IsNullOrEmpty(Def.Notes)) lines.Add(Def.Notes);
                if (Def.Samples is { Count: > 0 } samples)
                {
                    lines.Add(Loc.I["Samples"] + ":");
                    lines.AddRange(samples.Select(s => $"  {Short(s.Time)}  {s.Hex}"));
                }
                return string.Join(Environment.NewLine, lines);

                static string Short(string? ts) =>
                    DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                        ? t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : ts ?? "";
            }
        }
    }

    /// <summary>
    /// The car file's ECUs and the identifiers of the checked ones: the worked-out
    /// ones and those a scan found whose meaning is still open, filtered and
    /// searched. Reads the values of what is shown, ECU by ECU, and checks whether
    /// the checked ECUs answer, on request.
    /// </summary>
    public sealed class BrowseViewModel : ObservableObject
    {
        public enum Show { All, Known, Unknown }

        readonly AppState _state;
        CancellationTokenSource? _cts;
        BrowseRow? _selectedRow;
        string _search = "";
        Show _filter = Show.Known;
        string _status = "";

        public BrowseViewModel(AppState state)
        {
            _state = state;
            RowsView = AppHost.I.RowView(Rows, o => o is BrowseRow r && Matches(r));
            Read = new AsyncCommand(ReadAsync, () => _state.Adapter.CanRun && Shown().Count > 0);
            Check = new AsyncCommand(CheckAsync, () => _state.Adapter.CanRun && Ecus.Any(e => e.IsChecked));
            Stop = new RelayCommand(() => _cts?.Cancel(), () => _cts != null);
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppState.Catalog)) { Reload(); Raise(nameof(Source)); }
                // another car (or the demo car on the same file): the marks of the last one's check do not belong to it
                if (e.PropertyName is nameof(AppState.CatalogPath) or nameof(AppState.IsDemoCar))
                    foreach (var ecu in Ecus) ecu.Answered = null;
            };
            // texts change in place: reloading would drop the values just read
            Loc.I.PropertyChanged += (_, _) =>
            {
                if (_statusText != null) Status = _statusText();
                Raise(nameof(Hint));
                Raise(nameof(Source));
                foreach (var e in Ecus) e.Refresh();
                foreach (var r in Rows) r.Refresh();
            };
            Reload();
        }

        public AsyncCommand Read { get; }

        /// <summary>No read or check running, so the ECU choice can change.</summary>
        public bool IsIdle => _cts == null;
        /// <summary>Asks every checked ECU whether it answers.</summary>
        public AsyncCommand Check { get; }
        public RelayCommand Stop { get; }

        public ObservableCollection<BrowseEcu> Ecus { get; } = new();
        public ObservableCollection<BrowseRow> Rows { get; } = new();
        public IRowView RowsView { get; }
        /// <summary>The filter as three options, for radio buttons.</summary>
        public bool ShowKnown { get => Filter == Show.Known; set { if (value) Filter = Show.Known; } }
        public bool ShowAll { get => Filter == Show.All; set { if (value) Filter = Show.All; } }
        public bool ShowUnknown { get => Filter == Show.Unknown; set { if (value) Filter = Show.Unknown; } }

        /// <summary>Every ECU checked; setting it checks or clears them all.</summary>
        public bool AllChecked
        {
            get => Ecus.Count > 0 && Ecus.All(e => e.IsChecked);
            set
            {
                _bulk = true;
                foreach (var e in Ecus) e.IsChecked = value;
                _bulk = false;
                Fill();
            }
        }

        /// <summary>Checks only the ECU with this id; for the self-test.</summary>
        internal void CheckOnly(string ecuId)
        {
            _bulk = true;
            foreach (var e in Ecus) e.IsChecked = string.Equals(e.Def.Id, ecuId, StringComparison.OrdinalIgnoreCase);
            _bulk = false;
            Fill();
        }

        bool _bulk;

        /// <summary>Which identifiers a read covers, from how many ECUs, and roughly how long it takes.</summary>
        public string Hint
        {
            get
            {
                var shown = Shown();
                int ecus = shown.Select(r => r.Ecu.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                string key = Filter switch { Show.Known => "BrowseHintKnown", Show.Unknown => "BrowseHintUnknown", _ => "BrowseHintAll" };
                int hidden = Rows.Count(r => MatchesFilter(r) && !r.Def.ReadWith(ShowRare, ShowRefused));
                return Loc.I.Format(key, shown.Count, ecus, Durations.About(shown.Count * Durations.SecondsPerRead))
                    + (hidden > 0 ? " " + Loc.I.Format("BrowseHidden", hidden) : "");
            }
        }

        /// <summary>Also lists (and reads) values that seldom change.</summary>
        public bool ShowRare
        {
            get => _showRare;
            set { if (Set(ref _showRare, value)) { RowsView.Refresh(); Raise(nameof(Hint)); } }
        }

        /// <summary>Also lists (and reads) identifiers the ECU refused when scanned.</summary>
        public bool ShowRefused
        {
            get => _showRefused;
            set { if (Set(ref _showRefused, value)) { RowsView.Refresh(); Raise(nameof(Hint)); } }
        }

        bool _showRare, _showRefused;

        /// <summary>Where the list comes from, and that this tab only reads.</summary>
        public string Source => Loc.I.Format("BrowseSource", Path.GetFileName(_state.Catalog.Path), _state.Catalog.Platform.Name);

        public BrowseRow? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }

        public string Search
        {
            get => _search;
            set { if (Set(ref _search, value)) { RowsView.Refresh(); Raise(nameof(Hint)); } }
        }

        public Show Filter
        {
            get => _filter;
            set
            {
                if (!Set(ref _filter, value)) return;
                RowsView.Refresh();
                Raise(nameof(Hint));
                Raise(nameof(ShowKnown));
                Raise(nameof(ShowAll));
                Raise(nameof(ShowUnknown));
            }
        }

        public string Status { get => _status; private set => Set(ref _status, value); }

        /// <summary>A result line kept as how to word it, so that a language switch words it again; progress lines are not kept.</summary>
        Func<string>? _statusText;

        void SetStatus(Func<string>? text)
        {
            _statusText = text;
            Status = text?.Invoke() ?? "";
        }


        void Reload()
        {
            // the choice made is kept; an ECU new to the list starts checked, unless the car file says it has not answered
            var chosen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Ecus) chosen.TryAdd(e.Def.Id ?? "", e.IsChecked);
            var answered = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Ecus) answered.TryAdd(e.Def.Id ?? "", e.Answered);
            foreach (var e in Ecus) e.PropertyChanged -= OnEcuChanged;
            Ecus.Clear();
            // the ECUs that answer first, those that have not as a group of their own below them, each in the car file's order
            bool first = true;
            foreach (var def in _state.Catalog.Ecus.OrderBy(d => d.Answered == false))
            {
                bool silent = def.Answered == false;
                var e = new BrowseEcu(def)
                {
                    IsChecked = chosen.TryGetValue(def.Id ?? "", out var c) ? c : !silent,
                    Answered = answered.GetValueOrDefault(def.Id ?? ""),
                    FirstNeverAnswered = silent && first
                };
                if (silent) first = false;
                e.PropertyChanged += OnEcuChanged;
                Ecus.Add(e);
            }
            Fill();
        }

        void OnEcuChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BrowseEcu.IsChecked) && !_bulk) Fill();
        }

        /// <summary>The checked ECUs' identifiers, ECU by ECU in the car file's order, each ECU's by number.</summary>
        void Fill()
        {
            Rows.Clear();
            SelectedRow = null;
            SetStatus(null);
            foreach (var ecu in Ecus.Where(e => e.IsChecked).Select(e => e.Def))
                foreach (var r in ecu.Dids.Select(d => new BrowseRow(ecu, d, true))
                             .Concat(ecu.Discovered.Select(d => new BrowseRow(ecu, d, false)))
                             .OrderBy(r => r.Def.Did))
                    Rows.Add(r);
            Raise(nameof(AllChecked));
            Raise(nameof(Hint));
        }

        /// <summary>The rows on screen, in the order shown.</summary>
        List<BrowseRow> Shown() => RowsView.Cast<BrowseRow>().ToList();

        bool Matches(BrowseRow r) => r.Def.ReadWith(ShowRare, ShowRefused) && MatchesFilter(r);

        /// <summary>The filter and the search, without the choice of rare and refused identifiers.</summary>
        bool MatchesFilter(BrowseRow r)
        {
            if (Filter == Show.Known && !r.Known) return false;
            if (Filter == Show.Unknown && r.Known) return false;
            if (string.IsNullOrWhiteSpace(Search)) return true;
            string s = Search.Trim();
            return r.Did.Contains(s, StringComparison.OrdinalIgnoreCase)
                || r.Label.Contains(s, StringComparison.OrdinalIgnoreCase)
                || (r.Def.EnglishName ?? "").Contains(s, StringComparison.OrdinalIgnoreCase)
                || (r.Def.Notes ?? "").Contains(s, StringComparison.OrdinalIgnoreCase);
        }

        async Task ReadAsync()
        {
            // read what is on screen, ECU by ECU in the order they first appear, each ECU's rows in the order shown
            var rows = Shown();
            if (rows.Count == 0) return;
            var groups = rows.GroupBy(r => r.Ecu).ToList();
            var byDef = rows.GroupBy(r => (r.Ecu.Id, r.Def.Did)).ToDictionary(g => g.Key, g => g.ToList());
            int done = 0;
            _statusText = null;
            var progress = new Progress<(EcuDef Ecu, KnownValues.Reading Reading)>(x =>
            {
                if (byDef.TryGetValue((x.Ecu.Id, x.Reading.Def.Did), out var same))
                    foreach (var row in same) Apply(row, x.Reading);
                Status = Loc.I.Format("BrowseProgress", ++done, rows.Count);
            });

            _cts = new CancellationTokenSource();
            Raise(nameof(IsIdle));
            try
            {
                var silent = await _state.Adapter.RunAsync("TabEcus", (scanner, ct) =>
                {
                    var stopped = new List<string>();
                    var unreachable = new List<string>();
                    int refused = 0;
                    bool reached = false;
                    foreach (var g in groups)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var (_, quiet) = KnownValues.ReadEcu(scanner, g.Key, g.Select(r => r.Def),
                                reading => ((IProgress<(EcuDef, KnownValues.Reading)>)progress).Report((g.Key, reading)), ct);
                            reached = true;
                            if (quiet) stopped.Add(g.Key.Name);
                        }
                        // an ECU the adapter cannot address is skipped, the others are still read
                        catch (Exception ex) when (ex is AdapterException { Problem: AdapterProblem.CommandRefused } or ArgumentOutOfRangeException)
                        {
                            unreachable.Add(g.Key.Name);
                            // none of the ECUs so far could be addressed: the adapter is gone, not one ECU
                            if (++refused >= Snapshot.MaxUnreachable && !reached)
                                throw new AdapterException(AdapterProblem.NoEcuAddressable,
                                    $"The adapter would not address {refused} ECUs in a row. Disconnect, check the adapter and connect again.", count: refused);
                        }
                    }
                    return (stopped, unreachable);
                }, _cts.Token);
                if (silent.stopped.Count + silent.unreachable.Count > 0)
                    SetStatus(() => string.Join(" ", new[]
                    {
                        silent.stopped.Count > 0 ? Loc.I.Format("BrowseSilent", string.Join(", ", silent.stopped)) : "",
                        silent.unreachable.Count > 0 ? Loc.I.Format("BrowseUnreachable", string.Join(", ", silent.unreachable)) : ""
                    }.Where(t => t.Length > 0)));
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                Raise(nameof(IsIdle));
            }
        }

        async Task CheckAsync()
        {
            var chosen = Ecus.Where(e => e.IsChecked).ToList();
            foreach (var e in chosen) e.Answered = null;
            // never probe under an ID a known ECU answers on, nor the OBD broadcast and reply IDs (Scanner.ProbeKnown skips those)
            var skip = _state.Catalog.Ecus.Select(e => e.Response).Where(r => r > 0).ToList();
            var byRequest = chosen.Where(e => e.Def.Request is >= 0x100 and <= 0x7FF && e.Def.Request != 0x7DF
                                              && e.Def.Request is not (>= 0x7E8 and <= 0x7EF) && !skip.Contains(e.Def.Request))
                .GroupBy(e => e.Def.Request).ToDictionary(g => g.Key, g => g.ToList());
            int done = 0, total = byRequest.Count;
            var asked = new HashSet<int>();
            _statusText = null;
            var probing = new Progress<int>(id => { asked.Add(id); Status = Loc.I.Format("BrowseChecking", ++done, total); });
            var found = new Progress<int>(req => { foreach (var e in byRequest.GetValueOrDefault(req) ?? []) e.Answered = true; });

            _cts = new CancellationTokenSource();
            Raise(nameof(IsIdle));
            try
            {
                bool cut = false;
                var list = await _state.Adapter.RunAsync("TabEcus", (scanner, ct) =>
                {
                    var l = scanner.ProbeKnown(byRequest.Keys,
                        id => ((IProgress<int>)probing).Report(id),
                        (req, _) => ((IProgress<int>)found).Report(req), ct, skip);
                    // stopped here or by Disconnect, which cancels the adapter's own token
                    cut = ct.IsCancellationRequested;
                    return l;
                }, _cts.Token);
                // the probe goes up the IDs and from then on skips an ID another ECU answered on, though it reports it
                // as probed: so a reply ID above its request was not asked, one below it was asked before
                asked.ExceptWith(list.Where(x => x.Response > x.Request).Select(x => x.Response));
                // only an ECU that was asked is marked as silent; one not asked (stopped, or an address that cannot be probed) stays unmarked
                foreach (var e in chosen.Where(e => e.Answered == null && asked.Contains(e.Def.Request))) e.Answered = false;
                int marked = chosen.Count(e => e.Answered != null);
                int yes = chosen.Count(e => e.Answered == true);
                SetStatus(() => Loc.I.Format(cut ? "BrowseCheckStopped" : "BrowseChecked", yes, marked));
                _state.Log(Status + "  " + string.Join(", ", list.Select(e => $"0x{e.Request:X3}->0x{e.Response:X3}")));
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                Raise(nameof(IsIdle));
            }
        }

        static void Apply(BrowseRow row, KnownValues.Reading r)
        {
            row.Raw = r.Response.Kind == ResponseKind.Positive ? Bytes.Hex(r.Response.Data) : "";
            row.Value = r.Text != null ? $"\"{r.Text}\""
                : r.Value is double v ? v.ToString("0.###", CultureInfo.CurrentCulture)
                : r.Response.Kind == ResponseKind.Positive ? ""
                : r.Problem;
        }
    }
}
