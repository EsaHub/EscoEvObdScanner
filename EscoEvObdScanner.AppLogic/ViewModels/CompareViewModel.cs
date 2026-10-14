using System.Collections.ObjectModel;
using System.Globalization;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    public sealed record SnapFile(string Path, string Note, DateTime Time)
    {
        public override string ToString() => $"{Time.ToString("g", CultureInfo.CurrentCulture)}   {Note}";
    }

    public sealed record ChangeRow(string Ecu, string Did, string Name, string Change, string Delta, string ValueA, string ValueB);

    public sealed class CompareViewModel : ObservableObject
    {
        readonly AppState _state;
        string _note = "";
        SnapFile? _a, _b;
        string _status = "";
        double _progress;
        CancellationTokenSource? _cts;
        (List<Snapshot.Change> Changes, int Same)? _compared;

        public CompareViewModel(AppState state)
        {
            _state = state;
            TakeSnap = new AsyncCommand(TakeSnapAsync, () => _state.Adapter.CanRun);
            Stop = new RelayCommand(() => _cts?.Cancel(), () => _cts != null);
            RefreshFiles = new RelayCommand(LoadFiles);
            Compare = new RelayCommand(DoCompare, () => A != null && B != null && A != B);
            // this car's snaps
            OpenFolder = new RelayCommand(() => ConnectionViewModel.OpenFolder(Results.Folder(Results.Snaps)));
            state.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppState.Catalog)) Raise(nameof(SnapInfo));
                if (e.PropertyName != nameof(AppState.CatalogPath)) return;
                Rows.Clear();
                _compared = null;
                SetStatus(null);
                LoadFiles();            // the new car's snaps
            };
            // the hint, the list's dates, the comparison and the result line in the new language
            Loc.I.PropertyChanged += (_, _) =>
            {
                Raise(nameof(SnapInfo));
                // the dates in the new format: the same files again, not read again
                var (files, a, b) = (Files.ToList(), A, B);
                Files.Clear();
                foreach (var f in files) Files.Add(f);
                (A, B) = (a, b);
                if (_compared is { } c) FillRows(c.Changes);
                if (_statusText != null) Status = _statusText();
            };
            LoadFiles();
        }

        /// <summary>What a snap reads, from how many ECUs, roughly how long it takes, what it leaves out, and what comparing two shows.</summary>
        public string SnapInfo
        {
            get
            {
                var targets = Snapshot.Targets(_state.Catalog, rare: ReadRare, refused: ReadRefused);
                var all = Snapshot.Targets(_state.Catalog, rare: true, refused: true);
                int ecus = targets.Select(d => d.Ecu).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return Loc.I.Format("CompareHint", targets.Count, ecus, Durations.About(targets.Count * Durations.SecondsPerRead),
                    all.Count(d => d.Rare), all.Count(d => d.Refused && !d.Rare));
            }
        }

        /// <summary>The snap also reads values that seldom change.</summary>
        public bool ReadRare { get => _readRare; set { if (Set(ref _readRare, value)) Raise(nameof(SnapInfo)); } }

        /// <summary>The snap also reads identifiers the ECU refused when scanned.</summary>
        public bool ReadRefused { get => _readRefused; set { if (Set(ref _readRefused, value)) Raise(nameof(SnapInfo)); } }

        bool _readRare, _readRefused;

        public AsyncCommand TakeSnap { get; }
        public RelayCommand Stop { get; }
        public RelayCommand RefreshFiles { get; }
        public RelayCommand Compare { get; }
        public RelayCommand OpenFolder { get; }

        public ObservableCollection<SnapFile> Files { get; } = new();
        public ObservableCollection<ChangeRow> Rows { get; } = new();

        public string Note { get => _note; set => Set(ref _note, value); }
        public SnapFile? A { get => _a; set => Set(ref _a, value); }
        public SnapFile? B { get => _b; set => Set(ref _b, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public double Progress { get => _progress; private set => Set(ref _progress, value); }

        /// <summary>A result line kept as how to word it, so that a language switch words it again; progress lines are not kept.</summary>
        Func<string>? _statusText;

        void SetStatus(Func<string>? text)
        {
            _statusText = text;
            Status = text?.Invoke() ?? "";
        }


        /// <summary>The snaps of this car, newest first; the newest two are chosen.</summary>
        void LoadFiles()
        {
            Files.Clear();

            foreach (var path in Results.Files(Results.Snaps, "snap_*.jsonl"))
            {
                string note = "";
                try { note = Snapshot.Load(path).Note; } catch { /* unreadable file: list it without a note */ }
                Files.Add(new SnapFile(path, note, File.GetLastWriteTime(path)));
            }

            // newest as "after", the one before it as "before"
            B = Files.ElementAtOrDefault(0);
            A = Files.ElementAtOrDefault(1);
        }

        async Task TakeSnapAsync()
        {
            var targets = Snapshot.Targets(_state.Catalog, rare: ReadRare, refused: ReadRefused);
            var path = Results.NewFile(Results.Snaps, "snap");
            string note = Note;
            _statusText = null;
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                Progress = 100.0 * p.Done / p.Total;
                Status = Loc.I.Format("SnapProgress", p.Done, p.Total);
            });

            // Written under a temporary name and renamed when complete, so a snap
            // cut short (port lost, stopped) never shows up in the lists.
            string part = path + ".part";
            List<Snapshot.EcuResult> results;
            try
            {
                using (var output = new StreamWriter(part) { AutoFlush = true })
                {
                    _cts = new CancellationTokenSource();
                    results = await _state.Adapter.RunAsync("TakeSnap", (scanner, ct) =>
                    {
                        var taken = Snapshot.Take(scanner, _state.Catalog, targets, note, output,
                            progress: (d, t) => { if (d % 8 == 0 || d == t) ((IProgress<(int, int)>)progress).Report((d, t)); },
                            ct: ct);
                        // Take stops quietly when cancelled; a partial snap must not be kept as a whole one
                        ct.ThrowIfCancellationRequested();
                        return taken;
                    }, _cts.Token);
                }
                File.Move(part, path);
            }
            catch
            {
                File.Delete(part);
                SetStatus(null);
                Progress = 0;
                throw;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }

            int answered = results.Sum(r => r.Answered);
            SetStatus(() => $"{answered}/{targets.Count}   " + Loc.I.Format("SavedTo", Path.GetFileName(path)));
            _state.Log(Status);
            LoadFiles();
        }

        void DoCompare()
        {
            var (changes, same, onlyInOne) = Snapshot.Compare(Snapshot.Load(A!.Path), Snapshot.Load(B!.Path));
            _compared = (changes, same);
            FillRows(changes);
            SetStatus(() => Loc.I.Format("CompareSummary", changes.Count, same)
                + (onlyInOne > 0 ? " " + Loc.I.Format("CompareOnlyInOne", onlyInOne) : ""));
        }

        void FillRows(List<Snapshot.Change> changes)
        {
            var names = _state.Catalog.Dids.Concat(_state.Catalog.Discovered)
                .Where(d => !string.IsNullOrEmpty(d.Name))
                .GroupBy(d => $"{d.Ecu}|{d.Did}")
                .ToDictionary(g => g.Key, g => g.First().Name);

            Rows.Clear();
            foreach (var c in changes)
            {
                names.TryGetValue($"{c.Ecu}|{Catalog.ParseHexOrDefault(c.Did)}", out var name);
                Rows.Add(new ChangeRow(
                    c.Ecu, c.Did, name ?? c.Name ?? "",
                    Describe(c),
                    c.Delta is long d ? d.ToString("+#;-#;0", CultureInfo.CurrentCulture) : "",
                    c.A?.Value?.ToString("0.###", CultureInfo.CurrentCulture) ?? "",
                    c.B?.Value?.ToString("0.###", CultureInfo.CurrentCulture) ?? ""));
            }
        }

        /// <summary>What changed, in the UI language: the reply kinds, the bytes and numbers, or how many bytes differ.</summary>
        static string Describe(Snapshot.Change c)
        {
            if (!c.BothPositive) return $"{Kind(c.A?.Kind)} → {Kind(c.B?.Kind)}";
            if (c.IsNumeric) return $"{c.A!.Hex} → {c.B!.Hex}   {c.RawA} → {c.RawB}  ({c.Delta:+#;-#;0})";
            if (c.A!.Hex.Length != c.B!.Hex.Length) return Loc.I.Format("ChangeLength", c.A.Hex.Length / 2, c.B.Hex.Length / 2);
            return Loc.I.Format("ChangeBytes", c.BytesChanged, c.A.Hex.Length / 2);

            static string Kind(string? kind) => kind switch
            {
                null => Loc.I["ChangeMissing"],
                "positive" => Loc.I["ChangeAnswered"],
                "no-data" => Loc.I["DtcNoData"],
                var k when k.StartsWith("negative:") => Loc.I.Format("DtcRefused", k["negative:".Length..]),
                var k => k
            };
        }
    }
}
