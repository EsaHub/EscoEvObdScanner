namespace EscoEvObdScanner
{
    /// <summary>
    /// What the energy taken from the battery has cost at the owner's
    /// electricity price. The car counts energy into the battery with
    /// regeneration included, so the grid share cannot be read from that
    /// counter; the cost starts from the energy taken out and adds the
    /// charging loss on top.
    /// </summary>
    public static class EnergyCost
    {
        /// <summary>Under this many years a yearly figure says more about the season than the year.</summary>
        public const double MinYears = 0.5;

        /// <summary>An energy figure that can be counted with: a number, not below zero.</summary>
        static double? Energy(double? kwh) => kwh is double k && double.IsFinite(k) && k >= 0 ? k : null;

        /// <summary>kWh drawn from the grid for this much taken from the battery.</summary>
        public static double? FromGrid(double? kwhOut, double lossPercent) =>
            Energy(kwhOut) is double k && lossPercent is >= 0 and < 100 ? k * (1 + lossPercent / 100) : null;

        /// <summary>Euros, from cents per kWh; null without a price or energy.</summary>
        public static double? Euros(double? kwhOut, double? priceCents, double lossPercent) =>
            FromGrid(kwhOut, lossPercent) is double k && priceCents is double c and >= 0 ? k * c / 100 : null;

        /// <summary>kWh/100 km over the whole life; null below 100 km.</summary>
        public static double? Per100Km(double? kwhOut, double? km) =>
            Energy(kwhOut) is double k && km is double d and >= 100 && double.IsFinite(d) ? k / d * 100 : null;

        /// <summary>
        /// The part of the energy taken out over the kilometres since the odometer read <paramref name="startKm"/>,
        /// at the lifetime average: the counters cover the whole life, a previous owner's use included.
        /// Null below 100 km on the odometer or with no kilometres since.
        /// </summary>
        public static double? Since(double? kwhOut, double? odometer, double startKm) =>
            Energy(kwhOut) is double k && odometer is double d and >= 100 && double.IsFinite(d) && d > startKm && startKm >= 0 ? k * (d - startKm) / d : null;

        /// <summary>A figure spread over the years since <paramref name="since"/>; null under <see cref="MinYears"/>.</summary>
        public static double? PerYear(double? total, DateTime? since, DateTime now) =>
            Energy(total) is double t && since is DateTime s && (now - s).TotalDays / 365.25 is var years and >= MinYears ? t / years : null;
    }
}
