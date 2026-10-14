using System.Diagnostics;
using System.Text;

namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The command line's arguments, run as the built executable against the
    /// demo car: both forms of watch, options between the arguments, and the
    /// messages for arguments it cannot use.
    /// </summary>
    public class CommandLineTests
    {
        /// <summary>The command line built beside the tests: ...\EscoEvObdScanner\bin\&lt;configuration&gt;\net10.0.</summary>
        static string Executable()
        {
            var tests = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            // net10.0 -> configuration -> bin -> EscoEvObdScanner.Tests -> source tree
            var root = tests.Parent!.Parent!.Parent!.Parent!.FullName;
            return Path.Combine(root, "EscoEvObdScanner", "bin", tests.Parent!.Name, tests.Name,
                OperatingSystem.IsWindows() ? "EscoEvObdScanner.exe" : "EscoEvObdScanner");
        }

        sealed record Run(int? ExitCode, string Output, string Error, string OutDir);

        /// <summary>
        /// Runs the command line with <paramref name="args"/> and results under a folder of its own.
        /// A watch runs until stopped: it is stopped once <paramref name="enough"/> holds for what it printed.
        /// </summary>
        static Run Start(string args, Func<string, bool>? enough = null)
        {
            string outDir = Path.Combine(TestTemp.Root, Guid.NewGuid().ToString("N"));
            var info = new ProcessStartInfo(Executable(), $"{args} --car stellantis-ecmp --out \"{outDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var output = new StringBuilder();
            var error = new StringBuilder();
            using var seen = new ManualResetEventSlim();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (output)
                {
                    output.AppendLine(e.Data);
                    if (enough?.Invoke(output.ToString()) == true) seen.Set();
                }
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            int? exit = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !process.HasExited && !seen.IsSet) seen.Wait(100);
            if (process.HasExited)
            {
                process.WaitForExit();
                exit = process.ExitCode;
            }
            else
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            lock (output) lock (error) return new Run(exit, output.ToString(), error.ToString(), outDir);
        }

        static string[] WatchLog(Run run) =>
            Directory.GetFiles(Path.Combine(run.OutDir, "stellantis-ecmp-demo", "watch"), "watch_*.jsonl").SelectMany(File.ReadAllLines).ToArray();

        [Fact]
        public void WatchOfOneEcuReadsItsIdentifiers()
        {
            var run = Start("demo watch bms D410,D815 200", o => o.Contains("   D815 "));

            Assert.Null(run.ExitCode);                  // still watching when stopped
            Assert.Contains("Watching 2 identifier(s) on Battery management (BMS) every 200 ms.", run.Output);
            Assert.DoesNotContain("read in turn", run.Output);
            Assert.Contains(run.Output.Split('\n'), l => l.Contains("D410") && l.Contains('%'));
            Assert.All(WatchLog(run), l => Assert.Contains("\"ecu\":\"bms\"", l));
        }

        [Fact]
        public void WatchOfSeveralEcusReadsThemInTurn()
        {
            var run = Start("demo watch vcu:D402,D49C bms:D410 300", o => o.Contains("bms    D410"));

            Assert.Null(run.ExitCode);
            Assert.Contains("Watching 3 identifier(s) on Vehicle control unit (VCU), Battery management (BMS) every 300 ms.", run.Output);
            Assert.Contains("The ECUs are read in turn", run.Output);
            Assert.Contains("vcu    D402", run.Output);
            Assert.Contains("vcu    D49C", run.Output);
            var log = WatchLog(run);
            Assert.Contains(log, l => l.Contains("\"ecu\":\"vcu\"") && l.Contains("\"did\":\"0xD49C\""));
            Assert.Contains(log, l => l.Contains("\"ecu\":\"bms\"") && l.Contains("\"did\":\"0xD410\""));
            Assert.Single(Directory.GetFiles(Path.Combine(run.OutDir, "stellantis-ecmp-demo", "watch"), "watch_vcu_bms_*.jsonl"));
        }

        [Fact]
        public void OptionsBetweenTheArgumentsAreNotTakenForThem()
        {
            // --note takes a value, --trace does not
            var run = Start("demo watch --trace bms:D410 --note x 250", o => o.Contains("D410  "));

            Assert.Null(run.ExitCode);
            Assert.Contains("Watching 1 identifier(s) on Battery management (BMS) every 250 ms.", run.Output);
            Assert.Contains("[22D410]", run.Error);
        }

        [Theory]
        [InlineData("demo watch bms:ZZ", "Identifiers must be hex, separated by commas: ZZ")]
        [InlineData("demo watch bms D410 soon", "Not an interval in milliseconds: soon")]
        [InlineData("demo watch bms:D410 vcu D402", "Not an interval in milliseconds: vcu")]
        [InlineData("demo watch nosuch:D410", "No ECU 'nosuch' in the catalogue")]
        public void UnusableWatchArgumentsAreRefused(string args, string message)
        {
            var run = Start(args);
            Assert.Equal(1, run.ExitCode);
            Assert.Contains(message, run.Error);
            Assert.DoesNotContain("Watching", run.Output);
        }

        [Fact]
        public void WatchWithoutIdentifiersShowsTheUsage()
        {
            var run = Start("demo watch bms");
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("watch <ecuId>:<did[,did,...]> [<ecuId>:<dids> ...] [intervalMs]", run.Output);
        }

        [Fact]
        public void TheDemoCarsResultsAreNotMerged()
        {
            var run = Start("demo merge scan_bms.jsonl");
            Assert.Equal(1, run.ExitCode);
            Assert.Contains("The demo car's scans are not merged into a car file.", run.Error);
        }
    }
}
