namespace EscoEvObdScanner.AppLogic.ViewModels
{
    /// <summary>One point of a chart: seconds since the start, and the value.</summary>
    public readonly record struct ChartPoint(double X, double Y);

    /// <summary>One line of a rolling time chart: its name, colour (0xRRGGBB), number format and its points (those of the last keepSeconds given to Add).</summary>
    public sealed class ChartSeries
    {
        public ChartSeries(string title, uint rgb, string format = "F1")
        {
            Title = title;
            Rgb = rgb;
            Format = format;
        }

        public string Title { get; set; }
        public uint Rgb { get; }
        public string Format { get; }
        public List<ChartPoint> Points { get; } = new();

        public void Add(double t, double? value, double keepSeconds)
        {
            if (value is not double v || double.IsNaN(v)) return;
            Points.Add(new ChartPoint(t, v));
            int drop = 0;
            while (drop < Points.Count && Points[drop].X < t - keepSeconds) drop++;
            if (drop > 0) Points.RemoveRange(0, drop);
        }
    }
}
