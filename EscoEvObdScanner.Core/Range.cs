namespace EscoEvObdScanner
{
    /// <summary>One stretch of driving: distance, net battery energy and the mean outside temperature.</summary>
    public sealed record Drive(DateTime Start, double Km, double Kwh, double? AmbientC);

    /// <summary>
    /// Range from the battery's remaining energy and the consumption measured
    /// on this car's own earlier drives, preferring drives at a similar outside
    /// temperature, because cold alone can add a third to consumption.
    /// </summary>
    public static class RangeEstimator
    {
        /// <summary>Less history than this says more about one trip than about the car.</summary>
        public const double MinKm = 30;

        /// <summary>Only the newest drives count, so last winter does not drag this summer down.</summary>
        public const double RecentKm = 600;

        /// <summary>How close the outside temperature of a drive must be to count as similar.</summary>
        public const double SimilarC = 5;

        public sealed record Result(double KwhPer100, double Km, bool SimilarTemperature);

        public static Result? Consumption(IEnumerable<Drive> history, double? ambientC)
        {
            var newest = history.Where(d => d.Km > 0.5).OrderByDescending(d => d.Start).ToList();
            if (ambientC is double t
                && Take(newest.Where(d => d.AmbientC is double a && Math.Abs(a - t) <= SimilarC)) is { } similar)
                return similar with { SimilarTemperature = true };
            return Take(newest);
        }

        static Result? Take(IEnumerable<Drive> drives)
        {
            double km = 0, kwh = 0;
            foreach (var d in drives)
            {
                km += d.Km;
                kwh += d.Kwh;
                if (km >= RecentKm) break;
            }
            return km >= MinKm && kwh > 0 ? new Result(kwh / km * 100, km, false) : null;
        }

        public static double? Range(double? energyKwh, double? kwhPer100) =>
            energyKwh is double e && kwhPer100 is double c && c > 1 ? e / c * 100 : null;
    }
}
