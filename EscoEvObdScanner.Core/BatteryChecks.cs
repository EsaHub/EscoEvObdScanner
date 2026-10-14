namespace EscoEvObdScanner
{
    public enum Grade { Good, Watch, Bad, Info }

    /// <summary>
    /// Rough grading of a battery reading for the report. The limits are
    /// rules of thumb, not manufacturer figures, and the report says so.
    /// </summary>
    public static class BatteryChecks
    {
        /// <summary>Above this current the cells sag unevenly and their spread says little.</summary>
        public const double RestCurrentA = 5;

        /// <summary>SOH %: good from the first, watch from the second, poor below it.</summary>
        public const double SohGood = 90, SohWatch = 80;

        /// <summary>Cell voltage spread at rest, mV: good below the first, poor from the second.</summary>
        public const double SpreadWatchMv = 30, SpreadBadMv = 80;

        /// <summary>
        /// Temperature spread, C: good below the first (the under 5 C between cells that battery
        /// thermal management aims at), poor from the second.
        /// </summary>
        public const double TempWatchC = 5, TempBadC = 10;

        /// <summary>
        /// Insulation resistance, kOhm: good from this, or from the legal minimum where that is higher
        /// (packs over 1000 V, see <see cref="IsolationGoodFromKOhm"/>); below the legal minimum
        /// (<see cref="IsolationMinKOhm"/>) poor.
        /// </summary>
        public const double IsolationGoodKOhm = 500;

        /// <summary>
        /// UNECE R100's insulation minimum where the battery's DC side and the motor's AC side are
        /// galvanically connected, as in an electric car: 500 ohms per volt of the working voltage
        /// (100 ohms per volt applies only to separate or doubly insulated buses).
        /// </summary>
        public const double IsolationOhmPerVolt = 500;

        /// <summary>The voltage assumed when the reading has none, or one below 50 V: a typical pack.</summary>
        public const double IsolationDefaultVolts = 400;

        /// <summary>
        /// 12 V lead-acid battery at rest, V (Battery University BU-903): full charge and half charge.
        /// A reading below <see cref="AuxRestBelowV"/> is taken as one without the DC/DC charging
        /// it, which holds the 12 V battery at 13.5-14.5 V.
        /// </summary>
        public const double AuxFullV = 12.65, AuxHalfV = 12.24, AuxRestBelowV = 13.0;

        /// <summary>The wear rate is given from this age on, years: younger, (100 - SOH) is mostly the reading's own spread.</summary>
        public const double WearMinYears = 0.5;

        /// <summary>The legal minimum for this pack voltage, kOhm.</summary>
        public static double IsolationMinKOhm(double? volts) =>
            IsolationOhmPerVolt * (volts is double v && double.IsFinite(v) && v >= 50 ? v : IsolationDefaultVolts) / 1000;

        /// <summary>Where Good starts for this pack voltage, kOhm: never below the legal minimum.</summary>
        public static double IsolationGoodFromKOhm(double? volts) => Math.Max(IsolationGoodKOhm, IsolationMinKOhm(volts));

        public static Grade? Soh(double? soh) => Real(soh) switch
        {
            null => null,
            >= SohGood => Grade.Good,
            >= SohWatch => Grade.Watch,
            _ => Grade.Bad
        };

        /// <summary>Cell voltage spread in mV, comparable only at rest.</summary>
        public static Grade? CellSpread(double? mv, double? currentA)
        {
            if (Real(mv) is not double v) return null;
            if (currentA is not double a || Math.Abs(a) > RestCurrentA) return Grade.Info;
            return v < SpreadWatchMv ? Grade.Good : v < SpreadBadMv ? Grade.Watch : Grade.Bad;
        }

        /// <summary>Spread between the warmest and coldest point of the pack, C.</summary>
        public static Grade? TempSpread(double? c) => Real(c) switch
        {
            null => null,
            < TempWatchC => Grade.Good,
            < TempBadC => Grade.Watch,
            _ => Grade.Bad
        };

        /// <summary>The lower of the two insulation resistances, kOhm, at the pack voltage given (V).</summary>
        public static Grade? Isolation(double? kOhm, double? volts = null) => Real(kOhm) switch
        {
            null => null,
            double k when k >= IsolationGoodFromKOhm(volts) => Grade.Good,
            double k when k >= IsolationMinKOhm(volts) => Grade.Watch,
            _ => Grade.Bad
        };

        /// <summary>
        /// A cell's extra sag under load beyond its offset at rest, mV per 100 A: below 5 is what
        /// cells differ by, 5 to 15 a cell to watch, 15 or more a clearly higher internal resistance.
        /// </summary>
        public const double CellLoadWatchMv = 5, CellLoadBadMv = 15;

        /// <summary>The measuring noise (DriveStats.NoiseFloorMv of the session) scaled to 100 A for a drive at this current.</summary>
        static double NoisePer100A(double? loadCurrentA, double noiseMv) => loadCurrentA is double a && a > 0 ? noiseMv * 100 / a : 0;

        /// <summary>Where "watch" starts at this load current (DriveStats.LoadCurrentScaled): 5 mV / 100 A, or the noise if that is more.</summary>
        public static double CellLoadWatchAt(double? loadCurrentA, double noiseMv = DriveStats.NoiseMv) =>
            Math.Max(CellLoadWatchMv, NoisePer100A(loadCurrentA, noiseMv));

        /// <summary>Where "poor" starts at this load current: 15 mV / 100 A, or twice the noise if that is more.</summary>
        public static double CellLoadBadAt(double? loadCurrentA, double noiseMv = DriveStats.NoiseMv) =>
            Math.Max(CellLoadBadMv, 2 * NoisePer100A(loadCurrentA, noiseMv));

        /// <summary>
        /// Below this pack temperature (°C, the coldest probe under load) a cell's resistance rises steeply, and more in
        /// the colder parts of the pack, so a cell sagging under load is rated at most "watch".
        /// </summary>
        public const double CellLoadColdC = 10;

        public static bool TooColdForLoad(double? packTempC) => packTempC < CellLoadColdC;

        public static Grade? CellUnderLoad(double? mvPer100A, double? loadCurrentA, double noiseMv = DriveStats.NoiseMv, double? packTempC = null)
        {
            if (Real(mvPer100A) is not double v) return null;
            var g = v < CellLoadWatchAt(loadCurrentA, noiseMv) ? Grade.Good : v < CellLoadBadAt(loadCurrentA, noiseMv) ? Grade.Watch : Grade.Bad;
            return g == Grade.Bad && TooColdForLoad(packTempC) ? Grade.Watch : g;
        }

        /// <summary>A reading that is no number (0/0 in an expression) is no reading, not a bad one.</summary>
        static double? Real(double? v) => v is double d && double.IsFinite(d) ? d : null;

        public static Grade Faults(int active, int stored) =>
            active > 0 ? Grade.Bad : stored > 0 ? Grade.Watch : Grade.Good;

        /// <summary>The worst of the grades given; Info and missing ones do not count.</summary>
        public static Grade? Overall(IEnumerable<Grade?> grades)
        {
            var real = grades.OfType<Grade>().Where(g => g != Grade.Info).ToList();
            return real.Count == 0 ? null : real.Max();
        }
    }
}
