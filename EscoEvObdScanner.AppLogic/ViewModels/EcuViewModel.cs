using System.Collections.ObjectModel;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>An ECU that answered: its name if the catalogue knows it, and whether it is new.</summary>
    public sealed record EcuRow(string Name, string Address, string Status);

    /// <summary>Finds ECUs by sweeping a range of request addresses, and adds new ones to the car file on request.</summary>
    public sealed class EcuViewModel : ObservableObject
    {
        readonly AppState _state;
        CancellationTokenSource? _cts;
        string _from = "", _to = "";
        double _progress;
        string _status = "";
        string _carFile = "";
        readonly List<(int Request, int Response)> _found = new();

        public EcuViewModel(AppState state)
        {
            _state = state;
            UseMainRange();
            _carFile = _state.Catalog.Path;
            Discover = new AsyncCommand(DiscoverAsync, () => _state.Adapter.CanRun && Ids().Count > 0);
            Stop = new RelayCommand(() => _cts?.Cancel(), () => _cts != null);
            UseMain = new RelayCommand(UseMainRange);
            UseWide = new RelayCommand(() => { From = Floor; To = "7EF"; });
            // the demo car answers from recorded data: its ECUs are the car file's already
            AddNew = new RelayCommand(DoAddNew, () => !_state.IsDemoCar && !_state.Adapter.IsBusy && NewOnes().Any());
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppState.CatalogPath)) { Found.Clear(); _found.Clear(); SetStatus(null); Progress = 0; }
                if (e.PropertyName != nameof(AppState.Catalog)) return;
                // another car file: its ranges; the same one reloaded (an addition merged) keeps the range typed
                if (!string.Equals(_state.Catalog.Path, _carFile, StringComparison.OrdinalIgnoreCase))
                {
                    _carFile = _state.Catalog.Path;
                    UseMainRange();
                }
                Raise(nameof(HasMain));
                RaiseTexts();
                RefreshFound();
            };
            Loc.I.PropertyChanged += (_, _) => { RaiseTexts(); RefreshFound(); if (_statusText != null) Status = _statusText(); };
        }

        public AsyncCommand Discover { get; }
        public RelayCommand Stop { get; }
        /// <summary>Fills in the car file's main range.</summary>
        public RelayCommand UseMain { get; }
        /// <summary>Fills in everything from the lowest address a sweep may use up to the standard OBD addresses, as the command line's ecus.</summary>
        public RelayCommand UseWide { get; }
        /// <summary>Adds the found ECUs the car file does not have to it.</summary>
        public RelayCommand AddNew { get; }
        public ObservableCollection<EcuRow> Found { get; } = new();

        /// <summary>The car file names a main range.</summary>
        public bool HasMain => _state.Catalog.Platform.Main != null;

        void RaiseTexts()
        {
            Raise(nameof(RangeInfo));
            Raise(nameof(SearchedInfo));
            Raise(nameof(Ranges));
            Raise(nameof(SavedInfo));
            Raise(nameof(MainLabel));
            Raise(nameof(WideLabel));
        }

        string CarFile => Path.GetFileName(_state.Catalog.Path);

        /// <summary>The OBD standard's addresses, the addresses of the car file's ECUs and its main range, and what the wide range covers.</summary>
        public string Ranges
        {
            get
            {
                string known = string.Join(", ", _state.Catalog.Ecus.Select(e => e.Request).Where(r => r > 0).Distinct().Order().Select(r => $"{r:X3}"));
                return _state.Catalog.Platform.Main is var (from, to)
                    ? Loc.I.Format("EcuRanges", $"{from:X3}", $"{to:X3}", CarFile, Floor, known)
                    : Loc.I.Format("EcuRangesNoMain", CarFile, Floor, known);
            }
        }

        /// <summary>What the search saves: nothing by itself; the button adds new ECUs to the car file.</summary>
        public string SavedInfo => Loc.I.Format("EcuNotSaved", CarFile);

        /// <summary>The main range button, with its addresses.</summary>
        public string MainLabel => _state.Catalog.Platform.Main is var (from, to) ? Loc.I.Format("EcuUseMain", $"{from:X3}", $"{to:X3}") : "";

        /// <summary>The wide range button, with its addresses.</summary>
        public string WideLabel => Loc.I.Format("EcuUseWide", Floor, "7EF");

        /// <summary>The lowest address a sweep may use, as hex for the texts.</summary>
        public string Floor => $"{_state.Catalog.Platform.ProbeFrom:X3}";

        public string From { get => _from; set { if (Set(ref _from, value)) { Raise(nameof(RangeInfo)); Raise(nameof(SearchedInfo)); } } }
        public string To { get => _to; set { if (Set(ref _to, value)) { Raise(nameof(RangeInfo)); Raise(nameof(SearchedInfo)); } } }

        /// <summary>
        /// What the car file says is searched already of the range typed: how many addresses, and each
        /// searched range that overlaps it with its notes. Empty without a valid range.
        /// </summary>
        public string SearchedInfo
        {
            get
            {
                int from = Catalog.ParseHexOrDefault(From), to = Catalog.ParseHexOrDefault(To);
                if (from < 0 || to < from || to > 0x7FF) return "";
                var (covered, overlapping) = ScannedRange.Coverage(_state.Catalog.EcuSearch, from, to);
                int total = to - from + 1;
                string head = covered == total ? Loc.I["EcuSearchedAll"]
                    : overlapping.Count == 0 ? Loc.I["EcuSearchedNone"]
                    : Loc.I.Format("EcuSearchedPart", covered, total);
                return string.Join(Environment.NewLine, overlapping.Select(s => $"  {s.RangeText}  {s.Notes}".TrimEnd()).Prepend(head));
            }
        }
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }

        /// <summary>A result line kept as how to word it, so that a language switch words it again; progress lines are not kept.</summary>
        Func<string>? _statusText;

        void SetStatus(Func<string>? text)
        {
            _statusText = text;
            Status = text?.Invoke() ?? "";
        }


        /// <summary>How many addresses the chosen range holds and roughly how long the sweep takes (about a second each on an ELM327: the probe waits for an answer that mostly never comes), or how to fix the range.</summary>
        public string RangeInfo
        {
            get
            {
                int n = Ids().Count;
                return n == 0 ? Loc.I.Format("BadEcuRange", Floor) : Loc.I.Format("EcuRangeInfo", n, Durations.About(n));
            }
        }

        /// <summary>The car file's main range, or the wide range if it names none.</summary>
        void UseMainRange()
        {
            if (_state.Catalog.Platform.Main is var (from, to)) { From = $"{from:X3}"; To = $"{to:X3}"; }
            else { From = Floor; To = "7EF"; }
        }

        async Task DiscoverAsync()
        {
            var ids = Ids();
            if (ids.Count == 0) throw new InvalidOperationException(Loc.I.Format("BadEcuRange", Floor));

            Found.Clear();
            _found.Clear();
            _statusText = null;
            _cts = new CancellationTokenSource();
            int done = 0;
            var probing = new Progress<int>(id =>
            {
                Progress = 100.0 * ++done / ids.Count;
                Status = Loc.I.Format("Probing", id);
            });
            var found = new Progress<(int Req, int Resp)>(e => { _found.Add((e.Req, e.Resp)); Found.Add(Row(e.Req, e.Resp)); });

            // never probe under an ID a known ECU answers on
            var skip = _state.Catalog.Ecus.Select(e => e.Response).Where(r => r > 0).ToList();

            try
            {
                bool cut = false;
                var list = await _state.Adapter.RunAsync("Discover", (scanner, ct) =>
                {
                    var l = scanner.DiscoverEcus(ids[0], ids[^1],
                        id => ((IProgress<int>)probing).Report(id),
                        (req, resp) => ((IProgress<(int, int)>)found).Report((req, resp)), ct, skip);
                    // stopped here or by Disconnect, which cancels the adapter's own token
                    cut = ct.IsCancellationRequested;
                    return l;
                }, _cts.Token);

                int count = list.Count;
                SetStatus(() => Loc.I.Format(cut ? "FoundEcusStopped" : "FoundEcus", count));
                _state.Log(Status + "  " + string.Join(", ", list.Select(e => $"0x{e.Request:X3}->0x{e.Response:X3}")));
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        /// <summary>Found ECUs whose request and reply addresses the car file does not use.</summary>
        IEnumerable<(int Request, int Response)> NewOnes() =>
            _found.Where(f => _state.Catalog.Ecus.All(e => e.Request != f.Request && e.Response != f.Response));

        void DoAddNew()
        {
            int added = CatalogMerge.AddEcus(_state.Catalog, NewOnes().ToList(), DateTime.Now);
            _state.ReloadCatalog();
            SetStatus(() => Loc.I.Format("EcuAdded", added));
            _state.Log(Status);
        }

        /// <summary>The found list again, in the current language and against the current car file.</summary>
        void RefreshFound()
        {
            Found.Clear();
            foreach (var (req, resp) in _found) Found.Add(Row(req, resp));
        }

        /// <summary>The IDs to probe; empty if the range is invalid.</summary>
        List<int> Ids()
        {
            int from = Catalog.ParseHexOrDefault(From);
            int to = Catalog.ParseHexOrDefault(To);
            return from < _state.Catalog.Platform.ProbeFrom || to < from || to > 0x7FF ? new() : Enumerable.Range(from, to - from + 1).ToList();
        }

        /// <summary>Known by its request address; one answering on another ECU's reply address is that ECU again, not new.</summary>
        EcuRow Row(int req, int resp)
        {
            var known = _state.Catalog.Ecus.FirstOrDefault(x => x.Request == req);
            var sameReply = known == null ? _state.Catalog.Ecus.FirstOrDefault(x => x.Response == resp) : null;
            return new EcuRow(
                (known ?? sameReply)?.Name ?? Loc.I["EcuUnknown"],
                $"0x{req:X3} → 0x{resp:X3}",
                Loc.I[known != null ? "EcuKnown" : sameReply != null ? "EcuSameReply" : "EcuNew"]);
        }
    }
}
