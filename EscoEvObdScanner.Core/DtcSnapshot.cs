using System.Globalization;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Decodes a freeze frame (reply to 0x19 04): the values an ECU stored when
    /// a fault was set. Where the car file names them (platform.fault_time),
    /// these include the odometer and a running clock, which together say when
    /// and where the fault happened.
    ///
    /// The reply lists identifiers and their data back to back without
    /// lengths, so the split is found by search: lengths the catalogue knows
    /// are tried first, and the identifier count in the header must come out
    /// exactly.
    /// </summary>
    public static class DtcSnapshot
    {
        public sealed record Value(int Did, byte[] Data)
        {
            public long Number => Data.Aggregate(0L, (v, b) => v * 256 + b);
            public override string ToString() => $"{Did:X4}={Bytes.Hex(Data)}";
        }

        /// <param name="Km">Odometer at the time of the fault, km.</param>
        /// <param name="Clock">The car's clock at the time of the fault, in its own steps.</param>
        /// <param name="Values">The values of every record in the reply, in order.</param>
        public sealed record Frame(IReadOnlyList<Value> Values, long? Km, long? Clock);

        const int MaxLength = 8;

        /// <param name="hex">The reply after 0x59: 04, DTC (3), status, then per record: record number, identifier count, values. Km and Clock come from the first record that holds them.</param>
        /// <param name="knownLength">Data length of an identifier if known, e.g. from the catalogue.</param>
        /// <param name="time">Which identifiers hold the odometer and the clock; null: neither is picked out.</param>
        /// <param name="expressionOf">The ECU's expression for an identifier, from the car file: the odometer is read through it
        /// (the BMS keeps km x 1024), else taken as plain km.</param>
        public static Frame? Decode(string? hex, Func<int, int?>? knownLength = null, FaultTimeDef? time = null,
            Func<int, string?>? expressionOf = null)
        {
            var b = Bytes.FromHex(hex);
            if (b.Length < 7 || b[0] != 0x04) return null;

            int count = b[6];               // b[5] is the record number
            var values = new List<Value>();
            if (!Split(b, 7, count, knownLength, values, new HashSet<(int, int)>())) return new Frame(Array.Empty<Value>(), null, null);

            var odometers = time?.Odometers ?? new HashSet<int>();
            int clockDid = time?.Clock?.Dids is [int c, ..] ? c : -1;
            var odometer = values.FirstOrDefault(v => odometers.Contains(v.Did));
            long? km = odometer == null ? null
                : Expr.Evaluate(expressionOf?.Invoke(odometer.Did), odometer.Data) is double d ? (long)Math.Floor(d) : odometer.Number;
            return new Frame(values, km, values.FirstOrDefault(v => v.Did == clockDid)?.Number);
        }

        /// <summary>
        /// Whether two bytes can start a value: an identifier the catalogue knows
        /// for this ECU, or one in a block where manufacturers usually put them
        /// (0x01xx, 0xD000-0xDFFF, 0xF000-0xFFFF). Keeps the search from splitting
        /// in the middle of data.
        /// </summary>
        static bool LooksLikeDid(int did, Func<int, int?>? knownLength) =>
            knownLength?.Invoke(did) != null || did >> 12 is 0xD or 0xF || did >> 8 == 0x01;

        static bool Split(byte[] b, int pos, int left, Func<int, int?>? knownLength, List<Value> values, HashSet<(int, int)> dead)
        {
            if (left == 0)
            {
                if (pos == b.Length) return true;
                // the request asks for every record (FF): another record number and count may follow
                return pos + 2 <= b.Length && b[pos + 1] > 0 && Split(b, pos + 2, b[pos + 1], knownLength, values, dead);
            }
            if (pos + 3 > b.Length || dead.Contains((pos, left))) return false;

            int did = b[pos] << 8 | b[pos + 1];
            if (!LooksLikeDid(did, knownLength)) return false;

            var lengths = new List<int>();
            if (knownLength?.Invoke(did) is int k and > 0) lengths.Add(k);
            lengths.AddRange(Enumerable.Range(1, MaxLength).Where(l => !lengths.Contains(l)));

            foreach (int len in lengths)
            {
                if (pos + 2 + len > b.Length) continue;
                values.Add(new Value(did, b[(pos + 2)..(pos + 2 + len)]));
                if (Split(b, pos + 2 + len, left - 1, knownLength, values, dead)) return true;
                values.RemoveAt(values.Count - 1);
            }
            dead.Add((pos, left));
            return false;
        }

        /// <summary>
        /// When a clock reading was taken, from a later reading of the same
        /// clock and the time that later reading was made. The clock runs on
        /// while the car sleeps (on e-CMP it keeps time to 0.1 s over a night).
        /// </summary>
        public static DateTime? When(long? then, long? now, DateTime nowUtc, double tickSeconds)
        {
            if (then is not long t || now is not long n || t > n || !double.IsFinite(tickSeconds) || tickSeconds <= 0) return null;
            double seconds = (n - t) * tickSeconds;
            // a garbled clock value would point centuries back
            return seconds < 100 * 365.25 * 86400 ? nowUtc - TimeSpan.FromSeconds(seconds) : null;
        }

        public static string Describe(Frame f, DateTime? when) =>
            string.Join("  ", new[]
            {
                f.Km is long km ? km.ToString("N0", CultureInfo.CurrentCulture) + " km" : null,
                when?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            }.Where(s => s != null));
    }
}
