using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.App.Services;
using EscoEvObdScanner.AppLogic.Reports;
using EscoEvObdScanner.AppLogic.Services;
using EscoEvObdScanner.AppLogic.ViewModels;

namespace EscoEvObdScanner.App
{
    /// <summary>
    /// <c>EscoEvObdScanner.App.exe --selftest &lt;folder&gt;</c> drives the real UI
    /// against the demo car the way a user would: connect, live view, every
    /// tab and every action. Each tab is saved as a PNG and every check is
    /// written to report.txt. Exit code 0 when all checks pass.
    ///
    /// Runs in an isolated data folder so it never touches the user's results.
    /// </summary>
    static class SelfTest
    {
        public static async Task<int> RunAsync(MainWindow window, MainViewModel vm, TabControl tabs, string folder)
        {
            Directory.CreateDirectory(folder);
            var report = new List<string>();
            int failures = 0;

            void Check(string what, bool ok)
            {
                report.Add($"{(ok ? "PASS" : "FAIL")}  {what}");
                if (!ok) failures++;
            }

            async Task Shot(int tab, string name, int waitMs = 1200)
            {
                tabs.SelectedIndex = tab;
                await Task.Delay(waitMs);
                Save(window, Path.Combine(folder, $"{tab + 1:00}_{name}.png"));
            }

            async Task Until(Func<bool> condition, int timeoutMs)
            {
                var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (!condition() && DateTime.UtcNow < end) await Task.Delay(100);
            }

            try
            {
                // Finnish whatever the Windows language; the _en screenshots switch to English and back
                vm.SetFinnish.Execute(null);

                // connection: the demo car is listed as a car of its own, built from the e-CMP file
                var demo = vm.Connection.Cars.FirstOrDefault(c => c.Demo);
                Check("cars: the demo car is in the list", demo != null && demo.Path.EndsWith("stellantis-ecmp.json", StringComparison.OrdinalIgnoreCase));
                Check("cars: only cars with recorded data have one", vm.Connection.Cars.Count(c => c.Demo) == 1);
                if (demo != null) vm.Connection.SelectedCar = demo;
                Check("demo: no adapter needed", vm.State.IsDemoCar && !vm.Connection.NeedsAdapter && vm.Connection.Connect.CanExecute(null));
                vm.Connection.Connect.Execute(null);
                await Until(() => vm.State.Adapter.IsConnected, 5000);
                Check("connect to demo car", vm.State.Adapter.IsConnected && vm.State.Adapter.IsDemo);
                await Until(() => vm.State.VinAsked && !vm.State.Adapter.IsBusy, 5000);
                Check("connect: the VIN is read for the report", vm.State.Vin == "VR7DEM00000000001");
                Check("demo: title says so", vm.Subtitle.Contains("demo"));
                await Shot(0, "connection");

                // live view: let it run long enough for charts and cell data
                vm.Live.Start.Execute(null);
                await Until(() => vm.Live.Now?.CellVoltages != null, 10000);
                await Task.Delay(8000);
                Check("live view has state", vm.Live.Now != null);
                Check("live: SOC read", vm.Live.Now?.Soc is > 0 and <= 100);
                Check("live: 108 cell voltages", vm.Live.Now?.CellVoltages?.Length == 108);
                Check("live: 54 temperature probes", vm.Live.Now?.ModuleTemps?.Length == 54);
                Check("live: power computed", vm.Live.Now?.Power != null);
                Check("live: odometer read", vm.Live.Now?.Odometer == 59381);
                Check("live: drivetrain temperatures for the dashboard group", vm.Live.Now is { HasDrivetrainTemps: true, MotorTemp: > -40 and < 200, InverterTemp: > -40 and < 200 });
                Check("live: speed moving in demo", vm.Live.Now?.Speed > 0);
                Check("charts: points recorded", vm.Live.PowerSeries.Points.Count > 5);
                Check("trip: distance accumulates", vm.Live.TripKm > 0);
                Check("trip: net used is used less recovered", Math.Abs(vm.Live.TripNetKwh - (vm.Live.TripKwh - vm.Live.TripRegenKwh)) < 1e-9);
                Check("trip: charge used since the start is shown", vm.Live.TripSocUsed != null && vm.Live.TripSocNote.Contains('→'));
                Check("lifetime: the car's counters are on the dashboard", vm.Live.HasLifetime && vm.Live.LifeOut > 1000);
                // energy into the battery includes regeneration, so it is above what was taken out
                Check("lifetime: energy into the battery shown, above energy out", vm.Live.LifeIn > vm.Live.LifeOut && vm.Live.LifeInNote == Loc.I["LifeInNote"]);
                Check("lifetime: average from the counter and the odometer", vm.Live.LifeAverage is > 5 and < 50);
                string oldPrice = vm.Live.PriceText, oldLoss = vm.Live.LossText;
                vm.Live.PriceText = "16,5";
                vm.Live.LossText = "10";
                Check("lifetime: cost at the typed price with the loss", vm.Live.LifeCost is double cost
                      && Math.Abs(cost - vm.Live.LifeOut!.Value * 1.1 * 0.165) < 0.01 && vm.Live.LifeCostNote.Contains("16"));
                string oldInUse = vm.Live.InUseText, oldInUseKm = vm.Live.InUseKmText;
                vm.Live.InUseText = "";
                Check("lifetime: no yearly figure before the date taken into use is given", vm.Live.LifeCostYear == null);
                vm.Live.InUseText = "4.12.2024";
                vm.Live.InUseKmText = "0";
                double years = ((vm.Live.Now?.Time ?? DateTime.Now) - new DateTime(2024, 12, 4)).TotalDays / 365.25;
                Check("lifetime: yearly cost counts from the date taken into use", vm.Live.LifeCostYear is double year && vm.Live.LifeCost is double all
                      && Math.Abs(year - all / years) < 0.5 && vm.Live.LifeYearNote.Contains("km"));
                vm.Live.InUseKmText = "29 690";
                Check("lifetime: a used car counts only the kilometres since", vm.Live.LifeCostYear is double used && vm.Live.LifeCost is double whole
                      && vm.Live.Now?.Odometer is double odo && Math.Abs(used - whole * (odo - 29690) / odo / years) < 0.5);
                vm.Live.InUseText = oldInUse;
                vm.Live.InUseKmText = oldInUseKm;
                vm.Live.PriceText = "x";
                Check("lifetime: a price that is not a number is not taken", vm.Live.PriceText == "16.5" || vm.Live.PriceText == "16,5");
                vm.Live.PriceText = oldPrice;
                vm.Live.LossText = oldLoss;
                Check("live: no range on the charge gauge without a consumption", vm.Live.RangeGaugeText == "" || vm.Live.RangeGaugeText.Contains("km"));
                Check("csv recording file exists", vm.Live.RecordingFile != null && File.Exists(vm.Live.RecordingFile));
                Check("demo: results in a folder of their own", vm.Live.RecordingFile?.Contains(@"\stellantis-ecmp-demo\") == true);
                await Shot(1, "dashboard");
                // the trip computer and the lifetime figures are below the gauges
                foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible)) sv.ScrollToEnd();
                await Shot(1, "dashboard_lower", 600);
                foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible)) sv.ScrollToHome();
                await Shot(2, "battery");
                vm.Live.ShowCellHealth = true;
                await Shot(2, "battery_health", 600);
                vm.Live.ShowCellLoad = true;
                Check("battery: cells under load view says what it rests on", vm.Live.CellLoadStatus.Length > 0 && !vm.Live.ShowCellHealth);
                Check("battery: cells under load coloured by grade", !double.IsNaN(vm.Live.CellWarnAt) && vm.Live.CellBadAt >= BatteryChecks.CellLoadBadMv);
                await Shot(2, "battery_load", 600);
                // the README's screenshots (assets/screenshots), in English and while the live view runs
                vm.SetEnglish.Execute(null);
                Check("language: the running work is named in the new language", vm.StatusText.Contains("Dashboard") && !vm.StatusText.Contains("Mittaristo"));
                await Shot(2, "battery_load_en", 800);
                vm.Live.ShowCellLoad = false;
                await Shot(2, "battery_en", 800);
                vm.Live.ShowCellHealth = true;
                await Shot(2, "battery_health_en", 800);
                vm.Live.ShowCellHealth = false;
                // with a price and a day taken into use, so the electricity estimate is filled in
                string shotPrice = vm.Live.PriceText, shotInUse = vm.Live.InUseText, shotInUseKm = vm.Live.InUseKmText;
                vm.Live.PriceText = "16";
                vm.Live.InUseText = "2024-12-04";
                vm.Live.InUseKmText = "0";
                await Shot(1, "dashboard_live_en", 800);
                // the lower part
                foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible)) sv.ScrollToEnd();
                await Shot(1, "dashboard_lower_en", 800);
                foreach (var sv in Descendants<ScrollViewer>(window).Where(s => s.IsVisible)) sv.ScrollToHome();
                vm.Live.PriceText = shotPrice;
                vm.Live.InUseText = shotInUse;
                vm.Live.InUseKmText = shotInUseKm;
                vm.SetFinnish.Execute(null);
                vm.Live.ShowCellLoad = true;
                // the same view from the recording just made
                Check("recording: cell health is kept, so a report from it has the health grid", vm.Live.RecordingFile != null
                      && Recording.Read(vm.Live.RecordingFile).LastOrDefault()?.CellSoh?.Length == 108);
                Check("battery: a recording opens in the under-load view", vm.Live.RecordingFile != null && vm.Live.ShowRecording(vm.Live.RecordingFile)
                      && vm.Live.HasShownRecording && vm.Live.CellLoadStatus.Contains(Path.GetFileName(vm.Live.RecordingFile)));
                vm.Live.ShowRecording(null);
                Check("battery: and back to the live view", !vm.Live.HasShownRecording);
                Check("recordings: listed by what they hold", vm.Live.RecordingChoices() is [var newest, ..] && newest.Text.Contains("km"));
                vm.Live.ShowCellLoad = false;
                Check("battery: back to voltages", double.IsNaN(vm.Live.CellWarnAt));
                await Shot(3, "charts");
                await Shot(5, "live_notice", 600);
                Check("live: the reading tabs say the live view holds the adapter", vm.ShowLiveNotice);

                vm.Live.Stop.Execute(null);
                await Until(() => !vm.Live.IsRunning, 5000);
                Check("live: and stop saying so once it stops", !vm.ShowLiveNotice);
                Check("live view stops", !vm.Live.IsRunning);
                Check("csv has rows", vm.Live.RecordingFile != null && File.ReadAllLines(vm.Live.RecordingFile).Length > 5);
                var cellsFile = vm.Live.RecordingFile == null ? null : Recording.CellsFile(vm.Live.RecordingFile);
                var cellLines = cellsFile != null && File.Exists(cellsFile) ? File.ReadAllLines(cellsFile) : Array.Empty<string>();
                Check("cells csv: time + 108 cells + 54 probes per row",
                    cellLines.Length >= 2 && cellLines.All(l => l.Split(';').Length == 1 + 108 + 54));

                // known values of every ECU
                Check("browse: starts with the answering ECUs checked and the known shown",
                    vm.Browse.Ecus.All(e => e.IsChecked == !e.NeverAnswered) && vm.Browse.Filter == BrowseViewModel.Show.Known);
                Check("browse: the ECUs that have not answered are a group of their own, last",
                    vm.Browse.Ecus.Any(e => e.NeverAnswered)
                    && vm.Browse.Ecus.SkipWhile(e => !e.NeverAnswered).All(e => e.NeverAnswered)
                    && vm.Browse.Ecus.Count(e => e.FirstNeverAnswered) == 1);
                var checkedDids = vm.State.Catalog.Dids.Where(d => vm.Browse.Ecus.Any(e => e.IsChecked && e.Def.Id == d.Ecu)).ToList();
                var known = vm.Browse.RowsView.Cast<BrowseRow>().ToList();
                Check("browse: the catalogued identifiers listed, rare and refused ones hidden",
                    known.Count == checkedDids.Count(d => d.ReadWith(false, false)) && vm.Browse.Hint.Contains(Loc.I.Format("BrowseHidden", checkedDids.Count - known.Count)));
                vm.Browse.ShowRare = vm.Browse.ShowRefused = true;
                Check("browse: and shown on request", vm.Browse.RowsView.Cast<BrowseRow>().Count() == checkedDids.Count);
                vm.Browse.ShowRare = vm.Browse.ShowRefused = false;
                vm.Browse.Read.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 30000);
                Check("browse: reads more than one ECU", known.Where(r => r.Value.Length > 0).Select(r => r.Ecu.Id).Distinct().Count() > 1);
                await Shot(5, "browse_read");

                // scan a block
                vm.Scan.Ecu = vm.Scan.Ecus.First(e => e.Id == "bms");
                vm.Scan.From = "D800";
                vm.Scan.To = "D8FF";
                Check("scan: says the range is scanned already", vm.Scan.ScannedInfo.StartsWith(Loc.I["ScanCoveredAll"]) && vm.Scan.ScannedInfo.Contains("0xD000-0xDFFF"));
                // an ECU scanned in part: a sample over 0000-CFFF with some pages in full (as long as the car file has one)
                // (a 512-identifier block that is only partly covered by full pages)
                static bool Partly(EcuDef e, ScannedRange s) =>
                    s.IsFull && s.To < 0xD000 && ScannedRange.Coverage(e.Scanned, s.From & 0xFE00, (s.From & 0xFE00) + 0x1FF).Covered < 512;
                var partly = vm.Scan.Ecus.FirstOrDefault(e => e.Scanned.Any(s => !s.IsFull) && e.Scanned.Any(s => Partly(e, s)));
                if (partly != null)
                {
                    var page = partly.Scanned.First(s => Partly(partly, s));
                    int from = page.From & 0xFE00, to = from + 0x1FF;
                    int covered = ScannedRange.Coverage(partly.Scanned, from, to).Covered;
                    vm.Scan.Ecu = partly;
                    vm.Scan.From = from.ToString("X4");
                    vm.Scan.To = to.ToString("X4");
                    Check($"scan: and how much when only partly, samples not counted ({partly.Id} {from:X4}-{to:X4})",
                        covered < 512 && vm.Scan.ScannedInfo.StartsWith(Loc.I.Format("ScanCoveredPart", covered, 512)) && vm.Scan.ScannedInfo.Contains(Loc.I.Format("ScanSample", 16)));
                }
                // an ECU that never answered has nothing scanned
                vm.Scan.Ecu = vm.Scan.Ecus.First(e => e.Answered == false);
                vm.Scan.From = "0000";
                vm.Scan.To = "00FF";
                Check("scan: says when nothing of the range is scanned", vm.Scan.ScannedInfo.StartsWith(Loc.I["ScanCoveredNone"]));
                vm.Scan.Ecu = vm.Scan.Ecus.First(e => e.Id == "bms");
                vm.Scan.From = "D800";
                vm.Scan.To = "D8FF";
                vm.Scan.Start.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 60000);
                Check("scan: 43 identifiers in D800-D8FF", vm.Scan.Rows.Count == 43);
                Check("scan: result file written", vm.Scan.LastFile != null && File.Exists(vm.Scan.LastFile));
                await Shot(6, "scan");

                // the car check of vehicles: the battery ECU's serial number, kept only as a hash
                var identity = await vm.State.Adapter.RunAsync("selftest", (scanner, _) => VehicleIdentity.Read(scanner, vm.State.Catalog));
                Check("vehicles: the battery ECU gives an identity, as a hash", identity is { Length: 16 } && !identity.Contains("DEMO"));

                // fault codes: the ECUs that have not answered are left out unless asked for
                var neverAnswered = vm.State.Catalog.Ecus.Where(e => e.Answered == false).Select(e => e.Name).ToList();
                vm.Dtc.Read.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy && vm.Dtc.Summary.Length > 0, 30000);
                Check("dtc: ECUs that have not answered are skipped by default",
                    neverAnswered.Count > 0 && vm.Dtc.Rows.Count > 0 && !vm.Dtc.Rows.Any(r => neverAnswered.Contains(r.Ecu)));
                vm.Dtc.AllEcus = true;
                vm.Dtc.Deep = true;
                vm.Dtc.Read.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy && vm.Dtc.Summary.Length > 0, 30000);
                Check("dtc: active U1208 found", vm.Dtc.Rows.Any(r => r.Code == "U1208-29" && r.Severity == "ACTIVE"));
                Check("dtc: the freeze frame tells when the fault occurred", vm.Dtc.Rows.Any(r => r.Code == "P1C00-16" && r.Occurred.Contains("km")));
                Check("dtc: silent ECU reported, not crashed", vm.Dtc.Rows.Any(r => r.Severity == "no-data"));
                Check("dtc: and asked when chosen", neverAnswered.All(n => vm.Dtc.Rows.Any(r => r.Ecu == n)));
                Check("dtc: checks not run are hidden by default", vm.Dtc.RowsView.Cast<DtcRow>().All(r => !r.NotRun));
                vm.Dtc.ShowNotRun = true;
                Check("dtc: and shown on request", vm.Dtc.RowsView.Cast<DtcRow>().Count() == vm.Dtc.Rows.Count);
                vm.Dtc.ShowNotRun = false;
                Check("dtc: the read is listed under earlier reads", vm.Dtc.Files.Count >= 1);
                vm.Dtc.SelectedFile = vm.Dtc.Files.FirstOrDefault();
                vm.Dtc.ShowFile.Execute(null);
                Check("dtc: a saved read shows again", vm.Dtc.Rows.Any(r => r.Code == "U1208-29"));
                await Shot(4, "dtc");

                // battery report from the last reading, with the fault codes just read
                vm.Live.ReportNote = "Self-test";
                vm.Live.CreateReport.Execute(null);
                var pdf = vm.Live.LastReport;
                Check("report: pdf written", pdf != null && File.Exists(pdf) && new FileInfo(pdf).Length > 5000);
                Check("report: is a pdf", pdf != null && File.Exists(pdf) && File.ReadAllBytes(pdf).Take(5).SequenceEqual("%PDF-"u8.ToArray()));
                if (pdf != null && File.Exists(pdf)) File.Copy(pdf, Path.Combine(folder, "11_report.pdf"), true);
                // the saved read on the tab: the BMS has only checks not run, which from a saved read is only info; the other ECUs' faults are shown, not rated
                var rows = vm.Live.ReportInput() is { } ri ? BatteryReport.SummaryRows(ri) : Array.Empty<BatteryReport.SummaryRow>();
                var battery = rows.FirstOrDefault(r => r.Name == Loc.I["RepBatteryFaults"]);
                var other = rows.FirstOrDefault(r => r.Name == Loc.I["RepOtherFaults"]);
                Check("report: battery faults rated from the BMS only", battery?.Grade == Grade.Info && battery.Value == Loc.I.Format("RepFaultCount", 0, 0));
                Check("report: other ECUs' faults shown, not rated", other?.Grade == Grade.Info && other.Value == Loc.I.Format("RepFaultCount", 1, 4));
                // the drive behind the report, and a report made afterwards from the recording
                Check("report: the VIN ties the report to the car", vm.Live.ReportInput() is { Vin: "VR7DEM00000000001", VinAsked: true });
                Check("report: the session's drive is included", vm.Live.ReportInput()?.Drive is { HasData: true, Km: > 0, CellReads: > 0 });
                var recorded = Results.Files(Results.Live, "live_*.csv").FirstOrDefault(p => !p.EndsWith("_cells.csv", StringComparison.OrdinalIgnoreCase));
                var fromFile = recorded == null ? null : vm.Live.ReportFromFile(recorded);
                Check("report: made from a recording", fromFile != null && File.Exists(fromFile) && new FileInfo(fromFile).Length > 5000);
                if (vm.Live.ReportInput() is { } baseInput)
                {
                    Grade? Rated(FaultRead read) =>
                        BatteryReport.SummaryRows(baseInput with { Faults = read }).First(r => r.Name == Loc.I["RepBatteryFaults"]).Grade;
                    var silentBms = new DtcRow("BMS", "—", "no-data", "", "", "", "", "", EcuId: "bms", Answered: false);
                    var bmsFault = new DtcRow("BMS", "U1189-87", "ACTIVE", "0x09", "", "", "", "", EcuId: "bms");
                    var bmsClean = new DtcRow("BMS", "—", "none", "", "", "", "", "", EcuId: "bms");
                    Check("report: a silent BMS is not rated", Rated(new FaultRead(new[] { silentBms }, true, null, DateTime.Now)) == null);
                    Check("report: a fault found counts even if the read was cut short", Rated(new FaultRead(new[] { bmsFault }, false, null, DateTime.Now)) == Grade.Bad);
                    Check("report: a complete clean read is good", Rated(new FaultRead(new[] { bmsClean }, true, null, DateTime.Now)) == Grade.Good);
                    Check("report: an empty saved read is only info", Rated(new FaultRead(Array.Empty<DtcRow>(), true, "dtc_x.jsonl", DateTime.Now)) == Grade.Info);

                    // a car that gives its lifetime counters (the demo car does not) gets their section
                    var counted = baseInput.State.Clone();
                    (counted.LifetimeChargedKwh, counted.LifetimeDischargedKwh) = (13729.4, 13106.1);
                    (counted.AcCharges, counted.DcCharges, counted.AcChargedKwh, counted.DcChargedKwh) = (604, 28, 12179, 504);
                    var lifetimePdf = Path.Combine(folder, "12_report_lifetime.pdf");
                    BatteryReport.Write(baseInput with { State = counted }, lifetimePdf);
                    Check("report: lifetime counters written", File.Exists(lifetimePdf) && new FileInfo(lifetimePdf).Length > 5000);

                    // the ? button's user guide, in both languages
                    Check("help: the user guide is next to the app", File.Exists(MainViewModel.HelpFile()));
                    Check("help: in Finnish and English", File.Exists(Path.Combine(AppContext.BaseDirectory, "help", "ohje.fi.html"))
                                                          && File.Exists(Path.Combine(AppContext.BaseDirectory, "help", "guide.en.html")));
                }

                // snaps and compare
                vm.Compare.Note = "selftest A";
                vm.Compare.TakeSnap.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 60000);
                await Task.Delay(1100);              // file names have one-second resolution
                vm.Compare.Note = "selftest B";
                vm.Compare.TakeSnap.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 60000);
                Check("compare: two snaps listed", vm.Compare.Files.Count >= 2);
                Check("compare: a snap leaves out the rare and refused identifiers",
                    vm.Compare.Files.Count >= 1 && Snapshot.Load(vm.Compare.Files[0].Path).Entries.Count == Snapshot.Targets(vm.State.Catalog).Count
                    && Snapshot.Targets(vm.State.Catalog).Count < Snapshot.Targets(vm.State.Catalog, rare: true, refused: true).Count);
                vm.Compare.Compare.Execute(null);
                Check("compare: dynamic values changed", vm.Compare.Rows.Count > 0);
                await Shot(7, "compare");

                // which known ECUs answer
                vm.Browse.Check.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 60000);
                Check("ecus: known ECUs answer", vm.Browse.Ecus.Count(e => e.Answered == true) >= 5 && vm.Browse.Ecus.Where(e => e.IsChecked).All(e => e.Answered != null));

                // ECU discovery
                Check("discovery: starts with the car file's range", vm.Discovery.From == "680" && vm.Discovery.To == "6FF");
                Check("discovery: says the range is searched already", vm.Discovery.SearchedInfo.StartsWith(Loc.I["EcuSearchedAll"]) && vm.Discovery.SearchedInfo.Contains("0x680-0x6FF"));
                vm.Discovery.From = "400";
                vm.Discovery.To = "5FF";
                Check("discovery: and how much of a range searched in part", vm.Discovery.SearchedInfo.StartsWith(Loc.I.Format("EcuSearchedPart", 256, 512)));
                vm.Discovery.From = "680";
                vm.Discovery.To = "6BF";
                Check("discovery: range explained with a duration", vm.Discovery.RangeInfo.Contains("64") && vm.Discovery.RangeInfo.Contains(Loc.I["AboutMinute"]));
                vm.Discovery.Discover.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 60000);
                Check("discovery: five found in 680-6BF", vm.Discovery.Found.Count == 5);
                Check("discovery: nothing to add from the demo car", !vm.Discovery.AddNew.CanExecute(null));
                await Shot(6, "discovery");

                // identifier browser
                var bms = vm.State.Catalog.Ecu("bms");
                vm.Browse.CheckOnly("bms");
                Check("browse: every bms identifier listed", vm.Browse.Rows.Count == bms.Dids.Count + bms.Discovered.Count);
                vm.Browse.Filter = BrowseViewModel.Show.Unknown;
                Check("browse: unknown filter", vm.Browse.RowsView.Cast<BrowseRow>().Count() == bms.Discovered.Count(d => d.ReadWith(false, false)));
                vm.Browse.Filter = BrowseViewModel.Show.All;
                vm.Browse.Search = "D86";
                vm.Browse.Read.Execute(null);
                await Task.Delay(300);
                await Until(() => !vm.State.Adapter.IsBusy, 30000);
                var shown = vm.Browse.RowsView.Cast<BrowseRow>().ToList();
                // search also looks in names and notes, so a few rows mention D86x without being one
                Check("browse: search narrows the list", shown.Any(r => r.Did == "D865") && shown.Count < vm.Browse.Rows.Count);
                Check("browse: read fills values", shown.Any(r => r.Known && r.Value.Length > 0));
                vm.Browse.SelectedRow = shown.FirstOrDefault(r => r.Did == "D865");
                await Shot(5, "browse");

                // language switch
                vm.SetEnglish.Execute(null);
                await Shot(1, "dashboard_en", 800);
                Check("language switches to English", tabs.Items.OfType<TabItem>().First().Header as string == "Connection");
                vm.SetFinnish.Execute(null);

                Check("no error shown", vm.Error == null);

                // the live view can start by itself on connecting, for a laptop left in the car
                await vm.State.Adapter.DisconnectAsync();
                vm.Live.AutoStart = true;
                Check("live: starting on connect is remembered", vm.State.Settings.AutoStartLive && Settings.Load().AutoStartLive);
                vm.Connection.Connect.Execute(null);
                await Until(() => vm.Live.IsRunning, 10000);
                Check("live: starts by itself once connected", vm.Live.IsRunning);
                vm.Live.Stop.Execute(null);
                await Until(() => !vm.Live.IsRunning, 5000);
                vm.Live.AutoStart = false;
                await Until(() => !vm.State.Adapter.IsBusy, 5000);

                // another car: only while disconnected, and the tabs follow its file
                await vm.State.Adapter.DisconnectAsync();
                // vehicles: named cars on the car files, each with its own results
                var files = vm.Connection.CarFiles();
                string kiaFile = files.First(f => f.Path.EndsWith("kia-eniro.json", StringComparison.OrdinalIgnoreCase)).Path;
                string ecmpFile = files.First(f => f.Path.EndsWith("stellantis-ecmp.json", StringComparison.OrdinalIgnoreCase)).Path;
                Check("vehicles: a vehicle is added", vm.Connection.AddVehicleNamed("Testi Mokka", ecmpFile) == null && vm.State.CarId == "Testi Mokka");
                Check("vehicles: a name is used once", vm.Connection.AddVehicleNamed("testi mokka", kiaFile) != null);
                Check("vehicles: demo names are kept for demo cars", vm.Connection.AddVehicleNamed("x-demo", kiaFile) != null);
                Check("vehicles: a second vehicle", vm.Connection.AddVehicleNamed("Testi Kia", kiaFile) == null);
                var kia = vm.Connection.Cars.FirstOrDefault(c => c.Vehicle?.Name == "Testi Kia");
                Check("cars: the vehicles and demo cars listed", vm.Connection.Cars.Count(c => c.Vehicle != null) == 2 && kia != null && vm.Connection.Cars.Any(c => c.Demo));
                if (kia != null) vm.Connection.SelectedCar = kia;
                await Task.Delay(300);
                Check("cars: switching changes the car file", vm.State.Catalog.Name.Contains("Kia"));
                Check("cars: the last car's scan cannot be merged", vm.Scan.LastFile == null && vm.Scan.Rows.Count == 0 && !vm.Scan.Merge.CanExecute(null));
                Check("cars: title follows the car", vm.Subtitle.Contains("Kia"));
                Check("cars: a real car needs the adapter", vm.Connection.NeedsAdapter);
                // the real e-CMP: the demo car's file, but another car with its own results
                var real = vm.Connection.Cars.FirstOrDefault(c => c.Vehicle?.Name == "Testi Mokka");
                if (real != null) vm.Connection.SelectedCar = real;
                Check("cars: the real car after its demo is another car", !vm.State.IsDemoCar && vm.State.CarId == "Testi Mokka"
                      && vm.Subtitle.Contains("Testi Mokka") && !vm.Subtitle.Contains("demo") && Results.Car == "Testi Mokka");
                if (kia != null) vm.Connection.SelectedCar = kia;
                Check("cars: battery view follows its layout", vm.Live.CellColumns == 14);
                Check("cars: no report without a reading of this car", !vm.Live.CreateReport.CanExecute(null));
                await Task.Delay(300);
                Check("cars: the log moves to the new car's folder", Results.Files(Results.Logs, "app_*.log").Any(f =>
                {
                    using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                    var text = r.ReadToEnd();
                    return text.Contains(vm.State.Catalog.Name) && !text.Contains("Stellantis");
                }));
                await Shot(0, "connection_kia", 600);
                if (vm.Error != null) report.Add($"      error: {vm.Error}");
            }
            catch (Exception ex)
            {
                Check($"no exception ({ex.GetType().Name}: {ex.Message})", false);
            }

            report.Add(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
            File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
            return failures == 0 ? 0 : 1;
        }

        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
        }

        static void Save(Window window, string path)
        {
            var content = (FrameworkElement)window.Content;
            double dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX;
            double scale = dpi / 96.0;
            var bmp = new RenderTargetBitmap(
                (int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);

            // render on the window background so the PNG is not transparent
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            }
            bmp.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var file = File.Create(path);
            encoder.Save(file);
        }
    }
}
