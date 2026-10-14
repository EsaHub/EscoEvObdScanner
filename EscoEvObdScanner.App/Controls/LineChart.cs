using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EscoEvObdScanner.AppLogic.ViewModels;

namespace EscoEvObdScanner.App.Controls
{
    /// <summary>
    /// A rolling time chart. All series share one auto-scaled axis, so each
    /// chart groups values of the same unit. Redraws when <see cref="Revision"/>
    /// changes, which the view model bumps after adding points.
    /// </summary>
    public sealed class LineChart : FrameworkElement
    {
        public static readonly DependencyProperty SeriesProperty = Reg(nameof(Series), typeof(IList<ChartSeries>), null);
        public static readonly DependencyProperty RevisionProperty = Reg(nameof(Revision), typeof(int), 0);
        public static readonly DependencyProperty TitleProperty = Reg(nameof(Title), typeof(string), "");
        public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(string), "");
        public static readonly DependencyProperty WindowSecondsProperty = Reg(nameof(WindowSeconds), typeof(double), 300.0);
        public static readonly DependencyProperty ZeroLineProperty = Reg(nameof(ZeroLine), typeof(bool), false);

        static DependencyProperty Reg(string name, Type type, object? def) =>
            DependencyProperty.Register(name, type, typeof(LineChart),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public IList<ChartSeries>? Series { get => (IList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
        public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public double WindowSeconds { get => (double)GetValue(WindowSecondsProperty); set => SetValue(WindowSecondsProperty, value); }
        /// <summary>Always include zero in the scale and draw it, for values that change sign.</summary>
        public bool ZeroLine { get => (bool)GetValue(ZeroLineProperty); set => SetValue(ZeroLineProperty, value); }

        static readonly Brush Background = Frozen(Color.FromRgb(0x12, 0x18, 0x21));
        static readonly Brush Grid = Frozen(Color.FromRgb(0x1E, 0x27, 0x33));
        static readonly Brush ZeroBrush = Frozen(Color.FromRgb(0x3A, 0x47, 0x56));
        static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE6, 0xED, 0xF3));
        static readonly Brush Muted = Frozen(Color.FromRgb(0x8B, 0x98, 0xA5));
        static readonly Typeface Face = new("Segoe UI");
        static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        static readonly Dictionary<uint, Brush> SeriesBrushes = new();

        static Brush BrushOf(ChartSeries s)
        {
            if (!SeriesBrushes.TryGetValue(s.Rgb, out var b))
                SeriesBrushes[s.Rgb] = b = Frozen(Color.FromRgb((byte)(s.Rgb >> 16), (byte)(s.Rgb >> 8), (byte)s.Rgb));
            return b;
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 400 : available.Width,
                double.IsInfinity(available.Height) ? 160 : available.Height);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 80 || h < 60) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            dc.DrawRoundedRectangle(Background, new Pen(Grid, 1), new Rect(0.5, 0.5, w - 1, h - 1), 8, 8);

            const double left = 52, right = 12, top = 28, bottom = 22;
            var plot = new Rect(left, top, Math.Max(10, w - left - right), Math.Max(10, h - top - bottom));

            var title = Text(Title + (string.IsNullOrEmpty(Unit) ? "" : $"  ({Unit})"), Bold, 12, TextBrush, dpi);
            dc.DrawText(title, new Point(12, 7));

            var series = Series?.Where(s => s.Points.Count > 0).ToList() ?? new();
            if (series.Count == 0) return;

            double tMax = series.Max(s => s.Points[^1].X);
            double tMin = tMax - WindowSeconds;
            var visible = series.SelectMany(s => s.Points.Where(p => p.X >= tMin)).ToList();
            if (visible.Count == 0) return;

            double yMin = visible.Min(p => p.Y), yMax = visible.Max(p => p.Y);
            if (ZeroLine) { yMin = Math.Min(yMin, 0); yMax = Math.Max(yMax, 0); }
            if (yMax - yMin < 1e-6) { yMin -= 1; yMax += 1; }
            double pad = (yMax - yMin) * 0.08;
            yMin -= pad; yMax += pad;

            double X(double t) => plot.Left + (t - tMin) / WindowSeconds * plot.Width;
            double Y(double v) => plot.Bottom - (v - yMin) / (yMax - yMin) * plot.Height;

            // horizontal grid with labels; enough decimals that neighbouring labels differ
            var gridPen = new Pen(Grid, 1);
            double labelStep = (yMax - yMin) / 4;
            string labelFormat = labelStep >= 5 ? "F0" : labelStep >= 0.5 ? "F1" : labelStep >= 0.05 ? "F2" : "F3";
            for (int i = 0; i <= 4; i++)
            {
                double v = yMin + (yMax - yMin) * i / 4;
                double y = Math.Round(Y(v)) + 0.5;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var label = Text(v.ToString(labelFormat, CultureInfo.CurrentCulture), Face, 10, Muted, dpi);
                dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
            }

            // vertical grid every 15 s, 1 min or 5 min depending on the window, labelled back from now
            double step = WindowSeconds <= 120 ? 15 : WindowSeconds <= 600 ? 60 : 300;
            for (double t = tMax; t >= tMin - 1e-6; t -= step)
            {
                double x = Math.Round(X(t)) + 0.5;
                dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                double ago = tMax - t;
                string s = ago < 1 ? "0" : ago < 60 ? $"-{ago:0}s" : $"-{ago / 60:0.#}min";
                var label = Text(s, Face, 10, Muted, dpi);
                dc.DrawText(label, new Point(Math.Min(x - label.Width / 2, plot.Right - label.Width), plot.Bottom + 4));
            }

            if (ZeroLine && yMin < 0 && yMax > 0)
            {
                double y0 = Math.Round(Y(0)) + 0.5;
                dc.DrawLine(new Pen(ZeroBrush, 1) { DashStyle = DashStyles.Dash }, new Point(plot.Left, y0), new Point(plot.Right, y0));
            }

            // lines
            dc.PushClip(new RectangleGeometry(plot));
            foreach (var s in series)
            {
                var pts = s.Points.Where(p => p.X >= tMin - step).ToList();
                if (pts.Count < 2) continue;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(X(pts[0].X), Y(pts[0].Y)), false, false);
                    ctx.PolyLineTo(pts.Skip(1).Select(p => new Point(X(p.X), Y(p.Y))).ToList(), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, new Pen(BrushOf(s), 2) { LineJoin = PenLineJoin.Round }, g);
            }
            dc.Pop();

            // legend with the latest value, right-aligned in the title row
            double lx = w - 12;
            foreach (var s in Enumerable.Reverse(series))
            {
                var t = Text($"{s.Title} {s.Points[^1].Y.ToString(s.Format, CultureInfo.CurrentCulture)}", Face, 11, BrushOf(s), dpi);
                lx -= t.Width;
                dc.DrawText(t, new Point(lx, 8));
                lx -= 14;
            }
        }

        static FormattedText Text(string s, Typeface face, double size, Brush brush, double dpi) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush, dpi);
    }
}
