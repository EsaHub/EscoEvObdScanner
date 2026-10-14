using System.Text;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Decoder for service 0x24, ReadScalingDataByIdentifier.
    ///
    /// Where 0x22 returns an identifier's value, 0x24 returns a description of
    /// how that value should be interpreted: data type, byte count, and
    /// optionally a unit and a conversion formula with its coefficients.
    ///
    /// Layout of the scaling record: a sequence of scaling bytes, each one
    /// high nibble = encoding, low nibble = number of data bytes. Encodings
    /// 0x9 (formula) and 0xA (unit/format) are followed by an extension whose
    /// length is given by that same low nibble.
    ///
    /// CAVEAT: this follows ISO 14229-1 and is not verified against a real
    /// ECU. Treat the decoded text as a hint and the raw bytes as the truth. If something looks wrong, the raw bytes are printed too.
    /// </summary>
    public static class Scaling
    {
        public static string Encoding(int code) => code switch
        {
            0x0 => "unsigned",
            0x1 => "signed",
            0x2 => "bit-mapped",
            0x3 => "bit-mapped with mask",
            0x4 => "BCD",
            0x5 => "state encoded",
            0x6 => "ASCII",
            0x7 => "signed float",
            0x8 => "packet",
            0x9 => "formula",
            0xA => "unit/format",
            0xB => "state and connection type",
            _ => $"reserved(0x{code:X})"
        };

        /// <summary>
        /// Unit identifiers from ISO 14229-1. Only the ones likely to turn up
        /// on a battery ECU are listed; anything else is reported by its raw
        /// id rather than guessed at.
        /// </summary>
        public static string Unit(int id) => id switch
        {
            0x00 => "no unit",
            0x08 => "s",
            0x09 => "min",
            0x0A => "h",
            0x0D => "A",
            0x0E => "V",
            0x10 => "ohm",
            0x16 => "K",
            0x17 => "C",
            0x21 => "W",
            0x33 => "rpm",
            0x34 => "counts",
            0x35 => "%",
            _ => $"unit0x{id:X2}"
        };

        /// <summary>
        /// Formula identifiers. x is the raw value, C0..C2 the coefficients
        /// that follow in the extension.
        /// </summary>
        public static string Formula(int id) => id switch
        {
            0x00 => "y = C0*x + C1",
            0x01 => "y = C0*(x + C1)",
            0x02 => "y = C0/(x + C1) + C2",
            0x03 => "y = x/C0 + C1",
            0x04 => "y = (x + C0)/C1",
            0x05 => "y = (x + C0)/C1 + C2",
            0x06 => "y = C0*x",
            0x07 => "y = x/C0",
            0x08 => "y = x + C0",
            0x09 => "y = x*C0/C1",
            _ => $"formula0x{id:X2}"
        };

        /// <summary>
        /// Coefficients are two bytes: a signed 4-bit exponent in the high
        /// nibble, then a signed 12-bit mantissa. Value = mantissa * 10^exp.
        /// </summary>
        public static double Coefficient(byte hi, byte lo)
        {
            int exponent = (hi >> 4) & 0x0F;
            if (exponent > 7) exponent -= 16;            // two's complement, 4 bit

            int mantissa = ((hi & 0x0F) << 8) | lo;
            if (mantissa > 0x7FF) mantissa -= 0x1000;    // two's complement, 12 bit

            return mantissa * Math.Pow(10, exponent);
        }

        /// <summary>
        /// Turns a scaling record into readable lines. Never throws: where a
        /// record stops making sense it ends with a note; the caller prints the
        /// raw bytes alongside.
        /// </summary>
        public static List<string> Describe(byte[] record)
        {
            var lines = new List<string>();
            if (record.Length == 0) return lines;

            int i = 0;
            while (i < record.Length)
            {
                int encoding = (record[i] >> 4) & 0x0F;
                int length = record[i] & 0x0F;
                i++;

                switch (encoding)
                {
                    case 0x9 when i + length <= record.Length:
                    {
                        var ext = record.Skip(i).Take(length).ToArray();
                        i += length;

                        var sb = new StringBuilder();
                        if (ext.Length >= 1)
                        {
                            sb.Append(Formula(ext[0]));
                            var coeffs = new List<string>();
                            for (int c = 1; c + 1 < ext.Length; c += 2)
                                coeffs.Add($"C{coeffs.Count}={Coefficient(ext[c], ext[c + 1]).ToString("G6", System.Globalization.CultureInfo.InvariantCulture)}");
                            if (coeffs.Count > 0) sb.Append("   ").Append(string.Join(", ", coeffs));
                        }
                        lines.Add($"formula: {sb}");
                        break;
                    }

                    case 0xA when i + length <= record.Length:
                    {
                        var ext = record.Skip(i).Take(length).ToArray();
                        i += length;
                        lines.Add($"unit:    {string.Join(", ", ext.Select(b => Unit(b)))}");
                        break;
                    }

                    case 0x9 or 0xA:
                        // an extension longer than what is left: the rest cannot be trusted
                        lines.Add($"(record cut short: {length} byte(s) announced, {record.Length - i} left)");
                        return lines;

                    default:
                        lines.Add($"data:    {Encoding(encoding)}, {length} byte(s)");
                        break;
                }
            }

            return lines;
        }
    }
}