using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.AppLogic.Services
{
    /// <summary>
    /// Owns the adapter connection and makes sure only one thing talks to it
    /// at a time: connecting, disconnecting and every operation go through the
    /// same gate. Operations run on a worker thread through <see cref="RunAsync{T}"/>,
    /// so the UI stays responsive, and a second one waits (or is disabled in
    /// the UI) until the first finishes.
    /// </summary>
    public sealed class AdapterService : ObservableObject
    {
        readonly SemaphoreSlim _gate = new(1, 1);
        IElmLink? _link;
        Scanner? _scanner;
        CancellationTokenSource? _current;

        /// <summary>Cancels the running work; it may have ended and disposed its token source in the meantime.</summary>
        void CancelCurrent()
        {
            var current = _current;
            try { current?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        bool _isConnected;
        bool _isDemo;
        string? _version;
        string? _busyWith;

        /// <summary>Raw adapter traffic, raised on a worker thread.</summary>
        public event Action<string>? Traffic;

        public bool IsConnected { get => _isConnected; private set { if (Set(ref _isConnected, value)) Raise(nameof(CanRun)); } }
        public bool IsDemo { get => _isDemo; private set => Set(ref _isDemo, value); }
        public string? Version { get => _version; private set => Set(ref _version, value); }

        /// <summary>
        /// What the adapter is doing right now in the UI language, or null when idle. The work is named
        /// by a UI text key, optionally followed by a space and a detail ("TabDiscovery bms"), so the text
        /// follows a language change while the work runs.
        /// </summary>
        public string? BusyWith
        {
            get => _busyWith is not string what ? null
                : what.Split(' ', 2) is [var key, var detail] ? $"{Loc.I[key]} {detail}" : Loc.I[what];
            private set { if (Set(ref _busyWith, value)) { Raise(nameof(IsBusy)); Raise(nameof(CanRun)); } }
        }

        public bool IsBusy => _busyWith != null;
        public bool CanRun => IsConnected && !IsBusy;

        public Task ConnectAsync(string port, int baud, Catalog catalog) =>
            OpenAsync(() => new Elm327(port, baud), catalog, demo: false);

        public Task ConnectDemoAsync(Catalog catalog) => OpenAsync(() =>
        {
            if (catalog.Platform.Demo == null)
                throw new InvalidOperationException(Loc.I.Format("NoDemoCar", catalog.Name));
            var car = SimulatedCar.Demo(catalog);
            car.LatencyMs = 6;          // feels like a real adapter, keeps the demo lively
            car.Dynamic = true;
            return car;
        }, catalog, demo: true);

        async Task OpenAsync(Func<IElmLink> create, Catalog catalog, bool demo)
        {
            CancelCurrent();
            await _gate.WaitAsync();
            BusyWith = "Connect";
            try
            {
                Close();

                var link = create();
                link.Trace += line => Traffic?.Invoke(line);

                // everything that talks to the port happens off the UI thread;
                // nothing is kept until the adapter has answered
                var (scanner, version) = await Task.Run(() =>
                {
                    try
                    {
                        link.Open();
                        var s = new Scanner(link, catalog.Platform.Protocol ?? Scanner.DefaultProtocol)
                        {
                            ProbeFloor = catalog.Platform.ProbeFrom
                        };
                        s.Init();
                        return (s, s.Version());
                    }
                    catch
                    {
                        link.Dispose();
                        throw;
                    }
                });

                if (version.Length == 0)
                {
                    link.Dispose();
                    throw new InvalidOperationException(Loc.I["AdapterNoAnswer"]);
                }

                _link = link;
                _scanner = scanner;
                Version = version;
                IsDemo = demo;
                IsConnected = true;
            }
            finally
            {
                BusyWith = null;
                _gate.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            CancelCurrent();
            await _gate.WaitAsync();
            try
            {
                Close();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Only with the gate held.</summary>
        void Close()
        {
            _link?.Dispose();
            _link = null;
            _scanner = null;
            IsConnected = false;
            IsDemo = false;
            Version = null;
        }

        /// <summary>
        /// Runs one adapter operation on a worker thread. The token passed to
        /// <paramref name="work"/> is cancelled by <paramref name="ct"/> or by
        /// disconnecting. If the port itself is gone (unplugged, closed), the
        /// connection is dropped so the UI says so; any other failure leaves it.
        /// <paramref name="what"/> names the work by a UI text key, see <see cref="BusyWith"/>.
        /// </summary>
        public async Task<T> RunAsync<T>(string what, Func<Scanner, CancellationToken, T> work, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _current = linked;
            BusyWith = what;
            try
            {
                var scanner = _scanner ?? throw new InvalidOperationException(Loc.I["NeedConnection"]);
                return await Task.Run(() => work(scanner, linked.Token), CancellationToken.None);
            }
            catch (Exception ex) when (ex is PortLostException || _link is { IsOpen: false })
            {
                Close();
                throw;
            }
            finally
            {
                _current = null;
                BusyWith = null;
                _gate.Release();
            }
        }

        public Task RunAsync(string what, Action<Scanner, CancellationToken> work, CancellationToken ct = default) =>
            RunAsync<bool>(what, (s, t) => { work(s, t); return true; }, ct);
    }
}
