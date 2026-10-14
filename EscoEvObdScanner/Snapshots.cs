namespace EscoEvObdScanner
{
    /// <summary>Console front end for <see cref="Snapshot"/>: the snap and diff commands.</summary>
    static class Snapshots
    {
        public static int Take(Scanner scanner, Catalog catalog, string[] args)
        {
            var p = Program.Positional(args);
            string? ecuFilter = p.Length > 2 ? p[2] : null;
            string note = Program.ArgValue(args, "--note") ?? "";
            if (ecuFilter != null && catalog.TryEcu(ecuFilter) == null)
            {
                Console.Error.WriteLine($"No ECU '{ecuFilter}' in the catalogue.");
                return 1;
            }

            bool all = args.Contains("--all");
            var targets = Snapshot.Targets(catalog, ecuFilter, rare: all || args.Contains("--rare"), refused: all || args.Contains("--refused"));
            if (targets.Count == 0)
            {
                Console.Error.WriteLine("Nothing to read - run some scans and merge them first.");
                return 1;
            }

            // written under a temporary name: a snap cut short (adapter gone) never shows up among the snaps
            string outPath = Results.NewFile(Results.Snaps, "snap");
            string part = outPath + ".part";
            var outFile = new StreamWriter(part) { AutoFlush = true };

            Console.WriteLine($"Reading {targets.Count} identifier(s). Note: \"{note}\"");
            Console.WriteLine($"Writing {outPath}");
            Console.WriteLine();

            List<Snapshot.EcuResult> results;
            try
            {
                results = Snapshot.Take(scanner, catalog, targets, note, outFile,
                    ecuStarted: ecu => Console.Write($"  {ecu.Name,-34} "),
                    ecuDone: r => Console.WriteLine(
                        r.Unreachable ? "not reachable: the adapter would not address it" :
                        (r.StoppedSilent ? $"stopped: no answer to {Snapshot.MaxSilent} in a row  " : "") + $"{r.Answered}/{r.Total} answered"));
            }
            catch
            {
                Console.WriteLine();
                outFile.Dispose();
                File.Delete(part);
                throw;
            }
            // the snap is complete: if the rename fails, the .part file keeps it rather than being deleted
            outFile.Dispose();
            File.Move(part, outPath);

            Console.WriteLine();
            Console.WriteLine($"{results.Sum(r => r.Answered)} value(s) saved to {outPath}.");
            Console.WriteLine("Take another snap after the change, then compare the two:");
            Console.WriteLine($"  EscoEvObdScanner - diff {outPath} <second snap>");
            return 0;
        }

        public static int Diff(string[] args)
        {
            var files = Results.Resolve(Program.Positional(args).Skip(2), Results.Snaps);
            if (files.Count != 2)
            {
                Console.Error.WriteLine("Usage: EscoEvObdScanner - diff <snapA.jsonl> <snapB.jsonl>");
                return 1;
            }

            Snapshot.Snap a, b;
            try
            {
                a = Snapshot.Load(files[0]);
                b = Snapshot.Load(files[1]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            Console.WriteLine($"A: {a.Path}  \"{a.Note}\"");
            Console.WriteLine($"B: {b.Path}  \"{b.Note}\"");
            Console.WriteLine();

            var (changes, same, onlyInOne) = Snapshot.Compare(a, b);
            foreach (var c in changes)
            {
                string values = c.A?.Value is double va && c.B?.Value is double vb ? $"   [{va:G6} -> {vb:G6}]" : "";
                Console.WriteLine($"{c.Ecu,-7} {c.Did}  {c.Name ?? "",-28} {c.Describe()}{values}");
            }

            Console.WriteLine();
            Console.WriteLine($"{changes.Count} changed, {same} unchanged."
                + (onlyInOne > 0 ? $" {onlyInOne} read by only one of the snaps, not compared." : ""));
            return 0;
        }
    }
}
