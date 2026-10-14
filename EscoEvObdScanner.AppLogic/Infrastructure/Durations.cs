namespace EscoEvObdScanner.AppLogic.Infrastructure
{
    /// <summary>Rough durations in words for the hints before a read, scan or sweep.</summary>
    public static class Durations
    {
        /// <summary>About how long reading one identifier takes on an ELM327-class adapter.</summary>
        public const double SecondsPerRead = 0.15;

        /// <summary>Seconds to the nearest 5 under a minute, "a minute" under 90 s, whole minutes under 90 min, else whole hours.</summary>
        public static string About(double seconds) =>
            seconds < 60 ? Loc.I.Format("AboutSeconds", Math.Max(5, (int)Math.Round(seconds / 5) * 5))
            : seconds < 90 ? Loc.I["AboutMinute"]
            : seconds < 90 * 60 ? Loc.I.Format("AboutMinutes", (int)Math.Round(seconds / 60))
            : Loc.I.Format("AboutHours", (int)Math.Round(seconds / 3600));
    }
}
