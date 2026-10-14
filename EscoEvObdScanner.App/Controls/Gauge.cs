using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EscoEvObdScanner.App.Controls
{
    /// <summary>
    /// A round 270° gauge. The arc is drawn from <see cref="Origin"/> to the
    /// value, so a power gauge can swing both ways from zero: discharge to the
    /// right in the accent colour, regeneration and charging to the left in green.
    /// </summary>
    public sealed class Gauge : FrameworkElement
    {
        const double StartAngle = 135, Sweep = 270;

        public static readonly DependencyProperty ValueProperty = Reg(nameof(Value), typeof(double?), null);
        public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), typeof(double), 0.0);
        public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), 100.0);
        public static readonly DependencyProperty OriginProperty = Reg(nameof(Origin), typeof(double?), null);
        public static readonly DependencyProperty TitleProperty = Reg(nameof(Title), typeof(string), "");
        public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(string), "");
        public static readonly DependencyProperty FormatProperty = Reg(nameof(Format), typeof(string), "F0");
        public static readonly DependencyProperty AccentProperty = Reg(nameof(Accent), typeof(Brush), Brushes.Cyan);
        public static readonly DependencyProperty NegativeAccentProperty = Reg(nameof(NegativeAccent), typeof(Brush), Brushes.LimeGreen);
        public static readonly DependencyProperty MagnitudeProperty = Reg(nameof(Magnitude), typeof(bool), false);
        public static readonly DependencyProperty DetailProperty = Reg(nameof(Detail), typeof(string), "");

        static DependencyProperty Reg(string name, Type type, object? def) =>
            DependencyProperty.Register(name, type, typeof(Gauge),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public double? Origin { get => (double?)GetValue(OriginProperty); set => SetValue(OriginProperty, value); }
        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
        public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
        public Brush NegativeAccent { get => (Brush)GetValue(NegativeAccentProperty); set => SetValue(NegativeAccentProperty, value); }

        /// <summary>Show numbers without a sign; the arc direction, colour and title already tell which way.</summary>
        public bool Magnitude { get => (bool)GetValue(MagnitudeProperty); set => SetValue(MagnitudeProperty, value); }

        /// <summary>A second, smaller reading under the unit (the motor speed, the range); empty for none.</summary>
        public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

        static readonly Brush Track = Frozen(Color.FromRgb(0x22, 0x2C, 0x38));
        static readonly Brush Tick = Frozen(Color.FromRgb(0x3A, 0x47, 0x56));
        static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE6, 0xED, 0xF3));
        static readonly Brush Muted = Frozen(Color.FromRgb(0x8B, 0x98, 0xA5));
        static readonly Typeface Numbers = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface Labels = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 180 : available.Width,
                double.IsInfinity(available.Height) ? 180 : available.Height);

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size < 40) return;

            var center = new Point(ActualWidth / 2, ActualHeight / 2 + size * 0.04);
            double radius = size * 0.42;
            double thickness = size * 0.075;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // track and ticks
            dc.DrawGeometry(null, new Pen(Track, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(center, radius, StartAngle, StartAngle + Sweep));

            var tickPen = new Pen(Tick, Math.Max(1, size * 0.008));
            for (int i = 0; i <= 10; i++)
            {
                double a = Rad(StartAngle + Sweep * i / 10);
                double r1 = radius - thickness * 0.95, r2 = radius - thickness * (i % 5 == 0 ? 1.7 : 1.3);
                dc.DrawLine(tickPen,
                    new Point(center.X + r1 * Math.Cos(a), center.Y + r1 * Math.Sin(a)),
                    new Point(center.X + r2 * Math.Cos(a), center.Y + r2 * Math.Sin(a)));
            }

            // value arc from the origin
            double min = Minimum, max = Math.Max(Maximum, Minimum + 1e-9);
            if (Value is double v && !double.IsNaN(v))
            {
                double origin = Math.Clamp(Origin ?? min, min, max);
                double clamped = Math.Clamp(v, min, max);
                double a0 = AngleOf(origin, min, max), a1 = AngleOf(clamped, min, max);
                if (Math.Abs(a1 - a0) > 0.2)
                {
                    var brush = v < origin ? NegativeAccent : Accent;
                    dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                        Arc(center, radius, Math.Min(a0, a1), Math.Max(a0, a1)));
                }
            }

            // texts
            string valueText = Value is double val && !double.IsNaN(val)
                ? Shown(val).ToString(Format, CultureInfo.CurrentCulture) : "—";
            var big = Text(valueText, Numbers, size * 0.2, TextBrush, dpi);
            dc.DrawText(big, new Point(center.X - big.Width / 2, center.Y - big.Height * 0.62));

            var unit = Text(Unit, Labels, size * 0.085, Muted, dpi);
            dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + big.Height * 0.36));
            if (!string.IsNullOrEmpty(Detail))
            {
                var detail = Text(Detail, Numbers, size * 0.075, TextBrush, dpi);
                dc.DrawText(detail, new Point(center.X - detail.Width / 2, center.Y + big.Height * 0.36 + unit.Height * 1.05));
            }


            var lo = Text(Shown(min).ToString("0", CultureInfo.CurrentCulture), Labels, size * 0.06, Muted, dpi);
            var hi = Text(Shown(max).ToString("0", CultureInfo.CurrentCulture), Labels, size * 0.06, Muted, dpi);
            double lx = center.X + (radius) * Math.Cos(Rad(StartAngle)), ly = center.Y + radius * Math.Sin(Rad(StartAngle));
            double hx = center.X + (radius) * Math.Cos(Rad(StartAngle + Sweep));
            dc.DrawText(lo, new Point(lx - lo.Width / 2, ly + thickness * 0.7));
            dc.DrawText(hi, new Point(hx - hi.Width / 2, ly + thickness * 0.7));

            // the title sits between the end labels; a long one ("KULUTUS, SEIS") shrinks to fit rather than overlap them
            double room = (hx - hi.Width / 2) - (lx + lo.Width / 2) - size * 0.04;
            string caption = Title.ToUpper(CultureInfo.CurrentCulture);
            var title = Text(caption, Labels, size * 0.07, Muted, dpi);
            if (title.Width > room && room > 0) title = Text(caption, Labels, size * 0.07 * Math.Max(0.6, room / title.Width), Muted, dpi);
            dc.DrawText(title, new Point(center.X - title.Width / 2, center.Y + radius * 0.78));
        }

        double Shown(double v) => Magnitude ? Math.Abs(v) : v;

        static double AngleOf(double v, double min, double max) => StartAngle + Sweep * (v - min) / (max - min);

        static double Rad(double deg) => deg * Math.PI / 180;

        static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
        {
            var p0 = new Point(c.X + r * Math.Cos(Rad(fromDeg)), c.Y + r * Math.Sin(Rad(fromDeg)));
            var p1 = new Point(c.X + r * Math.Cos(Rad(toDeg)), c.Y + r * Math.Sin(Rad(toDeg)));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        static FormattedText Text(string s, Typeface face, double size, Brush brush, double dpi) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, Math.Max(1, size), brush, dpi);
    }
}
