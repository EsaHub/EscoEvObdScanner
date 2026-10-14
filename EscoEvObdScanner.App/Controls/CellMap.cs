using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EscoEvObdScanner.App.Controls
{
    public enum CellPalette
    {
        /// <summary>Low is bad (red), high is good (green): cell voltage, cell health.</summary>
        LowIsBad,
        /// <summary>Cold is blue, hot is red: temperatures.</summary>
        Heat
    }

    /// <summary>
    /// A grid of cells coloured by value, scaled to the current min..max so
    /// even a few millivolts of spread stand out. The lowest and highest cells
    /// get a frame; hovering shows the exact value.
    /// </summary>
    public sealed class CellMap : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = Reg(nameof(Values), typeof(double[]), null);
        public static readonly DependencyProperty ColumnsProperty = Reg(nameof(Columns), typeof(int), 12);
        public static readonly DependencyProperty FormatProperty = Reg(nameof(Format), typeof(string), "F3");
        public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(string), "");
        public static readonly DependencyProperty ItemLabelProperty = Reg(nameof(ItemLabel), typeof(string), "");
        public static readonly DependencyProperty PaletteProperty = Reg(nameof(Palette), typeof(CellPalette), CellPalette.LowIsBad);
        public static readonly DependencyProperty MinimumSpanProperty = Reg(nameof(MinimumSpan), typeof(double), 0.0);
        public static readonly DependencyProperty WarnAtProperty = Reg(nameof(WarnAt), typeof(double), double.NaN);
        public static readonly DependencyProperty BadAtProperty = Reg(nameof(BadAt), typeof(double), double.NaN);

        static DependencyProperty Reg(string name, Type type, object? def) =>
            DependencyProperty.Register(name, type, typeof(CellMap),
                new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

        public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
        public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
        public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public string ItemLabel { get => (string)GetValue(ItemLabelProperty); set => SetValue(ItemLabelProperty, value); }
        public CellPalette Palette { get => (CellPalette)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
        /// <summary>
        /// Smallest colour range. Without it a pack balanced to 2 mV would be
        /// painted from deep red to bright green over nothing.
        /// </summary>
        public double MinimumSpan { get => (double)GetValue(MinimumSpanProperty); set => SetValue(MinimumSpanProperty, value); }

        /// <summary>
        /// With both set, a cell is green below WarnAt, amber below BadAt and red from there,
        /// whatever the other cells read (high is bad). NaN: colours follow the range of the values.
        /// </summary>
        public double WarnAt { get => (double)GetValue(WarnAtProperty); set => SetValue(WarnAtProperty, value); }
        public double BadAt { get => (double)GetValue(BadAtProperty); set => SetValue(BadAtProperty, value); }

        static readonly Color GoodColour = Color.FromRgb(0x34, 0xD3, 0x99), WatchColour = Color.FromRgb(0xFB, 0xBF, 0x24), BadColour = Color.FromRgb(0xF8, 0x71, 0x71);

        static readonly Brush Empty = Frozen(Color.FromRgb(0x1A, 0x21, 0x2B));
        static readonly Brush Dark = Frozen(Color.FromRgb(0x0B, 0x0F, 0x14));
        static readonly Pen Frame = new(Frozen(Colors.White), 2);
        static readonly Typeface Face = new("Segoe UI");
        static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        readonly ToolTip _tip = new();

        public CellMap()
        {
            ToolTip = _tip;
            ToolTipService.SetInitialShowDelay(this, 0);
            ToolTipService.SetShowDuration(this, 60000);
        }

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 600 : available.Width,
                double.IsInfinity(available.Height) ? 300 : available.Height);

        (int Rows, double CellW, double CellH) Layout(int count)
        {
            int cols = Math.Max(1, Columns);
            int rows = (count + cols - 1) / cols;
            return (rows, ActualWidth / cols, rows == 0 ? 0 : ActualHeight / rows);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var values = Values;
            if (values == null || values.Length == 0 || ActualWidth < 20 || ActualHeight < 20) return;

            var (_, cw, ch) = Layout(values.Length);
            // a value that is no number (a damaged recording) takes no part in the scale and is drawn empty
            var finite = values.Where(double.IsFinite).ToArray();
            if (finite.Length == 0) return;
            double min = finite.Min(), max = finite.Max();
            double span = Math.Max(max - min, MinimumSpan);
            double lo = (min + max) / 2 - span / 2;
            int minIndex = Array.IndexOf(values, min), maxIndex = Array.IndexOf(values, max);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            const double gap = 3;

            for (int i = 0; i < values.Length; i++)
            {
                int cols = Math.Max(1, Columns);
                int col = i % cols, row = i / cols;
                var rect = new Rect(col * cw + gap / 2, row * ch + gap / 2, Math.Max(1, cw - gap), Math.Max(1, ch - gap));
                double f = span <= 0 ? 0.5 : Math.Clamp((values[i] - lo) / span, 0, 1);
                bool graded = !double.IsNaN(WarnAt) && !double.IsNaN(BadAt);
                if (!double.IsFinite(values[i])) { dc.DrawRoundedRectangle(Empty, null, rect, 4, 4); continue; }
                var fill = new SolidColorBrush(!graded ? Colour(f)
                    : values[i] < WarnAt ? GoodColour : values[i] < BadAt ? WatchColour : BadColour);
                fill.Freeze();

                dc.DrawRoundedRectangle(fill, i == minIndex || i == maxIndex ? Frame : null, rect, 4, 4);

                if (rect.Width > 34 && rect.Height > 26)
                {
                    // the text grows with the cell (a quarter of its height for the number, two fifths for the value),
                    // never smaller than in a small cell, and the value is narrowed to fit the cell's width
                    double numSize = Math.Max(Math.Min(10, rect.Height * 0.28), Math.Min(rect.Height * 0.24, rect.Width * 0.2));
                    var num = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, Face, numSize, Dark, dpi);
                    dc.DrawText(num, new Point(rect.Left + 4, rect.Top + 2));

                    string text = values[i].ToString(Format, CultureInfo.CurrentCulture);
                    double valSize = Math.Max(Math.Min(13, rect.Width * 0.2), Math.Min(rect.Height * 0.4, rect.Width * 0.24));
                    var val = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, valSize, Dark, dpi);
                    if (val.Width > rect.Width - 6)
                        val = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold,
                            Math.Max(6, valSize * (rect.Width - 6) / val.Width), Dark, dpi);
                    dc.DrawText(val, new Point(rect.Left + (rect.Width - val.Width) / 2, rect.Bottom - val.Height - 3));
                }
            }
        }

        Color Colour(double f)
        {
            // three-stop gradients; f is 0..1 within the current range
            var (a, b, c) = Palette == CellPalette.Heat
                ? (Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0xF8, 0x71, 0x71))
                : (Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0x34, 0xD3, 0x99));
            return f < 0.5 ? Lerp(a, b, f * 2) : Lerp(b, c, (f - 0.5) * 2);
        }

        static Color Lerp(Color x, Color y, double t) => Color.FromRgb(
            (byte)(x.R + (y.R - x.R) * t), (byte)(x.G + (y.G - x.G) * t), (byte)(x.B + (y.B - x.B) * t));

        int _hovered = -1;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var values = Values;
            if (values == null || values.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) { Hover(-1); return; }

            var (rows, cw, ch) = Layout(values.Length);
            int cols = Math.Max(1, Columns);
            var p = e.GetPosition(this);
            // clamp: the far right and bottom edges would otherwise step into the next row
            int col = Math.Clamp((int)(p.X / cw), 0, cols - 1);
            int row = Math.Clamp((int)(p.Y / ch), 0, Math.Max(0, rows - 1));
            int index = row * cols + col;
            Hover(index < values.Length ? index : -1);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Hover(-1);
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            // values update several times a second; keep an open tooltip current
            if (_hovered >= 0 && (e.Property == ValuesProperty || e.Property == FormatProperty || e.Property == UnitProperty))
                Hover(_hovered);
        }

        void Hover(int index)
        {
            var values = Values;
            _hovered = values != null && index >= 0 && index < values.Length ? index : -1;
            if (_hovered < 0) { _tip.IsOpen = false; return; }

            _tip.Content = $"{ItemLabel} {_hovered + 1}:  {values![_hovered].ToString(Format, CultureInfo.CurrentCulture)} {Unit}";
            _tip.IsOpen = true;
        }
    }
}
