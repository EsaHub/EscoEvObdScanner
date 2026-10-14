namespace EscoEvObdScanner.Tests
{
    /// <summary>The folders every test's temporary files go under; earlier runs' are removed when the test assembly loads.</summary>
    static class TestTemp
    {
        /// <summary>Every run's folders live under here.</summary>
        public static readonly string Base = Path.Combine(Path.GetTempPath(), "EscoEvObdScannerTests");

        /// <summary>This run's own folder, so two runs at once (the exe and an IDE) do not delete each other's files.</summary>
        public static readonly string Root = Path.Combine(Base, $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Clean()
        {
            // left over from earlier runs: only folders an hour old, so a run going on beside this one keeps its files
            if (Directory.Exists(Base))
                foreach (var old in Directory.GetDirectories(Base).Where(d => Directory.GetLastWriteTime(d) < DateTime.Now.AddHours(-1)))
                    try { Directory.Delete(old, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var file in Directory.Exists(Base) ? Directory.GetFiles(Base) : Array.Empty<string>())
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // no test may start pointing at the user's real Documents or settings folders
            EscoEvObdScanner.AppLogic.Services.AppPaths.UseIsolatedFolders(Path.Combine(Root, "app"));
        }
    }

    /// <summary>
    /// An adapter that replays fixed replies, for testing the response parser
    /// against exact adapter output (including lines captured from a real car).
    /// AT commands get "OK" unless scripted.
    /// </summary>
    sealed class ScriptedLink : IElmLink
    {
        readonly Dictionary<string, Queue<string[]>> _replies = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Sent { get; } = new();

        public event Action<string>? Trace;

        /// <summary>Queues a reply; "|" separates lines, as in the --trace output.</summary>
        public ScriptedLink On(string command, string reply)
        {
            if (!_replies.TryGetValue(command, out var q)) _replies[command] = q = new();
            q.Enqueue(reply.Split('|', StringSplitOptions.RemoveEmptyEntries));
            return this;
        }

        public bool IsOpen => true;
        public void Open() { }
        public void Dispose() { }

        /// <summary>A scripted reply that starts with the command is an echo, taken off as the adapter link does.</summary>
        public bool LastReplyEchoed { get; private set; }

        public string[] Send(string command, int timeoutMs = 5000)
        {
            Sent.Add(command);
            Trace?.Invoke(command);
            var reply = _replies.TryGetValue(command, out var q) && q.Count > 0 ? q.Dequeue()
                : command.StartsWith("AT", StringComparison.OrdinalIgnoreCase) ? new[] { "OK" } : new[] { "NO DATA" };
            LastReplyEchoed = reply.Length > 0 && reply[0].Equals(command, StringComparison.OrdinalIgnoreCase);
            return LastReplyEchoed ? reply[1..] : reply;
        }
    }

    static class TestData
    {
        public static string CatalogPath => Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName, "stellantis-ecmp.json");

        public static Catalog Catalog() => EscoEvObdScanner.Catalog.Load(CatalogPath);

        /// <summary>A copy of the catalogue in a temp folder, for tests that write to it.</summary>
        public static string CatalogCopy()
        {
            var dir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "stellantis-ecmp.json");
            File.Copy(CatalogPath, path);
            return path;
        }

        public static (SimulatedCar Car, Scanner Scanner, Catalog Catalog) DemoCar()
        {
            var catalog = Catalog();
            var car = SimulatedCar.Demo(catalog);
            car.Open();
            var scanner = new Scanner(car);
            scanner.Init();
            return (car, scanner, catalog);
        }

        /// <summary>The demo car with one ECU's replies and faults left out, so that ECU stays silent.</summary>
        public static (SimulatedCar Car, Scanner Scanner, Catalog Catalog) DemoCarWithout(string ecuId)
        {
            var catalog = Catalog();
            using var stream = typeof(SimulatedCar).Assembly.GetManifestResourceStream("EscoEvObdScanner.Resources.demo-car.jsonl")!;
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n')
                .Where(l => !l.Contains($"\"ecu\":\"{ecuId}\""));
            var car = new SimulatedCar(catalog, lines);
            car.IgnoreTesterPresent(0x6A2);
            car.Open();
            var scanner = new Scanner(car);
            scanner.Init();
            return (car, scanner, catalog);
        }
    }
}
