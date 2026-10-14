using System.Globalization;

namespace EscoEvObdScanner
{
    public static class Bytes
    {
        public static string Hex(byte[] b) => Convert.ToHexString(b);

        /// <summary>Printable ASCII as is, everything else as '.'.</summary>
        public static string Ascii(byte[] b) =>
            new string(b.Select(x => x >= 0x20 && x < 0x7F ? (char)x : '.').ToArray());

        /// <summary>The printable ASCII characters only (zero, 0xFF and other bytes dropped wherever they are), spaces at either end trimmed; null when nothing printable remains.</summary>
        public static string? Text(byte[] b)
        {
            string s = new string(b.Where(x => x >= 0x20 && x < 0x7F).Select(x => (char)x).ToArray()).Trim();
            return s.Length > 0 ? s : null;
        }

        /// <summary>
        /// A date as two-digit BCD numbers: day, month, year (08 01 23 = 8 January 2023) by default, or
        /// year, month, day with <paramref name="order"/> "ymd", as the car file says; null when the
        /// bytes are not a valid date, and for any other order (no date rather than a wrong one).
        /// </summary>
        public static DateTime? BcdDate(byte[] b, string? order = null)
        {
            static int? Bcd(byte x) => (x >> 4) < 10 && (x & 0xF) < 10 ? (x >> 4) * 10 + (x & 0xF) : null;
            if (order != null && !order.Equals("dmy", StringComparison.OrdinalIgnoreCase) && !order.Equals("ymd", StringComparison.OrdinalIgnoreCase)) return null;
            bool yearFirst = string.Equals(order, "ymd", StringComparison.OrdinalIgnoreCase);
            if (b.Length < 3 || Bcd(b[yearFirst ? 2 : 0]) is not int day || Bcd(b[1]) is not int month || Bcd(b[yearFirst ? 0 : 2]) is not int year) return null;
            return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(2000 + year, month)
                ? new DateTime(2000 + year, month, day) : null;
        }

        /// <summary>
        /// A reply shown as numbers a person can reason about: text if it reads
        /// as text, one number up to four bytes, and a row of 16-bit values for
        /// longer replies (cell voltages and similar arrays are 16-bit).
        /// </summary>
        public static string Readable(byte[] b, int maxValues = 8)
        {
            if (b.Length == 0) return "";

            var text = Ascii(b).TrimEnd('.', ' ');
            if (text.Length >= 4 && text.Count(char.IsLetterOrDigit) * 2 > text.Length && text.Count(c => c == '.') <= 1)
                return $"\"{text}\"";

            if (b.Length <= 4)
            {
                long v = 0;
                foreach (var x in b) v = v * 256 + x;
                return v.ToString(CultureInfo.CurrentCulture);
            }

            int words = b.Length / 2;
            var values = Enumerable.Range(0, Math.Min(words, maxValues)).Select(i => (b[2 * i] << 8 | b[2 * i + 1]).ToString(CultureInfo.CurrentCulture));
            return string.Join("  ", values) + (words > maxValues || b.Length % 2 == 1 ? "  …" : "");
        }

        public static byte[] FromHex(string? hex)
        {
            try { return string.IsNullOrWhiteSpace(hex) ? Array.Empty<byte>() : Convert.FromHexString(hex); }
            catch (FormatException) { return Array.Empty<byte>(); }
        }
    }
}
