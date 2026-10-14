using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>A mistyped command line: an option without its value, a bad range or list. Shown as its message, exit 1.</summary>
    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    static class Program
    {
        /// <summary>The console without the log: progress lines, which the next line overwrites, go only here.</summary>
        static TextWriter _screen = Console.Out;

        static void Progress(string text) => _screen.Write("\r" + text);

        /// <summary>Blanks the progress line on the console, so the next line starts clean there and in the log.</summary>
        static void EndProgress() => _screen.Write("\r" + new string(' ', 60) + "\r");

        static int Main(string[] args)
        {
            // output sent to a file is UTF-8 (°, ä, Ω), not the console's old code page
            if (Console.IsOutputRedirected) Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            // a mistyped command line (see UsageException), also before anything is opened
            try { return Run(args); }
            catch (UsageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            if (args.Length < 2) { Usage(); return 1; }

            string port = args[0];
            string mode = args[1].ToLowerInvariant();
            if (ArgValue(args, "--out") is string outDir)
            {
                try { Results.Root = Path.GetFullPath(outDir); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new UsageException($"Not a folder: {outDir}"); }
            }
            // a mistyped command gets the usage text, not a question about which car
            if (mode is not ("known" or "dtc" or "scaling" or "scan" or "watch" or "snap" or "ecus"
                          or "merge" or "blocks" or "diff" or "cars")) { Usage(); return 1; }

            if (mode == "cars") { ShowCars(); return 0; }
            // two snaps given by path need no car
            if (mode == "diff" && Positional(args) is [_, _, var a, var b, ..] && File.Exists(a) && File.Exists(b)) return Snapshots.Diff(args);

            if (FindCar(args) is not string catalogPath) return 1;
            // each car's results in its own folder, and the simulated car's apart from the real one's
            Results.Car = port.Equals("demo", StringComparison.OrdinalIgnoreCase)
                ? CarFiles.DemoName(CarFiles.NameOf(catalogPath))
                : CarFiles.NameOf(catalogPath);

            // diff only compares two files, looked up in this car's folders
            if (mode == "diff") return Snapshots.Diff(args);

            // ECU discovery works without a readable car file on a real adapter (the demo car is built from one)
            if (mode == "ecus")
                return WithAdapter(port, args, catalogPath, s => RunEcuDiscovery(s, args, catalogPath));

            Catalog catalog;
            try
            {
                catalog = Catalog.Load(catalogPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read catalogue '{catalogPath}': {ex.Message}");
                return 1;
            }

            // merge touches no hardware, so it runs before the port is opened
            if (mode == "merge")
            {
                // the demo car answers from another car's recorded data: never into a car file
                if (port.Equals("demo", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("The demo car's scans are not merged into a car file.");
                    return 1;
                }
                return RunMerge(catalog, args);
            }
            if (mode == "blocks") { ShowBlocks(catalog); return 0; }

            return WithAdapter(port, args, catalogPath, scanner => mode switch
            {
                "known" => RunKnown(scanner, catalog),
                "dtc" => RunDtc(scanner, catalog, args),
                "scaling" => RunScaling(scanner, catalog, args),
                "scan" => RunScan(scanner, catalog, args),
                "snap" => Snapshots.Take(scanner, catalog, args),
                _ => RunWatch(scanner, catalog, args)
            });
        }

        /// <summary>
        /// Opens and initialises the adapter, runs one mode against it and
        /// turns the expected failures into a message instead of a stack trace.
        /// </summary>
        static int WithAdapter(string port, string[] args, string catalogPath, Func<Scanner, int> run)
        {
            int baud = 115200;
            if (ArgValue(args, "--baud") is string baudText && (!int.TryParse(baudText, out baud) || baud <= 0))
            {
                Console.Error.WriteLine($"Not a baud rate: {baudText}");
                return 1;
            }

            // Everything shown on screen also goes to the logs folder, so no run's
            // output has to be copied by hand - including --trace.
            string logPath;
            StreamWriter logFile;
            try
            {
                logPath = Results.NewFile(Results.Logs, args[1].ToLowerInvariant(), ".log");
                logFile = new StreamWriter(logPath) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"Could not write the results folder: {ex.Message}");
                return 1;
            }
            using var logFileOwned = logFile;
            var (consoleOut, consoleError) = (Console.Out, Console.Error);
            _screen = consoleOut;
            // the console stops writing to the log before the log closes
            using var restoreConsole = new Restore(() => { Console.SetOut(consoleOut); Console.SetError(consoleError); });
            Console.SetOut(new TeeWriter(Console.Out, logFile));
            Console.SetError(new TeeWriter(Console.Error, logFile));
            Console.WriteLine($"# EscoEvObdScanner {string.Join(" ", args)}   ({DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})");
            Console.WriteLine($"# Log: {logPath}");

            // "demo" instead of a COM port runs against the built-in simulated car
            IElmLink? created = null;
            try
            {
                created = port.Equals("demo", StringComparison.OrdinalIgnoreCase)
                    ? DemoCar(catalogPath)
                    : new Elm327(port, baud);
                created.Open();
            }
            catch (Exception ex)
            {
                created?.Dispose();
                Console.Error.WriteLine($"Could not open {port}: {ex.Message}");
                return 1;
            }
            IElmLink elm = created;

            using var _ = elm;
            if (args.Contains("--trace")) elm.Trace += line => Console.Error.WriteLine(line);

            try
            {
                var scanner = new Scanner(elm, Platform(catalogPath)?.Protocol ?? Scanner.DefaultProtocol);
                Console.WriteLine($"Initialising adapter on {port} at {baud} baud ...");
                scanner.Init();

                string version = scanner.Version();
                if (version.Length == 0)
                {
                    Console.Error.WriteLine("The adapter does not answer. Check the port, and try --baud 38400");
                    Console.Error.WriteLine("(the usual default of cheap ELM327 clones).");
                    return 1;
                }
                Console.WriteLine($"Adapter: {version}");
                Console.WriteLine();

                return run(scanner);
            }
            // a mistyped command line, or an ECU or protocol the car file does not have
            catch (Exception ex) when (ex is UsageException or ArgumentException or FormatException or AdapterException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            catch (PortLostException ex)
            {
                Console.Error.WriteLine($"Lost the adapter: {ex.Message}");
                return 1;
            }
            // a result file that cannot be written (disk full, file locked, no access) is not the adapter
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not write a result file: {ex.Message}");
                return 1;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                Console.Error.WriteLine($"Lost the adapter: {ex.Message}");
                return 1;
            }
        }

        // -------------------------------------------------------------------

        static void Usage() => Console.WriteLine(@"
EscoEvObdScanner - read-only UDS diagnostics for electric cars over an ELM327/STN adapter.
Every car-specific detail - ECU addresses, identifiers, formulas, scan blocks,
dashboard values - comes from a car file in the cars folder.

  EscoEvObdScanner - cars
      List the car files. Choose one with --car <name> when there is more than one.

  EscoEvObdScanner <COMx> known
      START HERE. Read every catalogued identifier once and print it decoded
      using the catalogue's own expressions. A readable ECU serial number
      and software version prove the whole chain works.

  EscoEvObdScanner <COMx> dtc [ecuId] [--deep] [--all-ecus]
      Read stored fault codes with service 0x19. --deep also fetches each
      fault's freeze frame and extended data (occurrence and ageing
      counters). ECUs the car file marks as not answering (""answered"":
      false) are skipped unless --all-ecus is given. Read-only: service
      0x14, which erases all of this, is never sent.

  EscoEvObdScanner <COMx> scaling [ecuId]
      Ask service 0x24 to describe the catalogued identifiers: data type,
      byte count, unit and conversion formula. If the ECU implements it,
      scale factors stop being guesswork. Most do not: an ECU that answers
      'not supported', or does not answer at all, is skipped.

  EscoEvObdScanner <COMx> ecus [fromId toId]
      Sweep CAN IDs with TesterPresent (else a read of F186) and report
      responders, from the car file's probe_from (0x600 if it does not say)
      to 0x7EF. Each probe puts a frame on the bus under that ID; lower IDs
      can be the car's ordinary messages and a probe there can leave a fault
      code, so they are refused.
      Start narrow, e.g.  EscoEvObdScanner COM3 ecus 6A0 6BF

  EscoEvObdScanner - blocks
      Show the scan plan from the catalogue, in the order to run it.

  EscoEvObdScanner <COMx> scan <blockId> [ecuId]
  EscoEvObdScanner <COMx> scan <fromDid> <toDid> <ecuId>
      Walk service 0x22 and write every hit to a .jsonl run file. Exits with 2
      when the scan stopped early (the ECU went silent or answered only errors,
      refused the service, or the adapter could not address it), 0 when done.
      Examples:  EscoEvObdScanner COM3 scan d8xx bms
                 EscoEvObdScanner COM3 scan D800 D8FF bms

  EscoEvObdScanner <COMx> watch <ecuId> <did[,did,...]> [intervalMs]
  EscoEvObdScanner <COMx> watch <ecuId>:<did[,did,...]> [<ecuId>:<dids> ...] [intervalMs]
      Poll identifiers and print a line whenever a value changes.
      This is how meaning gets worked out. The second form reads several
      ECUs in turn, to compare values that live in different ECUs.
      Example:  EscoEvObdScanner COM3 watch bms D410,D860 1000
                EscoEvObdScanner COM3 watch vcu:D8D9,D402 bms:D815,D816 500

  EscoEvObdScanner - merge <file.jsonl> [more.jsonl ...]
      Fold scan, snap or watch files into the catalogue's ""discovered"" array. Needs no
      adapter, so pass '-' as the port. Identifiers already catalogued are
      skipped; repeat observations of the same identifier are merged.

  EscoEvObdScanner <COMx> snap [ecuId] [--note ""text""] [--rare] [--refused] [--all]
      Read the identifiers found so far (catalogued and discovered) once
      and save them with a note on what the car was doing. Take one before
      and one after a change - charging, a drive - and diff them.
      Values that seldom change (""changes"": ""rarely"") are read only with
      --rare, identifiers the ECU refused when scanned only with --refused;
      --all reads both.
      Example:  EscoEvObdScanner COM3 snap --note ""before charging, 41 %""

  EscoEvObdScanner - diff <snapA.jsonl> <snapB.jsonl>
      List the identifiers whose value differs between two snaps. Needs no
      adapter. Identifiers only one of the snaps read are counted, not
      compared.

  Port ""demo"" runs every adapter command against a simulated car (where the
  car file has one); its results go to <car>-demo and are never merged.

  Results go to Documents\EscoEvObdScanner\<car>, one folder per kind (scans,
  snaps, dtc, watch, scaling, logs). The Windows app keeps a vehicle's results
  under the vehicle's name, so the two meet only when the app uses a car file
  without a vehicle. File arguments
  to merge and diff are looked up in the chosen car's folders, and wildcards
  work: merge scan_*.jsonl. With several car files, add --car <name>; diff with
  two full paths needs no car.

  --out <folder>     write results under this folder instead
  --car <name>       the car file to use, e.g. --car stellantis-ecmp
  --catalog <path>   a car file anywhere, by path
  --baud <rate>      serial speed, default 115200 (many clones use 38400)
  --trace            echo all adapter traffic to stderr

  --car, --catalog, --out, --baud and --note need a value; without one the
  command stops with an error.
");

        internal static string? ArgValue(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            if (i < 0) return null;
            // a value option at the end, or followed by another option, would otherwise be quietly ignored
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"{name} needs a value.");
            return args[i + 1];
        }

        static readonly string[] ValueOptions = { "--catalog", "--car", "--baud", "--note", "--out" };

        // The cars folder in the current directory first, so merge updates the working
        // copy in the source tree; then the one next to the executable.
        static string[] CarFolders =>
            new[] { Path.Combine(Directory.GetCurrentDirectory(), CarFiles.FolderName), Path.Combine(AppContext.BaseDirectory, CarFiles.FolderName) }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>
        /// The car file to use: --catalog as a path, --car by name, or the only
        /// car there is. With several and no choice, lists them and returns null.
        /// </summary>
        static string? FindCar(string[] args)
        {
            if (ArgValue(args, "--catalog") is string path) return path;

            if (ArgValue(args, "--car") is string name)
            {
                if (CarFiles.Find(name, CarFolders) is string found) return found;
                Console.Error.WriteLine($"No car '{name}'.");
                ShowCars();
                return null;
            }

            var cars = CarFiles.In(CarFolders);
            if (cars.Count == 1) return cars[0];

            Console.Error.WriteLine(cars.Count == 0 ? "No car files found in a cars folder." : "Choose a car with --car <name>:");
            ShowCars();
            return null;
        }

        static void ShowCars()
        {
            foreach (var f in CarFiles.In(CarFolders))
            {
                string name;
                try { name = Catalog.Load(f).Name; }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { name = "(unreadable)"; }
                Console.WriteLine($"  {CarFiles.NameOf(f),-24} {name}");
            }
        }

        static PlatformDef? Platform(string catalogPath)
        {
            try { return Catalog.Load(catalogPath).Platform; }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return null; }
        }

        static IElmLink DemoCar(string catalogPath)
        {
            var catalog = Catalog.Load(catalogPath);
            if (catalog.Platform.Demo == null)
                throw new InvalidOperationException($"There is no demo car for {catalog.Name}.");
            return SimulatedCar.Demo(catalog);
        }

        /// <summary>
        /// The arguments without options, so "scan d8xx --trace" does not take
        /// --trace for an ECU id. Index 0 is the port, 1 the mode.
        /// </summary>
        internal static string[] Positional(string[] args)
        {
            var list = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (ValueOptions.Contains(args[i])) { i++; continue; }
                if (args[i].StartsWith("--")) continue;
                list.Add(args[i]);
            }
            return list.ToArray();
        }

        static void ShowBlocks(Catalog catalog)
        {
            Console.WriteLine("Scan plan:\n");
            foreach (var b in catalog.Blocks.OrderBy(x => x.Order))
            {
                string range = b.IsScannable ? $"{b.Range[0]}-{b.Range[1]}" : "(catalogued only)";
                // a block without a range (the known block) counts the catalogued identifiers, not a number stored in the file
                int count = b.IsScannable ? b.Count : catalog.Dids.Count;
                Console.WriteLine($"  {b.Order}. {b.Id,-7} {range,-18} {count,6} queries   {b.Runtime}");
                Console.WriteLine($"     {b.Name}");
                Console.WriteLine($"     {Wrap(b.Why, 72, "     ")}");
                Console.WriteLine();
            }
        }

        static string Wrap(string? text, int width, string indent)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var words = text.Split(' ');
            var line = new System.Text.StringBuilder();
            var all = new List<string>();
            foreach (var w in words)
            {
                if (line.Length + w.Length + 1 > width) { all.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            if (line.Length > 0) all.Add(line.ToString());
            return string.Join("\n" + indent, all);
        }

        // -------------------------------------------------------------------

        static int RunKnown(Scanner scanner, Catalog catalog)
        {
            EcuDef? current = null;
            KnownValues.Read(scanner, catalog, r =>
            {
                if (r.Ecu != current)
                {
                    current = r.Ecu;
                    Console.WriteLine($"\n{r.Ecu.Name}  (0x{r.Ecu.Request:X3} -> 0x{r.Ecu.Response:X3})");
                }
                Console.WriteLine($"  {r.Def.DidHex}  {r.Def.Name,-28}  {Describe(r)}");
            });

            Console.WriteLine();
            Console.WriteLine("Readable serial number and software version mean the connection works.");
            Console.WriteLine("All silent usually means the car is asleep - put it in Ready.");
            return 0;
        }

        static string Describe(KnownValues.Reading r)
        {
            if (r.Response.Kind != ResponseKind.Positive)
                return r.Response.Kind == ResponseKind.Negative ? $"rejected: {r.Problem}" : r.Problem;

            string hex = Hex(r.Response.Data);
            if (r.Text != null) return $"\"{r.Text}\"";
            if (r.Value is double v) return $"{v,10:F2} {r.Def.Unit,-4}{(r.Uncertain ? " ?" : "")}  raw {hex}";
            return $"raw {hex}   (no expression)";
        }
        // -------------------------------------------------------------------

        const int SilentScalingReads = 5;

        /// <summary>
        /// Asks the ECU to describe its identifiers rather than value them.
        ///
        /// Runs as a separate pass over identifiers already known to exist, so
        /// it costs one query each rather than another sweep of the address
        /// space. Skips an ECU that says the service is not supported, the
        /// likely outcome, or that does not answer at all.
        /// </summary>
        static int RunScaling(Scanner scanner, Catalog catalog, string[] args)
        {
            // scaling [ecuId] - defaults to every catalogued id
            var p = Positional(args);
            string? ecuFilter = p.Length > 2 ? p[2] : null;

            if (ecuFilter != null && catalog.TryEcu(ecuFilter) == null)
            {
                Console.Error.WriteLine($"No ECU '{ecuFilter}' in the catalogue.");
                return 1;
            }

            var targets = catalog.Dids
                .Where(d => d.Did >= 0)             // a malformed "did" in the car file is skipped, not fatal
                .Where(d => ecuFilter == null ||
                            string.Equals(d.Ecu, ecuFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Ecu).ThenBy(d => d.Did)
                .ToList();

            if (targets.Count == 0)
            {
                Console.Error.WriteLine("Nothing to ask about.");
                return 1;
            }

            // created on the first description, so a run nobody answers leaves no empty file
            string? outPath = null;
            StreamWriter? outFile = null;

            Console.WriteLine($"Asking {targets.Count} identifier(s) for their scaling.");
            Console.WriteLine("Service 0x24 is optional and most manufacturers skip it.");
            Console.WriteLine();

            string? currentEcu = null;
            int supported = 0;
            bool ecuUnsupported = false;
            int ecuSilent = 0;

            foreach (var d in targets)
            {
                if (d.Ecu != currentEcu)
                {
                    var e = catalog.Ecu(d.Ecu);
                    currentEcu = d.Ecu;
                    ecuUnsupported = false;
                    ecuSilent = 0;
                    Console.WriteLine($"\n{e.Name}  (0x{e.Request:X3})");
                    try { scanner.Target(e.Request, e.Response); }
                    catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException)
                    {
                        // the adapter cannot address this ECU: skip it, keep the others
                        Console.WriteLine($"  {ex.Message} - skipping this ECU");
                        ecuUnsupported = true;
                    }
                }

                // Once an ECU has said the service is unsupported, or stayed silent
                // too long, stop asking it - but keep going for the other ECUs.
                if (ecuUnsupported || ecuSilent >= SilentScalingReads) continue;

                var r = scanner.ReadScaling(d.Did);

                if (r.Kind == ResponseKind.Negative &&
                    (r.Nrc == 0x11 || r.Nrc == 0x12 || r.Nrc == 0x7F))
                {
                    ecuUnsupported = true;
                    Console.WriteLine($"  service 0x24 not implemented here - skipping this ECU");
                    continue;
                }

                if (r.Kind != ResponseKind.Positive)
                {
                    // silence counts towards giving up; a refusal for this identifier does not
                    ecuSilent = r.Kind == ResponseKind.Negative ? 0 : ecuSilent + 1;
                    Console.WriteLine($"  {d.DidHex}  {r.KindLabel}");
                    if (ecuSilent == SilentScalingReads)
                        Console.WriteLine($"  no answer to {SilentScalingReads} in a row - skipping this ECU");
                    continue;
                }

                ecuSilent = 0;
                supported++;
                if (outFile == null)
                {
                    outPath = Results.NewFile(Results.Scaling, "scaling");
                    outFile = new StreamWriter(outPath) { AutoFlush = true };
                }
                string hex = Hex(r.Data);
                var described = Scaling.Describe(r.Data);

                Console.WriteLine($"  {d.DidHex}  {d.Name}");
                Console.WriteLine($"          raw {hex}");
                foreach (var line in described)
                    Console.WriteLine($"          {line}");

                outFile.WriteLine(new JsonObject
                {
                    ["ts"] = DateTime.UtcNow.ToString("O"),
                    ["ecu"] = d.Ecu,
                    ["did"] = d.DidHex,
                    ["name"] = d.EnglishName,
                    ["scaling_hex"] = hex,
                    ["decoded"] = new JsonArray(described.Select(s => (JsonNode)s).ToArray()),
                    ["current_expression"] = d.Expression
                }.ToJsonString());
            }
            outFile?.Dispose();

            Console.WriteLine();
            if (supported > 0)
            {
                Console.WriteLine($"{supported} identifier(s) described. Saved to {outPath}.");
                Console.WriteLine("Compare each against the catalogue's expression - where they");
                Console.WriteLine("disagree, the ECU is right and the catalogued guess was wrong.");
            }
            return 0;
        }

        /// <summary>
        /// Reads stored faults with service 0x19, plus their freeze frames and
        /// extended data where the ECU offers them.
        ///
        /// Strictly read-only. Service 0x14 would erase all of this and is
        /// never sent from anywhere in this program.
        /// </summary>
        static int RunDtc(Scanner scanner, Catalog catalog, string[] args)
        {
            bool deep = args.Contains("--deep");
            var p = Positional(args);
            string? ecuFilter = p.Length > 2 ? p[2] : null;

            // a mistyped id throws "No ECU '...'" instead of reading nothing; one named is read even if it has not answered
            var ecus = ecuFilter == null
                ? catalog.Ecus.Where(e => args.Contains("--all-ecus") || e.Answered != false).ToList()
                : new List<EcuDef> { catalog.Ecu(ecuFilter) };

            string outPath = Results.NewFile(Results.Dtc, "dtc");
            using var outFile = new StreamWriter(outPath) { AutoFlush = true };
            // the car's clock with the deep read, so the app can date the freeze frames of this file
            (long, DateTime)? clock = null;
            if (deep)
                try { clock = DtcReader.ReadClock(scanner, catalog); }
                catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException) { }

            int refused = 0;
            bool answered = false;
            foreach (var e in ecus)
            {
                Console.WriteLine($"\n{e.Name}  (0x{e.Request:X3})");
                DtcReader.EcuFaults result;
                try { result = DtcReader.Read(scanner, e, deep); }
                catch (AdapterException ex) when (ex.Problem == AdapterProblem.CommandRefused)
                {
                    // this ECU cannot be addressed; the others still can, unless none can
                    Console.WriteLine("  not reachable: the adapter would not address it");
                    if (++refused >= Snapshot.MaxUnreachable && !answered)
                        throw new AdapterException(AdapterProblem.NoEcuAddressable,
                            $"The adapter would not address {refused} ECUs in a row. Disconnect, check the adapter and connect again.", count: refused);
                    continue;
                }
                answered = true;

                if (!result.Answered) { Console.WriteLine($"  {result.Problem}"); continue; }
                if (result.Faults.Count == 0) { Console.WriteLine("  no stored faults"); continue; }

                foreach (var f in result.Faults)
                {
                    Console.WriteLine($"  {f.Dtc}");
                    if (f.Snapshot != null) Console.WriteLine($"      snapshot {f.Snapshot}");
                    if (f.Extended != null) Console.WriteLine($"      extended {f.Extended}");
                    outFile.WriteLine(DtcReader.ToJson(e, f, clock));
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Saved to {outPath}.");
            Console.WriteLine();
            Console.WriteLine("ACTIVE   = failing right now (status bit 0).");
            Console.WriteLine("stored   = failed at some point and remembered, not failing now.");
            Console.WriteLine("no fault = only 'test not completed' bits: a check that has not run yet.");
            if (!deep)
                Console.WriteLine("Add --deep for freeze frames and occurrence counters.");
            Console.WriteLine();
            Console.WriteLine("Snapshot and extended data come back as raw bytes. Their layout is");
            Console.WriteLine("manufacturer-defined and rarely published, so they are logged");
            Console.WriteLine("rather than decoded.");
            return 0;
        }

        /// <summary>
        /// The ecus command. Uses the car file for the probe floor and for the
        /// reply IDs never to transmit on; works without it on a real adapter.
        /// </summary>
        static int RunEcuDiscovery(Scanner scanner, string[] args, string catalogPath)
        {
            Catalog? catalog = null;
            try { catalog = Catalog.Load(catalogPath); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
            // reply IDs of the catalogued ECUs: discovery never transmits on them
            var skip = catalog?.Ecus.Select(e => e.Response).Where(r => r > 0).ToList() ?? new List<int>();
            scanner.ProbeFloor = catalog?.Platform.ProbeFrom ?? Scanner.DefaultProbeFloor;

            var p = Positional(args);
            int from = scanner.ProbeFloor, to = 0x7EF;

            // a lone bound would otherwise fall back to the full sweep the help warns about
            if (p.Length == 3)
                throw new UsageException("Give both ends of the ID range, e.g.  ecus 6A0 6BF");
            if (p.Length >= 4)
            {
                from = Catalog.ParseHexOrDefault(p[2]);
                to = Catalog.ParseHexOrDefault(p[3]);
            }
            if (from < scanner.ProbeFloor || to > 0x7FF || from > to)
                throw new UsageException($"ID range must be within {scanner.ProbeFloor:X3}-7FF, low end first.");

            Console.WriteLine($"Probing 0x{from:X3}-0x{to:X3} for ECUs ({to - from + 1} IDs) ...");
            var ecus = scanner.DiscoverEcus(from, to,
                probing: id => { if ((id & 0x1F) == 0) Progress($"  probing 0x{id:X3} ..."); },
                foundEcu: (req, resp) => { EndProgress(); Console.WriteLine($"  ECU found: request 0x{req:X3} -> response 0x{resp:X3}"); },
                skip: skip);
            Console.WriteLine();
            if (ecus.Count == 0)
            {
                Console.WriteLine("Nothing responded.");
                Console.WriteLine("Check: car in Ready (not just ignition), correct COM port, and");
                Console.WriteLine("whether this car routes diagnostics via OBD pins 3/8 rather than 6/14.");
                return 0;
            }

            Console.WriteLine($"{ecus.Count} ECU(s) responding:");
            foreach (var (req, resp) in ecus)
                Console.WriteLine($"  0x{req:X3} -> 0x{resp:X3}");
            return 0;
        }

        // -------------------------------------------------------------------

        static int RunScan(Scanner scanner, Catalog catalog, string[] all)
        {
            var args = Positional(all);
            if (args.Length < 3) { Usage(); return 1; }

            int from, to;
            string ecuId;

            // scan <blockId> [ecuId]   |   scan <from> <to> <ecuId>
            var block = catalog.Blocks.FirstOrDefault(
                b => string.Equals(b.Id, args[2], StringComparison.OrdinalIgnoreCase));

            if (block != null)
            {
                if (!block.IsScannable)
                {
                    Console.Error.WriteLine($"Block '{block.Id}' has no range - it is the catalogued set. Use 'known'.");
                    return 1;
                }
                from = block.From;
                to = block.To;
                ecuId = args.Length > 3 ? args[3]
                      : block.Ecus?.FirstOrDefault()
                        ?? throw new UsageException($"Block '{block.Id}' names no ECU - give one after the block id.");
                Console.WriteLine($"Block {block.Id}: {block.Name}");
            }
            else
            {
                if (args.Length < 5)
                {
                    Console.Error.WriteLine($"No block '{args[2]}' in the car file, and not a hex range (scan <from> <to> <ecu>). See: blocks");
                    return 1;
                }
                from = Catalog.ParseHexOrDefault(args[2]);
                to = Catalog.ParseHexOrDefault(args[3]);
                ecuId = args[4];
            }

            if (from < 0 || to > 0xFFFF || from > to)
                throw new UsageException("Identifier range must be within 0000-FFFF, low end first.");

            var ecu = catalog.Ecu(ecuId);

            string runPath = Results.NewFile(Results.Scans, $"scan_{ecu.Id}_{from:X4}-{to:X4}");
            using var run = new StreamWriter(runPath) { AutoFlush = true };

            Console.WriteLine($"Scanning 0x{from:X4}..0x{to:X4} on {ecu.Name} (0x{ecu.Request:X3})");
            // what the car file says is scanned already: a scan is not stopped for it, only told
            var (covered, overlapping) = ScannedRange.Coverage(ecu.Scanned, from, to);
            if (overlapping.Count > 0)
            {
                Console.WriteLine($"Already scanned in full: {covered} of {to - from + 1} identifiers. The car file lists:");
                foreach (var s in overlapping)
                    Console.WriteLine($"  {s.RangeText}  {(s.IsFull ? "" : $"(every {s.Step}th only) ")}{s.Notes}");
            }
            Console.WriteLine($"Writing {runPath}  - one JSON object per line, flushed on every hit.");
            Console.WriteLine("Ctrl+C is safe: the file stays valid because each line is complete.");
            Console.WriteLine();

            var started = DateTime.UtcNow;
            int hits = 0;

            var result = DidScan.Run(scanner, catalog, ecu, from, to, run,
                hit: h =>
                {
                    hits++;
                    var r = h.Response;
                    string tag = h.Known != null ? $"  [{h.Known.Name}]" : "";
                    // over the progress line, which has no line break of its own and is not in the log
                    EndProgress();
                    Console.WriteLine($"  {h.Did:X4}  {r.KindLabel,-30} {r.Data.Length,3}B  {Hex(r.Data)}{tag}");
                },
                progress: did =>
                {
                    if (((did - from) & 0x3F) != 0 && did != to) return;
                    var elapsed = DateTime.UtcNow - started;
                    double done = did - from + 1, total = to - from + 1;
                    var eta = TimeSpan.FromTicks((long)(elapsed.Ticks / done * (total - done)));
                    Progress($"  0x{did:X4}  hits:{hits}  eta:{eta.ToString(@"hh\:mm\:ss")}     ");
                });

            // a scan that stopped early exits with 2, so a script can tell it from a finished one
            int exit = result.Outcome is DidScan.Outcome.ServiceRefused or DidScan.Outcome.Silent or DidScan.Outcome.AdapterRefused ? 2 : 0;
            switch (result.Outcome)
            {
                case DidScan.Outcome.ServiceRefused:
                    Console.WriteLine($"\n  {ecu.Name} refuses service 0x22: {UdsResponse.NrcName(result.RefusalNrc)}. Stopping.");
                    break;
                case DidScan.Outcome.Silent:
                    Console.WriteLine($"\n  No answer to {DidScan.MaxSilent} queries in a row - ECU asleep or gone.");
                    Console.WriteLine($"  Resume with:  EscoEvObdScanner <COMx> scan {result.ResumeFrom:X4} {to:X4} {ecu.Id} "
                        + (ArgValue(all, "--catalog") != null ? $"--catalog \"{Path.GetFullPath(catalog.Path)}\"" : $"--car {CarFiles.NameOf(catalog.Path)}")
                        + (ArgValue(all, "--baud") is string baud ? $" --baud {baud}" : "")
                        + (ArgValue(all, "--out") is string outDir ? $" --out \"{Path.GetFullPath(outDir)}\"" : ""));
                    break;
                case DidScan.Outcome.AdapterRefused:
                    Console.WriteLine($"\n  The adapter refused to address {ecu.Name}. Stopping.");
                    break;
            }

            Console.WriteLine($"\n\n{(result.Outcome == DidScan.Outcome.Completed ? "Done" : "Stopped")}. {result.Hits} identifiers. Saved to {runPath}");
            // the demo car answers from recorded data: its scans are not for a car file
            if (Results.Car is not { } car || !car.EndsWith("-demo", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Fold it into the car file with:  EscoEvObdScanner - merge \"{runPath}\" "
                    + (ArgValue(all, "--catalog") != null ? $"--catalog \"{Path.GetFullPath(catalog.Path)}\"" : $"--car {CarFiles.NameOf(catalog.Path)}"));
            return exit;
        }

        // -------------------------------------------------------------------

        static int RunWatch(Scanner scanner, Catalog catalog, string[] all)
        {
            var args = Positional(all);

            static int[] Dids(string list)
            {
                var dids = list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Catalog.ParseHexOrDefault).ToArray();
                if (dids.Length == 0 || dids.Any(d => d < 0 || d > 0xFFFF))
                    throw new UsageException($"Identifiers must be hex, separated by commas: {list}");
                return dids;
            }

            // "<ecuId> <dids>", or one "<ecuId>:<dids>" per ECU to read several in turn
            var groups = new List<(EcuDef Ecu, int[] Dids)>();
            int next = 2;
            if (args.Length > 2 && args[2].Contains(':'))
            {
                for (; next < args.Length && args[next].Contains(':'); next++)
                {
                    var parts = args[next].Split(':', 2);
                    groups.Add((catalog.Ecu(parts[0]), Dids(parts[1])));
                }
            }
            else
            {
                if (args.Length < 4) { Usage(); return 1; }
                groups.Add((catalog.Ecu(args[2]), Dids(args[3])));
                next = 4;
            }
            int interval = 500;
            if (args.Length > next && (!int.TryParse(args[next], out interval) || interval < 0))
                throw new UsageException($"Not an interval in milliseconds: {args[next]}");

            bool several = groups.Select(g => g.Ecu.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (!several) scanner.Target(groups[0].Ecu.Request, groups[0].Ecu.Response);

            string logPath = Results.NewFile(Results.Watch, "watch_" + string.Join("_", groups.Select(g => g.Ecu.Id).Distinct(StringComparer.OrdinalIgnoreCase)));
            using var log = new StreamWriter(logPath) { AutoFlush = true };

            var previous = new Dictionary<(string Ecu, int Did), string>();

            Console.WriteLine($"Watching {groups.Sum(g => g.Dids.Length)} identifier(s) on {string.Join(", ", groups.Select(g => g.Ecu.Name).Distinct())} every {interval} ms.");
            if (several) Console.WriteLine("The ECUs are read in turn, so a round takes longer than with one.");
            Console.WriteLine($"Logging {logPath}. Ctrl+C to stop.");
            Console.WriteLine();
            Console.WriteLine("Now change something: plug in the charger, switch on the heater,");
            Console.WriteLine("drive a little. Whatever moves is what that identifier means.");
            Console.WriteLine();

            while (true)
            {
                foreach (var (ecu, dids) in groups)
                {
                    if (several) scanner.Target(ecu.Request, ecu.Response);
                    foreach (int did in dids)
                    {
                        var r = scanner.ReadDid(did);
                        if (r.Kind != ResponseKind.Positive) continue;

                        string hex = Hex(r.Data);
                        if (previous.TryGetValue((ecu.Id, did), out var old) && old == hex) continue;

                        var known = catalog.Find(ecu.Id, did);
                        double? v = known != null ? Expr.Evaluate(known.Expression, r.Data) : null;

                        var record = new JsonObject
                        {
                            ["ts"] = DateTime.UtcNow.ToString("O"),
                            ["ecu"] = ecu.Id,
                            ["did"] = $"0x{did:X4}",
                            ["kind"] = "positive",      // only positive replies are logged; merge needs the kind
                            ["hex"] = hex,
                            ["ascii"] = Ascii(r.Data),
                            ["value"] = v,
                            ["name"] = known?.EnglishName
                        };
                        log.WriteLine(record.ToJsonString());

                        string shown = v.HasValue ? $"{v.Value,10:F2} {known?.Unit}" : hex;
                        string marker = old == null ? " " : "*";
                        string where = several ? $"{ecu.Id,-7}" : "";
                        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {marker} {where}{did:X4}  {shown,-18} {known?.Name}");
                        previous[(ecu.Id, did)] = hex;
                    }
                }

                Thread.Sleep(interval);
            }
        }

        // -------------------------------------------------------------------

        /// <summary>
        /// The merge command; CatalogMerge does the work.
        /// </summary>
        static int RunMerge(Catalog catalog, string[] args)
        {
            var files = Results.Resolve(Positional(args).Skip(2), Results.Scans, Results.Snaps, Results.Watch);
            if (files.Count == 0) { Usage(); return 1; }
            // results of the simulated car never go into a car file
            // a demo car's results sit in <results>\<car>-demo\...
            static bool IsDemoResult(string f)
            {
                string rel = Path.GetRelativePath(Path.GetFullPath(Results.Root), Path.GetFullPath(f));
                return !rel.StartsWith("..") && !Path.IsPathRooted(rel)
                       && rel.Split(Path.DirectorySeparatorChar)[0].EndsWith("-demo", StringComparison.OrdinalIgnoreCase);
            }
            if (files.FirstOrDefault(IsDemoResult) is string demo)
            {
                Console.Error.WriteLine($"{demo} is a result of the demo car; it is not merged into a car file.");
                return 1;
            }

            CatalogMerge.Result r;
            try
            {
                r = CatalogMerge.Merge(catalog, files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"Could not merge: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"{r.Added} new, {r.Updated} with a new sample, {r.Skipped} with nothing new.");
            if (r.Cleared) Console.WriteLine("Stored values of identifying identifiers were removed.");
            if (r.Added + r.Updated == 0)
            {
                Console.WriteLine(r.Cleared ? $"Written to {catalog.Path}" : "Nothing new; the car file is unchanged.");
                return 0;
            }
            Console.WriteLine($"Written to {catalog.Path}");
            Console.WriteLine();
            Console.WriteLine("Each new entry has null name/expression. Fill those in as you work");
            Console.WriteLine("out what they mean, and move the solved ones up into \"dids\".");
            return 0;
        }

        // -------------------------------------------------------------------

        static string Hex(byte[] b) => Bytes.Hex(b);

        static string Ascii(byte[] b) => Bytes.Ascii(b);
    }
}
