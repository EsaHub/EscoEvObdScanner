using System.Globalization;
using System.Reflection;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.ViewModels;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MColor = MigraDoc.DocumentObjectModel.Color;

namespace EscoEvObdScanner.AppLogic.Reports
{
    /// <summary>
    /// A PDF of the battery as the app shows it right now: the latest reading,
    /// every cell and probe, and the fault codes on the Fault codes tab if they
    /// were read. Texts in the UI language. Only what the car file lets the app
    /// read appears; the rest is left out rather than shown empty.
    /// </summary>
    public static class BatteryReport
    {
        public sealed record Input(
            string CarName,
            string? CarDetails,
            string CarFile,
            string? Note,
            VehicleState State,
            int CellColumns,
            int ProbeColumns,
            FaultRead? Faults,
            IReadOnlySet<string> BatteryEcus,
            bool Demo,
            DriveStats? Drive,
            string? Recording,
            string? Vin = null,
            bool VinAsked = false,
            DateTime? VinRead = null,
            string? Warranty = null);

        /// <summary>One line of the summary table.</summary>
        public sealed record SummaryRow(string Name, string Value, Grade? Grade, string Comment, string Source);

        /// <summary>Where a figure comes from, as the report's Source column says it.</summary>
        public enum Source { Car, Calculated }

        static readonly MColor Good = new(0x16, 0xA3, 0x4A);
        static readonly MColor Watch = new(0xD9, 0x77, 0x06);
        static readonly MColor Bad = new(0xDC, 0x26, 0x26);
        static readonly MColor Info = new(0x64, 0x74, 0x8B);
        static readonly MColor Line = new(0xCB, 0xD5, 0xE1);
        static readonly MColor HeaderFill = new(0xF1, 0xF5, 0xF9);
        static readonly MColor Accent = new(0x08, 0x91, 0xB2);

        static string T(string key) => Loc.I[key];
        static CultureInfo C => CultureInfo.CurrentCulture;
        static string N(double? v, string format) => v?.ToString(format, C) ?? "—";

        /// <summary>Writes the report to <paramref name="path"/>.</summary>
        public static void Write(Input input, string path)
        {
            ReportFonts.Use();

            var doc = new Document();
            doc.Info.Title = T("RepTitle");
            doc.Info.Subject = input.CarName;
            doc.Info.Author = "Esco EV OBD Scanner";

            var normal = doc.Styles[StyleNames.Normal]!;
            normal.Font.Name = ReportFonts.Family;
            normal.Font.Size = 10;

            var section = doc.AddSection();
            var page = section.PageSetup;
            page.PageFormat = PageFormat.A4;
            page.LeftMargin = page.RightMargin = Unit.FromCentimeter(1.8);
            page.TopMargin = Unit.FromCentimeter(1.6);
            page.BottomMargin = Unit.FromCentimeter(1.8);

            var footer = section.Footers.Primary.AddParagraph();
            footer.Format.Font.Size = 7;
            footer.Format.Font.Color = Info;
            footer.AddText($"Esco EV OBD Scanner {Version()} · {T(input.Demo ? "RepSimulated" : "RepReadOnly")} · ");
            footer.AddPageField();
            footer.AddText(" / ");
            footer.AddNumPagesField();

            var s = input.State;
            Header(section, input);
            Summary(section, input);
            Values(section, s);

            if (s.CellVoltages is { Length: > 0 } cells)
            {
                Heading(section, T("CellVoltages"));
                Note(section, Loc.I.Format("RepCellsLine", cells.Length, N(cells.Min(), "F3"), Array.IndexOf(cells, cells.Min()) + 1,
                    N(cells.Max(), "F3"), Array.IndexOf(cells, cells.Max()) + 1, N((cells.Max() - cells.Min()) * 1000, "F0"), N(cells.Average(), "F3")));
                Grid(section, cells, input.CellColumns, "F3", 0.02, lowIsBad: true);
                Note(section, T("RepGridFromCar"));
            }
            if (s.CellSoh is { Length: > 0 } health)
            {
                Heading(section, T("CellHealth"));
                Note(section, Loc.I.Format("RepHealthLine", N(health.Min(), "F1"), Array.IndexOf(health, health.Min()) + 1, N(health.Average(), "F1")));
                Grid(section, health, input.CellColumns, "F1", 2, lowIsBad: true);
                Note(section, T("RepGridFromCar"));
            }
            if (s.ModuleTemps is { Length: > 0 } probes)
            {
                Heading(section, T("ModuleTemps"));
                Note(section, Loc.I.Format("RepProbesLine", probes.Length, N(probes.Min(), "F0"), N(probes.Max(), "F0")));
                Grid(section, probes, input.ProbeColumns, "F0", 4, lowIsBad: false);
                Note(section, T("RepGridFromCar"));
            }

            Drive(section, input);
            Lifetime(section, s);
            Faults(section, input);
            Notes(section, input);

            var renderer = new PdfDocumentRenderer { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(path);
        }

        // ---- sections ----------------------------------------------------------

        static void Header(Section section, Input input)
        {
            var title = section.AddParagraph(T("RepTitle"));
            title.Format.Font.Size = 20;
            title.Format.Font.Bold = true;
            title.Format.Font.Color = Accent;

            var car = section.AddParagraph(input.CarName);
            car.Format.Font.Size = 12;
            car.Format.Font.Bold = true;
            // ties the report to the car measured: the VIN can be compared with the car and its registration
            var vin = section.AddParagraph(input.Vin is string v
                ? (input.VinRead is DateTime read ? Loc.I.Format("RepVinRead", v, read.ToString("d", C)) : Loc.I.Format("RepVin", v))
                : T(input.VinAsked ? "RepVinNotGiven" : "RepVinNotRead"));
            vin.Format.Font.Size = 10;
            if (input.Vin == null) vin.Format.Font.Color = Info;
            if (!string.IsNullOrWhiteSpace(input.Note))
            {
                var note = section.AddParagraph(input.Note.Trim());
                note.Format.Font.Size = 11;
            }

            var s = input.State;
            var meta = section.AddParagraph();
            meta.Format.SpaceBefore = Unit.FromMillimeter(2);
            meta.Format.SpaceAfter = Unit.FromMillimeter(4);
            meta.Format.Font.Color = Info;
            meta.AddText(Loc.I.Format("RepReadAt", s.Time.ToString("g", C)));
            if (s.Odometer is double km) meta.AddText("   ·   " + Loc.I.Format("RepOdometer", km.ToString("N0", C)));
            if (input.Recording != null) meta.AddText("   ·   " + Loc.I.Format("RepFromRecording", input.Recording));
        }

        /// <summary>The rated items, worst decides the verdict. Public so the self-test can check the ratings.</summary>
        public static IReadOnlyList<SummaryRow> SummaryRows(Input input)
        {
            var s = input.State;
            double? spreadMv = s.CellVoltages is { Length: > 0 } c ? (c.Max() - c.Min()) * 1000 : s.CellSpread * 1000;
            double? tempSpread = s.ModuleTemps is { Length: > 0 } p ? p.Max() - p.Min() : s.TempMax - s.TempMin;
            var readings = new[] { s.IsolationPos, s.IsolationNeg }.OfType<double>().ToList();
            double? isolation = readings.Count > 0 ? readings.Min() : null;
            var read = input.Faults;
            var faults = read?.Rows;
            bool Battery(DtcRow f) => input.BatteryEcus.Contains(f.EcuId);
            int active = faults?.Count(f => Battery(f) && f.Severity == "ACTIVE") ?? 0;
            int stored = faults?.Count(f => Battery(f) && f.Severity == "stored") ?? 0;
            int otherActive = faults?.Count(f => !Battery(f) && f.Severity == "ACTIVE") ?? 0;
            int otherStored = faults?.Count(f => !Battery(f) && f.Severity == "stored") ?? 0;
            // a read from the car: every battery ECU must have answered; a saved file does not say who answered
            var silent = read == null || read.File != null ? new List<string>()
                : input.BatteryEcus.Where(id => !faults!.Any(f => f.Answered && string.Equals(f.EcuId, id, StringComparison.OrdinalIgnoreCase))).ToList();

            var rows = new List<(string Name, string Value, Grade? Grade, string Comment, Source Source)>();
            if (s.Soh is double soh)
                rows.Add((T("Soh"), $"{N(soh, "F1")} %", BatteryChecks.Soh(soh), T("RepSohComment"), Source.Car));
            // a car that gives cell voltages in coarse steps cannot tell a spread below one step from none
            double? step = input.Drive?.CellStepMv ?? (s.CellVoltages is { Length: > 1 } sc ? DriveStats.StepMv(sc) : null);
            bool coarse = step >= 5;
            string Spread(double v) => coarse && v < step ? Loc.I.Format("RepBelowStep", N(step, "F0")) : $"{N(v, "F0")} mV";
            string StepNote() => coarse ? " " + Loc.I.Format("RepCellStep", N(step, "F0")) : "";
            if (spreadMv is double mv)
            {
                // the current when the cells were read, not the latest one
                double? current = s.CellVoltages is { Length: > 0 } ? s.CellsCurrent : s.Current;
                var g = BatteryChecks.CellSpread(mv, current);
                if (g == Grade.Info && input.Drive?.LastRestSpread is double rest && input.Drive.LastRestTime is DateTime at)
                    // read under load now, but the session had a moment at rest: rate that one
                    rows.Add((T("RepCellSpread"), Spread(rest), BatteryChecks.CellSpread(rest, 0),
                        Loc.I.Format("RepSpreadFromRest", at.ToString("g", C)) + " " + T("RepSpreadComment") + StepNote(), Source.Calculated));
                else
                    rows.Add((T("RepCellSpread"), Spread(mv), g,
                        (g != Grade.Info ? T("RepSpreadComment") : current is null ? T("RepSpreadNoCurrent") : Loc.I.Format("RepSpreadLoaded", N(current, "F0"))) + StepNote(), Source.Calculated));
            }
            if (input.Drive is { } dr && dr.WeakestUnderLoad(1) is [var weak])
            {
                var g = BatteryChecks.CellUnderLoad(weak.ExtraPer100A, dr.LoadCurrentScaled, dr.NoiseFloorMv, dr.LoadTempMin);
                string cold = BatteryChecks.TooColdForLoad(dr.LoadTempMin) ? " " + Loc.I.Format("RepUnderLoadCold", N(dr.LoadTempMin, "F0"), N(BatteryChecks.CellLoadColdC, "F0")) : "";
                rows.Add(g == Grade.Good
                    ? (T("RepUnderLoad"), Loc.I.Format("RepUnderLoadNone", N(Math.Max(0, weak.ExtraPer100A), "F0")), g, T("RepUnderLoadNoneComment") + cold, Source.Calculated)
                    : (T("RepUnderLoad"), Loc.I.Format("RepUnderLoadValue", weak.Cell, N(weak.ExtraPer100A, "F0")), g, T("RepUnderLoadComment") + cold, Source.Calculated));
            }
            if (tempSpread is double dt)
                rows.Add((T("RepTempSpread"), $"{N(dt, "F0")} °C", BatteryChecks.TempSpread(dt), T("RepTempComment"), Source.Calculated));
            if (isolation is double k)
                rows.Add((T("Isolation"), $"{N(k, "N0")} kΩ", BatteryChecks.Isolation(k, s.Voltage), T("RepIsolationComment"), Source.Car));
            if (read == null)
                rows.Add((T("RepBatteryFaults"), T("RepNotRead"), null, T("RepFaultsNotRead"), Source.Car));
            else if ((silent.Count > 0 || !read.Complete) && active + stored == 0)
                // no answer is no finding: not rated, rather than "no faults"
                rows.Add((T("RepBatteryFaults"), T(silent.Count > 0 ? "RepNoAnswer" : "RepIncomplete"), null, T("RepBatteryNoAnswer"), Source.Car));
            else if (silent.Count > 0 || !read.Complete)
                // the faults that were found count, but the list may not be all of them
                rows.Add((T("RepBatteryFaults"), Loc.I.Format("RepFaultCount", active, stored), BatteryChecks.Faults(active, stored), T("RepFaultsPartial"), Source.Car));
            else if (read.File != null && active + stored == 0)
                rows.Add((T("RepBatteryFaults"), Loc.I.Format("RepFaultCount", 0, 0), Grade.Info, T("RepFaultsFromFile"), Source.Car));
            else
                rows.Add((T("RepBatteryFaults"), Loc.I.Format("RepFaultCount", active, stored), BatteryChecks.Faults(active, stored), T("RepFaultComment"), Source.Car));
            if (read != null)
            {
                if (otherActive + otherStored > 0)
                    rows.Add((T("RepOtherFaults"), Loc.I.Format("RepFaultCount", otherActive, otherStored), Grade.Info, T("RepOtherComment"), Source.Car));
            }
            return rows.Select(r => new SummaryRow(r.Name, r.Value, r.Grade, r.Comment, SourceText(r.Source))).ToList();
        }

        static void Summary(Section section, Input input)
        {
            var rows = SummaryRows(input);
            var overall = BatteryChecks.Overall(rows.Select(r => r.Grade));
            var verdict = section.AddParagraph();
            verdict.Format.SpaceAfter = Unit.FromMillimeter(2);
            verdict.Format.Font.Size = 12;
            verdict.Format.Font.Bold = true;
            verdict.Format.Font.Color = overall is Grade g0 ? Colour(g0) : Info;
            verdict.AddText(overall switch
            {
                Grade.Good => T("RepOverallGood"),
                Grade.Watch => T("RepOverallWatch"),
                Grade.Bad => T("RepOverallBad"),
                _ => T("RepOverallNone")
            });

            var table = NewTable(section, 3.9, 2.6, 1.7, 2.2, 7.0);
            Head(table, T("RepItem"), T("RepValue"), T("RepSourceCol"), T("RepGrade"), T("RepComment"));
            foreach (var (name, value, grade, comment, source) in rows)
            {
                var r = table.AddRow();
                r.Cells[0].AddParagraph(name);
                r.Cells[1].AddParagraph(value).Format.Font.Bold = true;
                SourceCell(r.Cells[2], source);
                var gp = r.Cells[3].AddParagraph(grade is Grade gr ? GradeText(gr) : "—");
                gp.Format.Font.Bold = true;
                gp.Format.Font.Color = grade is Grade gc ? Colour(gc) : Info;
                r.Cells[4].AddParagraph(comment).Format.Font.Size = 9;
            }
            Note(section, T("RepLimitsNote"));
        }

        /// <summary>When the session ran: "12.10.2026 6.01–6.29", with the end's date too when it ran past midnight.</summary>
        static string Span(DriveStats d) =>
            d.Start is not DateTime s || d.End is not DateTime e ? ""
            : s.Date == e.Date ? $"{s.ToString("d", C)} {s.ToString("t", C)}–{e.ToString("t", C)}"
            : $"{s.ToString("g", C)} – {e.ToString("g", C)}";

        /// <summary>The session behind the reading: distance, energy, loads, and the cells under load.</summary>
        static void Drive(Section section, Input input)
        {
            Heading(section, T("RepDrive"));
            var d = input.Drive;
            if (d == null || !d.HasData)
            {
                Note(section, T("RepNoDrive"));
                return;
            }
            var rows = new List<(string, string)>
            {
                (T("RepDriveTime"), Loc.I.Format("RepDriveTimeValue", N(d.Duration.TotalMinutes, d.Duration.TotalMinutes < 10 ? "F1" : "F0"), N(d.MovingSeconds / 60, d.Duration.TotalMinutes < 10 ? "F1" : "F0"), Span(d))),
                (T("RepDistance"), $"{N(d.Km, "F1")} km")
            };
            if (d.KwhOut > 0)
                rows.Add((T("RepDriveEnergy"), Loc.I.Format("RepDriveEnergyValue", N(d.KwhOut, "F2"), N(d.KwhRegen, "F2"),
                    N(d.KwhRegen / d.KwhOut * 100, "F0"))));
            if (d.Consumption is double use) rows.Add((T("RepConsumption"), $"{N(use, "F1")} kWh/100 km"));
            if (d.MaxCurrent != null || d.MaxPower != null)
                rows.Add((T("RepPeak"), Loc.I.Format("RepPeakValue", N(d.MaxCurrent, "F0"), N(d.MaxPower, "F0"), N(d.MaxRegenPower ?? 0, "F0"))));
            if (d.TempFirst != null)
                rows.Add((T("RepTempDrive"), Loc.I.Format("RepTempDriveValue", N(d.TempFirst, "F0"), N(d.TempLast, "F0"), N(d.TempHigh, "F0"))));
            if (d.RestReads > 0)
                rows.Add((T("RepSpreadRest"), Loc.I.Format("RepSpreadRestValue", N(d.RestSpreadMin, "F0"), N(d.RestSpreadMax, "F0"), N(d.RestSpreadAvg, "F1"), d.RestReads)));
            if (d.LoadReads > 0)
                rows.Add((T("RepSpreadLoad"), Loc.I.Format("RepSpreadLoadValue", N(d.LoadSpreadAvg, "F1"), N(d.LoadSpreadMax, "F0"), d.LoadReads, N(d.LoadCurrentMax, "F0"))));
            // all of it worked out of the session's readings
            SourceTable(section, rows.Select(r => (r.Item1, (string?)r.Item2, Source.Calculated)));

            Heading(section, T("RepCellsUnderLoad"));
            var weakest = d.WeakestUnderLoad(5);
            if (weakest.Count == 0)
            {
                Note(section, Loc.I.Format("RepFewLoadReads", DriveStats.MinLoadReads, N(DriveStats.LoadCurrentA, "F0"), DriveStats.MinRestReads));
                return;
            }
            var health = input.State.CellSoh;
            // left to right the sum: below the average under load, minus below it at rest, is the extra under load;
            // the health column only where the reading has cell health
            var cells = health != null ? NewTable(section, 1.6, 2.8, 3.4, 3.4, 3.2, 3.0) : NewTable(section, 2.0, 3.2, 4.2, 4.2, 3.8);
            var titles = new List<string> { T("Cell"), T("RepPer100A"), T("RepBelowUnderLoad"), T("RepBelowAtRest"), T("RepExtraUnderLoad") };
            if (health != null) titles.Add(T("CellHealth"));
            Head(cells, titles.ToArray());
            foreach (var c in weakest)
            {
                var r = cells.AddRow();
                int col = 0;
                r.Cells[col++].AddParagraph(c.Cell.ToString(C)).Format.Font.Bold = true;
                var per100 = r.Cells[col++].AddParagraph($"{(c.ExtraPer100A >= 0 ? "+" : "")}{N(c.ExtraPer100A, "F1")}");
                per100.Format.Font.Bold = true;
                if (BatteryChecks.CellUnderLoad(c.ExtraPer100A, d.LoadCurrentScaled, d.NoiseFloorMv, d.LoadTempMin) is Grade g) per100.Format.Font.Color = Colour(g);
                r.Cells[col++].AddParagraph($"{N(c.LoadMv, "F1")} mV");
                r.Cells[col++].AddParagraph($"{N(c.RestMv, "F1")} mV");
                r.Cells[col++].AddParagraph($"{(c.ExtraMv >= 0 ? "+" : "")}{N(c.ExtraMv, "F1")} mV");
                if (health != null) r.Cells[col].AddParagraph(c.Cell <= health.Length ? $"{N(health[c.Cell - 1], "F1")} %" : "—");
            }
            cells.Rows[0].KeepWith = cells.Rows.Count - 1;     // the few rows stay on one page
            Note(section, T("RepUnderLoadCalculated"));
            Note(section, Loc.I.Format("RepSagHead", d.LoadReads, N(DriveStats.LoadCurrentA, "F0"), d.RestReads));
            foreach (var key in new[] { "RepSagLoad", "RepSagRest", "RepSagExtra", "RepSagPer100" }) Bullet(section, T(key));
            Bullet(section, Loc.I.Format("RepSagScale", N(d.TypicalExtraMv, "F1"), N(d.NoiseFloorMv, "F0")));
            if (d.CellStepMv >= 5) Bullet(section, Loc.I.Format("RepCellStepLoad", N(d.CellStepMv, "F0"), N(d.NoiseFloorMv, "F0")));
            Note(section, T("RepSagHealth"));
            double watch = BatteryChecks.CellLoadWatchAt(d.LoadCurrentScaled, d.NoiseFloorMv), bad = BatteryChecks.CellLoadBadAt(d.LoadCurrentScaled, d.NoiseFloorMv);
            LimitTable(section, T("RepLoadLimitsTitle"),
                (Grade.Good, Loc.I.Format("RepBelow", N(watch, "F0"))),
                (Grade.Watch, $"{N(watch, "F0")}–{N(bad, "F0")}"),
                (Grade.Bad, Loc.I.Format("RepFromUp", N(bad, "F0"))));
            Note(section, Loc.I.Format("RepLoadLimitsNote", N(d.LoadCurrentScaled, "F0"),
                N(BatteryChecks.CellLoadWatchMv, "F0"), N(BatteryChecks.CellLoadBadMv, "F0")));
            if (BatteryChecks.TooColdForLoad(d.LoadTempMin))
                Note(section, Loc.I.Format("RepUnderLoadCold", N(d.LoadTempMin, "F0"), N(BatteryChecks.CellLoadColdC, "F0")));
            // low at rest is a matter of charge, shown apart so it is not read as resistance
            var restLow = d.LowestAtRest(3).Where(x => x.Mv >= d.NoiseFloorMv).ToList();
            if (restLow.Count > 0)
                Note(section, Loc.I.Format("RepLowAtRest", string.Join(", ", restLow.Select(x => $"{x.Cell} ({N(x.Mv, "F0")} mV)"))));
        }

        /// <summary>The battery's values in groups: charge and health, the pack's age and versions, electrics, temperatures, insulation and 12 V, charging.</summary>
        static void Values(Section section, VehicleState s)
        {
            var rows = new List<(string, string?, Source)>();
            var group = new List<(string, string?, Source)>();
            void Add(string key, double? v, string format, string unit, Source source = Source.Car)
            {
                if (v != null) group.Add((T(key), $"{N(v, format)} {unit}".Trim(), source));
            }
            // a group's heading only when the car gave something for it
            void Group(string key)
            {
                if (group.Count > 0) { rows.Add((T(key), null, Source.Car)); rows.AddRange(group); }
                group.Clear();
            }

            Add("Soc", s.Soc, "F1", "%");
            Add("RepEnergy", s.Energy, "F1", "kWh");
            Add("Soh", s.Soh, "F1", "%");
            Group("RepGroupCharge");

            // details kept from an earlier connection say when they were read, as the VIN does
            string Kept() => s.BatteryInfoRead is DateTime at ? "   " + Loc.I.Format("RepReadOn", at.ToString("d", C)) : "";
            if (s.BatteryMade is DateTime made)
            {
                group.Add((T("RepBatteryMade"), made.ToString("d", C) + Kept(), Source.Car));
                double years = (s.Time - made).TotalDays / 365.25;
                if (years > 0)
                {
                    group.Add((T("RepBatteryAge"), Loc.I.Format("RepAgeYears", N(years, "F1")), Source.Calculated));
                    if (s.Soh is double soh && soh <= 100 && years >= BatteryChecks.WearMinYears)
                        group.Add((T("RepWear"), s.Odometer is double km && km >= 1000
                            ? Loc.I.Format("RepWearValueKm", N((100 - soh) / years, "F1"), N((100 - soh) / km * 10000, "F1"))
                            : Loc.I.Format("RepWearValue", N((100 - soh) / years, "F1")), Source.Calculated));
                }
            }
            if (s.BmsSoftware != null) group.Add((T("RepBmsSoftware"), s.BmsSoftware + Kept(), Source.Car));
            if (s.BmsHardware != null) group.Add((T("RepBmsHardware"), s.BmsHardware + Kept(), Source.Car));
            Group("RepGroupPack");

            Add("Voltage", s.Voltage, "F1", "V");
            Add("Current", s.Current, "F1", "A");
            Add("Power", s.Power, "F1", "kW", Source.Calculated);
            Add("RepMaxCharge", s.MaxChargePower, "F1", "kW");
            Add("RepMaxDischarge", s.MaxDischargePower, "F1", "kW");
            // with every cell listed below, the separately read min/max (from another moment) would only confuse
            if (s.CellVoltages is not { Length: > 0 } && (s.CellMin != null || s.CellMax != null))
                group.Add((T("RepCellMinMax"), $"{N(s.CellMin, "F3")} / {N(s.CellMax, "F3")} V"
                    + (s.LowestCell is int n ? "   " + Loc.I.Format("RepLowestCell", n) : ""), Source.Car));
            Group("RepGroupElectric");

            if (s.TempMin != null || s.TempMax != null)
                group.Add((T("TempMinMax"), $"{N(s.TempMin, "F0")} / {N(s.TempMax, "F0")} °C", Source.Car));
            // the car's average, else the average of the probes
            if (s.TempAvg != null) Add("RepTempAvg", s.TempAvg, "F0", "°C");
            else if (s.ModuleTemps is { Length: > 0 } probes) Add("RepTempAvg", probes.Average(), "F1", "°C", Source.Calculated);
            Add("Ambient", s.Ambient, "F0", "°C");
            Group("RepGroupTemps");

            // both sides where the car gives them, else the one value it gives, without a side
            if (s.IsolationPos != null && s.IsolationNeg != null)
                group.Add((T("Isolation"), $"+ {N(s.IsolationPos, "N0")} / − {N(s.IsolationNeg, "N0")} kΩ", Source.Car));
            else if ((s.IsolationPos ?? s.IsolationNeg) is double one)
                group.Add((T("Isolation"), $"{N(one, "N0")} kΩ", Source.Car));
            // a rest voltage says roughly how full the 12 V battery is; at the DC/DC's voltage it says nothing about that
            if (s.AuxVoltage is double aux)
                group.Add((T("RepAux"), $"{N(aux, "F2")} V" + (AuxAtRest(s) ? $"   ({T("RepAuxRest")})"
                    : aux >= BatteryChecks.AuxRestBelowV ? $"   ({T("RepAuxCharging")})" : ""), Source.Car));
            Add("RepAuxTemp", s.AuxBatteryTemp, "F0", "°C");
            Group("RepGroupSafety");

            if (s.Charging is bool ch) group.Add((T("RepCharging"), T(ch ? "Yes" : "No"), Source.Car));
            // with the cable in, the charger's temperature tells how hard it works
            if (s.Charging == true) Add("RepChargerTemp", s.ChargerTemp, "F0", "°C");
            Group("RepGroupCharging");
            if (rows.Count == 0) return;

            Heading(section, T("RepValues"));
            SourceTable(section, rows);
            if (AuxAtRest(s))
                Note(section, Loc.I.Format("RepAuxRestNote", N(BatteryChecks.AuxFullV, "F2"), N(BatteryChecks.AuxHalfV, "F2")));
        }

        /// <summary>The 12 V reading is a rest voltage: below the DC/DC's charging voltage, the car standing and the pack current at rest level.</summary>
        static bool AuxAtRest(VehicleState s) =>
            s.AuxVoltage < BatteryChecks.AuxRestBelowV && (s.Speed ?? 0) < 1 && Math.Abs(s.Current ?? 0) <= BatteryChecks.RestCurrentA;

        /// <summary>Name, value and where the value comes from; a row without a value is a group heading across the table.</summary>
        static Table SourceTable(Section section, IEnumerable<(string Name, string? Value, Source Source)> rows)
        {
            var table = NewTable(section, 5.5, 9.6, 2.3);
            Head(table, T("RepItem"), T("RepValue"), T("RepSourceCol"));
            foreach (var (name, value, source) in rows)
            {
                var r = table.AddRow();
                if (value == null)
                {
                    r.Cells[0].MergeRight = 2;
                    var g = r.Cells[0].AddParagraph(name);
                    g.Format.Font.Bold = true;
                    g.Format.Font.Color = Accent;
                    r.KeepWith = 1;         // the heading stays with its first row
                    continue;
                }
                r.Cells[0].AddParagraph(name);
                r.Cells[1].AddParagraph(value).Format.Font.Bold = true;
                SourceCell(r.Cells[2], SourceText(source));
            }
            return table;
        }

        static string SourceText(Source source) => T(source == Source.Car ? "RepSrcCar" : "RepSrcCalculated");

        static void SourceCell(Cell cell, string text)
        {
            var p = cell.AddParagraph(text);
            p.Format.Font.Size = 9;
            p.Format.Font.Color = Info;
        }

        /// <summary>The car's own counters over its life, and what follows from them; left out for a car that gives none.</summary>
        static void Lifetime(Section section, VehicleState s)
        {
            if (!s.HasLifetimeCounters) return;
            var rows = new List<(string, string?, Source)>();
            string Kwh(double? v) => $"{N(v, "N0")} kWh";
            if (s.LifetimeChargedKwh != null) rows.Add((T("RepLifeIn"), Kwh(s.LifetimeChargedKwh), Source.Car));
            if (s.LifetimeDischargedKwh != null) rows.Add((T("RepLifeOut"), Kwh(s.LifetimeDischargedKwh), Source.Car));
            if (s.AcCharges != null || s.DcCharges != null)
                rows.Add((T("RepLifeCharges"), Loc.I.Format("RepLifeAcDc", N(s.AcCharges, "N0"), N(s.DcCharges, "N0")), Source.Car));
            if (s.AcChargedKwh != null || s.DcChargedKwh != null)
                rows.Add((T("RepLifeCharged"), Loc.I.Format("RepLifeAcDc", Kwh(s.AcChargedKwh), Kwh(s.DcChargedKwh)), Source.Car));
            // fast charging's share of the energy charged, and the energy charged per 100 km driven
            if (s.AcChargedKwh is double ac && s.DcChargedKwh is double dc && ac + dc > 0)
            {
                rows.Add((T("RepLifeDcShare"), $"{N(dc / (ac + dc) * 100, "F0")} %", Source.Calculated));
                if (s.Odometer is double km && km >= 100)
                    rows.Add((T("RepLifePer100"), $"{N((ac + dc) / km * 100, "F1")} kWh/100 km", Source.Calculated));
            }
            if (s.ContactorCloses != null) rows.Add((T("RepLifeStarts"), N(s.ContactorCloses, "N0"), Source.Car));
            if (s.TimeAbove55 is double hot)
                rows.Add((T("RepLifeHot"), hot == 0 ? T("RepLifeHotNever") : Loc.I.Format("RepLifeHotValue", N(hot, "N0")), Source.Car));
            Heading(section, T("RepLifetime"));
            var table = SourceTable(section, rows);
            table.Rows[0].KeepWith = table.Rows.Count - 1;     // the few rows stay on one page
            // the energy and charge counters need their explanation; starts and heat do not
            bool energy = s.LifetimeChargedKwh != null || s.LifetimeDischargedKwh != null || s.AcCharges != null || s.DcCharges != null
                          || s.AcChargedKwh != null || s.DcChargedKwh != null;
            Note(section, T(energy ? "RepLifeNote" : "RepLifeNoteShort"));
        }

        /// <summary>Cells or probes in rows, coloured like the battery view: red at the low end, green at the high end (heat: blue to red).</summary>
        static void Grid(Section section, double[] values, int columns, string format, double minimumSpan, bool lowIsBad)
        {
            columns = Math.Clamp(columns, 4, 20);
            double width = 17.4 / columns;
            var table = section.AddTable();
            table.Borders.Color = new MColor(0xFF, 0xFF, 0xFF);
            table.Borders.Width = 1;
            table.Format.Alignment = ParagraphAlignment.Center;
            for (int i = 0; i < columns; i++) table.AddColumn(Unit.FromCentimeter(width));

            double min = values.Min(), max = values.Max();
            double span = Math.Max(max - min, minimumSpan);
            double lo = (min + max) / 2 - span / 2;
            int lowest = Array.IndexOf(values, min), highest = Array.IndexOf(values, max);

            for (int start = 0; start < values.Length; start += columns)
            {
                var number = table.AddRow();
                var value = table.AddRow();
                number.KeepWith = 1;                // a cell number never parts from its value at a page break
                number.Format.Font.Size = 7;
                number.Format.Font.Color = Info;
                value.Format.Font.Size = columns > 14 ? 8.5 : 10;
                for (int c = 0; c < columns && start + c < values.Length; c++)
                {
                    int i = start + c;
                    number.Cells[c].AddParagraph((i + 1).ToString(C));
                    var p = value.Cells[c].AddParagraph(values[i].ToString(format, C));
                    if (i == lowest || i == highest) p.Format.Font.Bold = true;
                    double f = Math.Clamp((values[i] - lo) / span, 0, 1);
                    value.Cells[c].Shading.Color = Shade(f, lowIsBad);
                }
            }
            section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1);
        }

        static void Faults(Section section, Input input)
        {
            Heading(section, T("TabDtc"));
            if (input.Faults is not { } read)
            {
                Note(section, T("RepFaultsNotRead"));
                return;
            }
            // when and from where: a saved read may be older than the reading above
            Note(section, read.File != null
                ? Loc.I.Format("RepFaultsSaved", System.IO.Path.GetFileName(read.File), read.When.ToString("g", C))
                : Loc.I.Format("RepFaultsReadAt", read.When.ToString("g", C)) + (read.Complete ? "" : " " + T("RepIncompleteNote")));
            var silent = read.Rows.Where(f => !f.Answered).Select(f => f.Ecu).Distinct().ToList();
            if (silent.Count > 0) Note(section, Loc.I.Format("RepSilentEcus", string.Join(", ", silent)));
            var faults = read.Rows.Where(f => f.Severity is "ACTIVE" or "stored").ToList();
            if (faults.Count == 0)
            {
                // after "did not answer" or a cut-short read, "no faults" would claim too much
                if (read.Complete && silent.Count == 0) Note(section, T("RepNoFaults"));
                return;
            }
            Note(section, T("RepFaultsFromCar"));
            var table = NewTable(section, 3.6, 2.4, 2.0, 6.0, 3.4);
            Head(table, T("RepEcu"), T("RepCode"), T("RepState"), T("RepMeaning"), T("RepOccurred"));
            foreach (var f in faults)
            {
                var r = table.AddRow();
                r.Format.Font.Size = 9;
                r.Cells[0].AddParagraph(f.Ecu);
                r.Cells[1].AddParagraph(f.Code).Format.Font.Bold = true;
                if (f.FailureTypeShown.Length > 0) r.Cells[1].AddParagraph(f.FailureTypeShown).Format.Font.Size = 7;
                var st = r.Cells[2].AddParagraph(f.Severity == "ACTIVE" ? T("RepActive") : T("RepStored"));
                st.Format.Font.Color = f.Severity == "ACTIVE" ? Bad : Watch;
                r.Cells[3].AddParagraph(f.StatusTextShown);
                if (f.StatusMeaningShown.Length > 0) r.Cells[3].AddParagraph(f.StatusMeaningShown).Format.Font.Size = 7;
                r.Cells[4].AddParagraph(f.Occurred);
            }
        }

        /// <summary>How each figure the program works out is calculated, and every limit with where it comes from.</summary>
        static void Formulas(Section section, double? volts, string? warranty)
        {
            Heading(section, T("RepHowTitle"));
            var formulas = NewTable(section, 5.5, 11.9);
            Head(formulas, T("RepItem"), T("RepHowCol"));
            // each figure's name and its calculation, which may name the load and rest currents
            var keys = new[]
            {
                ("RepHowPower", "RepHowPowerF"), ("RepHowCellSpread", "RepHowCellSpreadF"), ("RepHowTempSpread", "RepHowTempSpreadF"),
                ("RepHowDistance", "RepHowDistanceF"), ("RepHowEnergy", "RepHowEnergyF"), ("RepHowConsumption", "RepHowConsumptionF"),
                ("RepHowBelowLoad", "RepHowBelowLoadF"), ("RepHowBelowRest", "RepHowBelowRestF"), ("RepHowExtra", "RepHowExtraF"),
                ("RepHowPer100", "RepHowPer100F"), ("RepHowDcShare", "RepHowDcShareF"), ("RepHowPer100Km", "RepHowPer100KmF"),
                ("RepHowTempAvg", "RepHowTempAvgF"), ("RepHowAge", "RepHowAgeF"), ("RepHowWear", "RepHowWearF")
            };
            foreach (var (name, formula) in keys)
            {
                var r = formulas.AddRow();
                r.Cells[0].AddParagraph(T(name));
                r.Cells[1].AddParagraph(Loc.I.Format(formula, N(DriveStats.LoadCurrentA, "F0"), N(BatteryChecks.RestCurrentA, "F0"))).Format.Font.Size = 9;
            }

            Heading(section, T("RepLimitsTitle"));
            var limits = NewTable(section, 3.4, 2.1, 2.5, 2.4, 7.0);
            Head(limits, T("RepItem"), T("RepGood"), T("RepWatch"), T("RepBad"), T("RepBasis"));
            void Row(string item, string good, string watch, string bad, string basis)
            {
                var r = limits.AddRow();
                r.Cells[0].AddParagraph(item);
                foreach (var (cell, text, g) in new[] { (1, good, Grade.Good), (2, watch, Grade.Watch), (3, bad, Grade.Bad) })
                    r.Cells[cell].AddParagraph(text).Format.Font.Color = Colour(g);
                r.Cells[4].AddParagraph(basis).Format.Font.Size = 9;
            }
            Row(T("Soh"), Loc.I.Format("RepFromUp", $"{N(BatteryChecks.SohGood, "F0")} %"), $"{N(BatteryChecks.SohWatch, "F0")}–{N(BatteryChecks.SohGood, "F0")} %",
                Loc.I.Format("RepBelow", $"{N(BatteryChecks.SohWatch, "F0")} %"),
                // the maker's warranty comes from the car file: each car shows its own
                warranty is string w ? Loc.I.Format("RepBasisSohWarranty", w) : T("RepBasisSoh"));
            Row(T("RepCellSpread"), Loc.I.Format("RepBelow", $"{N(BatteryChecks.SpreadWatchMv, "F0")} mV"), $"{N(BatteryChecks.SpreadWatchMv, "F0")}–{N(BatteryChecks.SpreadBadMv, "F0")} mV",
                Loc.I.Format("RepFromUp", $"{N(BatteryChecks.SpreadBadMv, "F0")} mV"), T("RepBasisSpread"));
            Row(T("RepUnderLoad"), Loc.I.Format("RepBelow", N(BatteryChecks.CellLoadWatchMv, "F0")), $"{N(BatteryChecks.CellLoadWatchMv, "F0")}–{N(BatteryChecks.CellLoadBadMv, "F0")}",
                Loc.I.Format("RepFromUp", N(BatteryChecks.CellLoadBadMv, "F0")), Loc.I.Format("RepBasisLoad", N(DriveStats.NoiseMv, "F0"), N(BatteryChecks.CellLoadColdC, "F0")));
            Row(T("RepTempSpread"), Loc.I.Format("RepBelow", $"{N(BatteryChecks.TempWatchC, "F0")} °C"), $"{N(BatteryChecks.TempWatchC, "F0")}–{N(BatteryChecks.TempBadC, "F0")} °C",
                Loc.I.Format("RepFromUp", $"{N(BatteryChecks.TempBadC, "F0")} °C"), T("RepBasisTemp"));
            double min = BatteryChecks.IsolationMinKOhm(volts);
            double usedVolts = min * 1000 / BatteryChecks.IsolationOhmPerVolt;
            // over 1000 V the legal minimum is above the program's own limit: Good starts at the minimum, no Watch band
            double goodFrom = BatteryChecks.IsolationGoodFromKOhm(volts);
            Row(T("Isolation"), Loc.I.Format("RepFromUp", $"{N(goodFrom, "N0")} kΩ"), min < goodFrom ? $"{N(min, "N0")}–{N(goodFrom, "N0")} kΩ" : "–",
                Loc.I.Format("RepBelow", $"{N(min, "N0")} kΩ"),
                Loc.I.Format("RepBasisIsolation", N(BatteryChecks.IsolationOhmPerVolt, "F0"), N(usedVolts, "F0"), N(min, "N0"), N(BatteryChecks.IsolationGoodKOhm, "N0")));
            Row(T("RepBatteryFaults"), T("RepNoFaultsShort"), T("RepStored"), T("RepActive"), T("RepBasisFaults"));
        }

        static void Notes(Section section, Input input)
        {
            Formulas(section, input.State.Voltage, input.Warranty);

            // what the car tells is apart from what the program works out of it
            Heading(section, T("RepSourcesTitle"));
            var groups = new[]
            {
                ("RepFromCarHead", "RepFromCarIntro", "RepFromCarList"),
                ("RepCalculatedHead", "RepCalculatedIntro", "RepCalculatedList"),
                ("RepEstimatedHead", "RepEstimatedIntro", "RepEstimatedList")
            };
            foreach (var (head, intro, list) in groups)
            {
                SubHeading(section, T(head));
                Note(section, T(intro));
                // one line per group of figures, separated by | in the text
                foreach (var line in T(list).Split('|')) Bullet(section, line.Trim(), labelled: true);
            }
            Note(section, Loc.I.Format("RepSource", input.CarFile)).Format.SpaceBefore = Unit.FromMillimeter(3);

            Heading(section, T("RepAbout"));
            if (!string.IsNullOrWhiteSpace(input.CarDetails)) Note(section, input.CarDetails!);
            Note(section, Loc.I.Format("RepMadeBy", Version(), DateTime.Now.ToString("g", C)));
            Note(section, T("RepDisclaimer"));
            if (!input.Demo) Note(section, T("RepBuyer"));
        }

        // ---- building blocks ---------------------------------------------------

        static void Heading(Section section, string text)
        {
            var p = section.AddParagraph(text);
            p.Format.Font.Size = 12;
            p.Format.Font.Bold = true;
            p.Format.SpaceBefore = Unit.FromMillimeter(5);
            p.Format.SpaceAfter = Unit.FromMillimeter(2);
            p.Format.KeepWithNext = true;
        }

        static void SubHeading(Section section, string text)
        {
            var p = section.AddParagraph(text);
            p.Format.Font.Size = 10;
            p.Format.Font.Bold = true;
            p.Format.SpaceBefore = Unit.FromMillimeter(2.5);
            p.Format.KeepWithNext = true;
        }

        static Paragraph Note(Section section, string text)
        {
            var p = section.AddParagraph(text);
            p.Format.Font.Size = 9;
            p.Format.Font.Color = Info;
            p.Format.SpaceBefore = Unit.FromMillimeter(1);
            p.Format.SpaceAfter = Unit.FromMillimeter(1.5);
            return p;
        }

        /// <summary>A bullet line; with <paramref name="labelled"/> the text up to the first colon is bold.</summary>
        static void Bullet(Section section, string text, bool labelled = false)
        {
            var p = section.AddParagraph("•\t");
            int colon = labelled ? text.IndexOf(':') : -1;
            if (colon > 0)
            {
                p.AddFormattedText(text[..(colon + 1)], TextFormat.Bold);
                p.AddText(text[(colon + 1)..]);
            }
            else p.AddText(text);
            p.Format.Font.Size = 9;
            p.Format.Font.Color = Info;
            p.Format.LeftIndent = Unit.FromMillimeter(4);
            p.Format.FirstLineIndent = Unit.FromMillimeter(-4);
            p.Format.TabStops.AddTabStop(Unit.FromMillimeter(4));
            p.Format.SpaceAfter = Unit.FromMillimeter(0.8);
        }

        /// <summary>The colour limits one under the other: the grade in its colour, then the range.</summary>
        static void LimitTable(Section section, string title, params (Grade Grade, string Range)[] rows)
        {
            var table = NewTable(section, 3.0, 4.0);
            Head(table, title, "");
            table.Rows[0].Cells[0].MergeRight = 1;
            foreach (var (grade, range) in rows)
            {
                var r = table.AddRow();
                var g = r.Cells[0].AddParagraph(GradeText(grade));
                g.Format.Font.Bold = true;
                g.Format.Font.Color = Colour(grade);
                r.Cells[1].AddParagraph(range);
            }
            table.Rows[0].KeepWith = table.Rows.Count - 1;
            section.AddParagraph().Format.SpaceAfter = Unit.FromMillimeter(1);
        }

        static Table NewTable(Section section, params double[] widthsCm)
        {
            var table = section.AddTable();
            table.Borders.Color = Line;
            table.Borders.Width = 0.5;
            table.LeftPadding = table.RightPadding = Unit.FromMillimeter(1.5);
            table.TopPadding = table.BottomPadding = Unit.FromMillimeter(0.8);
            foreach (var w in widthsCm) table.AddColumn(Unit.FromCentimeter(w));
            return table;
        }

        static void Head(Table table, params string[] titles)
        {
            var r = table.AddRow();
            r.HeadingFormat = true;
            r.Shading.Color = HeaderFill;
            r.Format.Font.Bold = true;
            for (int i = 0; i < titles.Length; i++) r.Cells[i].AddParagraph(titles[i]);
        }

        static MColor Colour(Grade g) => g switch
        {
            Grade.Good => Good,
            Grade.Watch => Watch,
            Grade.Bad => Bad,
            _ => Info
        };

        static string GradeText(Grade g) => T(g switch
        {
            Grade.Good => "RepGood",
            Grade.Watch => "RepWatch",
            Grade.Bad => "RepBad",
            _ => "RepInfo"
        });

        /// <summary>Light tints, so the numbers stay readable on paper.</summary>
        static MColor Shade(double f, bool lowIsBad)
        {
            var (a, b, c) = lowIsBad
                ? ((0xFC, 0xA5, 0xA5), (0xFD, 0xE6, 0x8A), (0xBB, 0xF7, 0xD0))
                : ((0xBF, 0xDB, 0xFE), (0xBB, 0xF7, 0xD0), (0xFC, 0xA5, 0xA5));
            var (x, y, t) = f < 0.5 ? (a, b, f * 2) : (b, c, (f - 0.5) * 2);
            static byte L(int p, int q, double t) => (byte)Math.Round(p + (q - p) * t);
            return new MColor(L(x.Item1, y.Item1, t), L(x.Item2, y.Item2, t), L(x.Item3, y.Item3, t));
        }

        static string Version()
        {
            var v = typeof(BatteryReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }
}
