using System.Collections.ObjectModel;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    public sealed record ScanRow(string Did, string Name, string Kind, int Length, string Hex, string Ascii);

    /// <summary>A range from the car file's scan blocks, shown by its identifiers and rough duration rather than its name, which fits only some ECUs.</summary>
    public sealed record BlockChoice(BlockDef Def)
    {
        public string Text => Loc.I.Format("ScanBlockText", $"{Def.From:X4}", $"{Def.To:X4}", Def.To - Def.From + 1,
            Durations.About((Def.To - Def.From + 1) * Durations.SecondsPerRead));
    }

    public sealed class ScanViewModel : ObservableObject
    {
        readonly AppState _state;
        CancellationTokenSource? _cts;
        EcuDef? _ecu;
        BlockChoice? _block;
        string _from = "", _to = "";
        string _carId = "";
        double _progress;
        string _status = "";
        string? _lastFile;

        public ScanViewModel(AppState state)
        {
            _state = state;
            Start = new AsyncCommand(RunAsync, () => _state.Adapter.CanRun && Ecu != null);
            Stop = new RelayCommand(() => _cts?.Cancel(), () => _cts != null);
            // the demo car answers from recorded data: merging it would only duplicate the real car's samples
            Merge = new RelayCommand(DoMerge, () => LastFile != null && !_state.IsDemoCar && !_state.Adapter.IsBusy && File.Exists(LastFile));
            // this car's scans, where every scan is saved
            OpenFolder = new RelayCommand(() => ConnectionViewModel.OpenFolder(Results.Folder(Results.Scans)));
            // the demo car and the real car share a file: either change is another car
            state.PropertyChanged += (_, e) =>
            {
                // a car chosen (even the same one again, e.g. an import) drops the last results; a reload after merge keeps them
                if (e.PropertyName == nameof(AppState.CatalogPath)) _carId = "";
                if (e.PropertyName is nameof(AppState.Catalog) or nameof(AppState.CatalogPath) or nameof(AppState.IsDemoCar)) LoadCatalog();
                if (e.PropertyName == nameof(AppState.Catalog)) { Raise(nameof(SavedInfo)); Raise(nameof(ScannedInfo)); }
            };
            LoadCatalog();
            // the ECU and block names are per language: fill the lists again, keeping the choice
            Loc.I.PropertyChanged += (_, _) => { LoadCatalog(); Raise(nameof(SavedInfo)); Raise(nameof(ScannedInfo)); if (_statusText != null) Status = _statusText(); };
        }

        public AsyncCommand Start { get; }
        public RelayCommand Stop { get; }
        public RelayCommand Merge { get; }
        public RelayCommand OpenFolder { get; }

        public ObservableCollection<EcuDef> Ecus { get; } = new();
        public ObservableCollection<BlockChoice> Blocks { get; } = new();
        public ObservableCollection<ScanRow> Rows { get; } = new();

        public EcuDef? Ecu { get => _ecu; set { if (Set(ref _ecu, value)) Raise(nameof(ScannedInfo)); } }
        public BlockChoice? Block
        {
            get => _block;
            set { if (Set(ref _block, value) && value?.Def is { IsScannable: true } b) { From = b.From.ToString("X4"); To = b.To.ToString("X4"); } }
        }
        public string From { get => _from; set { if (Set(ref _from, value)) Raise(nameof(ScannedInfo)); } }
        public string To { get => _to; set { if (Set(ref _to, value)) Raise(nameof(ScannedInfo)); } }

        /// <summary>
        /// What the car file says is scanned already of the chosen ECU and range:
        /// how much in full, and each scanned range that overlaps it with its notes.
        /// Empty without a valid range.
        /// </summary>
        public string ScannedInfo
        {
            get
            {
                int from = Catalog.ParseHexOrDefault(From), to = Catalog.ParseHexOrDefault(To);
                if (Ecu == null || from < 0 || to < from || to > 0xFFFF) return "";
                var (covered, overlapping) = ScannedRange.Coverage(Ecu.Scanned, from, to);
                int total = to - from + 1;
                string head = covered == total ? Loc.I["ScanCoveredAll"]
                    : overlapping.Count == 0 ? Loc.I["ScanCoveredNone"]
                    : Loc.I.Format("ScanCoveredPart", covered, total);
                return string.Join(Environment.NewLine, overlapping
                    .Select(s => $"  {s.RangeText}  " + (s.IsFull ? "" : Loc.I.Format("ScanSample", s.Step!.Value) + " ") + s.Notes)
                    .Prepend(head));
            }
        }
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string? LastFile { get => _lastFile; private set => Set(ref _lastFile, value); }

        /// <summary>A result line kept as how to word it, so that a language switch words it again; progress lines are not kept.</summary>
        Func<string>? _statusText;

        void SetStatus(Func<string>? text)
        {
            _statusText = text;
            Status = text?.Invoke() ?? "";
        }


        /// <summary>Where a scan is saved, and that findings go into the car file only by the button.</summary>
        public string SavedInfo => Loc.I.Format("ScanSaved", Path.GetFileName(_state.Catalog.Path));

        void LoadCatalog()
        {
            // the range typed (or resumed from) is kept for the same car; another car starts from the block of the same id, else its first block
            bool sameCar = _state.CarId == _carId;
            // another car: results of the last one must not be merged into this one's file
            if (!sameCar)
            {
                _carId = _state.CarId;
                Rows.Clear();
                LastFile = null;
                SetStatus(null);
                Progress = 0;
            }

            string? ecuId = Ecu?.Id, blockId = Block?.Def.Id;
            string from = From, to = To;
            Ecus.Clear();
            foreach (var e in _state.Catalog.Ecus) Ecus.Add(e);
            Blocks.Clear();
            foreach (var b in _state.Catalog.Blocks.Where(b => b.IsScannable).OrderBy(b => b.Order)) Blocks.Add(new BlockChoice(b));
            Ecu = Ecus.FirstOrDefault(e => e.Id == ecuId) ?? Ecus.FirstOrDefault();
            Block = Blocks.FirstOrDefault(b => b.Def.Id == blockId) ?? Blocks.FirstOrDefault();
            if (sameCar && from.Length > 0) { From = from; To = to; }
        }

        async Task RunAsync()
        {
            var ecu = Ecu!;
            int from = Catalog.ParseHexOrDefault(From), to = Catalog.ParseHexOrDefault(To);
            if (from < 0 || to < from || to > 0xFFFF) throw new InvalidOperationException(Loc.I["BadRange"]);

            Rows.Clear();
            Progress = 0;
            var path = Results.NewFile(Results.Scans, $"scan_{ecu.Id}_{from:X4}-{to:X4}");
            _cts = new CancellationTokenSource();
            var started = DateTime.UtcNow;
            _statusText = null;
            int hits = 0;

            var hitProgress = new Progress<DidScan.Hit>(h =>
            {
                hits++;
                Rows.Add(new ScanRow($"0x{h.Did:X4}", h.Known?.Name ?? "", h.Response.KindLabel,
                    h.Response.Data.Length, Bytes.Hex(h.Response.Data), Bytes.Ascii(h.Response.Data)));
            });
            var didProgress = new Progress<int>(did =>
            {
                double done = did - from + 1, total = to - from + 1;
                Progress = done / total * 100;
                var elapsed = DateTime.UtcNow - started;
                var eta = TimeSpan.FromTicks((long)(elapsed.Ticks / done * (total - done)));
                Status = $"0x{did:X4}   {Loc.I["Hits"]}: {hits}   {Loc.I["Eta"]}: {eta:hh\\:mm\\:ss}";
            });

            try
            {
                DidScan.Result result;
                using (var output = new StreamWriter(path) { AutoFlush = true })
                {
                    result = await _state.Adapter.RunAsync($"TabDiscovery {ecu.Id}", (scanner, ct) =>
                        DidScan.Run(scanner, _state.Catalog, ecu, from, to, output,
                            h => ((IProgress<DidScan.Hit>)hitProgress).Report(h),
                            // throttle UI updates to every 16th identifier
                            d => { if ((d & 0x0F) == 0 || d == to) ((IProgress<int>)didProgress).Report(d); },
                            ct), _cts.Token);
                }

                LastFile = path;
                SetStatus(() => result.Outcome switch
                {
                    DidScan.Outcome.ServiceRefused => Loc.I.Format("ScanRefused", UdsResponse.NrcName(result.RefusalNrc)),
                    DidScan.Outcome.Silent => Loc.I.Format("ScanSilent", DidScan.MaxSilent, result.ResumeFrom),
                    DidScan.Outcome.Cancelled => Loc.I.Format("ScanCancelled", result.LastDid),
                    DidScan.Outcome.AdapterRefused => Loc.I["ScanAdapterRefused"],
                    _ => Loc.I.Format("ScanDone", result.Hits)
                } + "   " + Loc.I.Format("SavedTo", Path.GetFileName(path)));
                if (result.Outcome == DidScan.Outcome.Silent) From = result.ResumeFrom.ToString("X4");
                if (result.Outcome == DidScan.Outcome.Completed) Progress = 100;
                _state.Log(Status);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        void DoMerge()
        {
            var r = CatalogMerge.Merge(_state.Catalog, new[] { LastFile! });
            _state.ReloadCatalog();
            SetStatus(() => Loc.I.Format("MergeResult", r.Added, r.Updated, r.Skipped));
            _state.Log(Status);
        }
    }
}
