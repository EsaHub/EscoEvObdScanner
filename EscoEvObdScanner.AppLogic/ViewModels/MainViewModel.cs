using System.ComponentModel;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>A language button: its code and what pressing it does.</summary>
    public sealed record LanguageChoice(string Code, RelayCommand Select);

    public sealed class MainViewModel : ObservableObject
    {
        string? _error;

        public MainViewModel()
        {
            State = new AppState();
            Connection = new ConnectionViewModel(State);
            Live = new LiveViewModel(State);
            Scan = new ScanViewModel(State);
            Dtc = new DtcViewModel(State);
            // the report lists the faults only once they have been read; "no faults" is then a finding
            Live.FaultCodes = Dtc.CurrentRead;
            Compare = new CompareViewModel(State);
            Discovery = new EcuViewModel(State);
            Browse = new BrowseViewModel(State);

            State.Adapter.PropertyChanged += OnAdapterChanged;
            // a laptop left in the car: the live view starts without anyone touching it
            Connection.Connected += () => { if (Live.AutoStart && Live.Start.CanExecute(null)) Live.Start.Execute(null); };
            Live.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LiveViewModel.IsRunning)) Raise(nameof(ShowLiveNotice)); };
            State.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppState.Catalog) or nameof(AppState.CarTitle)) Raise(nameof(Subtitle)); };
            AsyncCommand.Failed += ex =>
            {
                Error = Errors.Describe(ex);
                State.Log($"{Loc.I["Error"]}: {Error}");
            };

            Languages = Loc.I.Languages.Select(code => new LanguageChoice(code.ToUpperInvariant(), new RelayCommand(() => SetLanguage(code)))).ToList();
            SetFinnish = new RelayCommand(() => SetLanguage("fi"));
            SetEnglish = new RelayCommand(() => SetLanguage("en"));
            DismissError = new RelayCommand(() => Error = null);
            OpenHelp = new RelayCommand(() =>
            {
                try { AppHost.I.Open(HelpFile()); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Error = ex.Message; }
            });
            Error = State.StartupWarning;
        }

        public AppState State { get; }

        /// <summary>The tab shown; tabs from Fault codes on cannot read while the live view runs.</summary>
        public int SelectedTab { get => _selectedTab; set { if (Set(ref _selectedTab, value)) Raise(nameof(ShowLiveNotice)); } }
        int _selectedTab;
        const int FirstReadingTab = 4;

        /// <summary>The live view runs and this tab waits for it: say so, and offer to stop it.</summary>
        public bool ShowLiveNotice => Live.IsRunning && SelectedTab >= FirstReadingTab;
        public ConnectionViewModel Connection { get; }
        public LiveViewModel Live { get; }
        public ScanViewModel Scan { get; }
        public DtcViewModel Dtc { get; }
        public CompareViewModel Compare { get; }
        /// <summary>The ECU search, the left half of the Discovery tab; <see cref="Scan"/> is the right half.</summary>
        public EcuViewModel Discovery { get; }
        public BrowseViewModel Browse { get; }

        /// <summary>A button per language file.</summary>
        public IReadOnlyList<LanguageChoice> Languages { get; }

        public RelayCommand OpenHelp { get; }

        /// <summary>The user guide in the UI language, else in English; installed next to the app. Public for the self-test.</summary>
        public static string HelpFile()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "help");
            string own = Path.Combine(folder, Loc.I.Language.Equals("fi", StringComparison.OrdinalIgnoreCase) ? "ohje.fi.html" : "guide.en.html");
            return File.Exists(own) ? own : Path.Combine(folder, "guide.en.html");
        }

        /// <summary>Self-test hooks: the language buttons do the same through <see cref="Languages"/>.</summary>
        public RelayCommand SetFinnish { get; }
        public RelayCommand SetEnglish { get; }
        public RelayCommand DismissError { get; }

        /// <summary>The last failure, shown in a bar at the top until dismissed.</summary>
        public string? Error { get => _error; set => Set(ref _error, value); }

        /// <summary>Under the title: the chosen car as the list names it.</summary>
        public string Subtitle => Loc.I.Format("AppSubtitle", State.CarTitle);

        public string StatusText
        {
            get
            {
                var a = State.Adapter;
                if (!a.IsConnected) return Loc.I["NotConnected"];
                string s = a.IsDemo ? $"{Loc.I["Demo"]} · {a.Version}" : Loc.I.Format("ConnectedTo", a.Version ?? "");
                return a.IsBusy ? $"{s}  ·  {Loc.I["Busy"]}: {a.BusyWith}" : s;
            }
        }

        void OnAdapterChanged(object? sender, PropertyChangedEventArgs e) => Raise(nameof(StatusText));

        void SetLanguage(string language)
        {
            Loc.I.Language = language;
            AppHost.I.LanguageChanged();
            State.Settings.Language = language;
            State.Settings.Save();
            Raise(nameof(StatusText));
            Raise(nameof(Subtitle));
        }

        /// <summary>What must not be lost when Windows ends the session: it does not wait for the adapter.</summary>
        public void SaveNow()
        {
            Live.StopRecording();
            State.Settings.Save();
            State.CloseLog();
        }

        public async Task ShutdownAsync()
        {
            Live.StopRecording();
            await State.Adapter.DisconnectAsync();
            State.Settings.Save();
            State.CloseLog();
        }
    }
}
