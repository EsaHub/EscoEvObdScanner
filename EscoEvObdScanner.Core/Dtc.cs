namespace EscoEvObdScanner
{
    /// <summary>
    /// One stored fault, as returned by service 0x19.
    ///
    /// A DTC is three bytes plus a status byte. The first two bytes encode the
    /// familiar P/C/B/U code per ISO 15031-6; the third is the failure type
    /// byte, which narrows down how the fault presented (short to ground, out
    /// of range, no signal, and so on).
    /// </summary>
    public sealed class Dtc
    {
        public int Raw;             // 3-byte code
        public byte Status;

        public string Code
        {
            get
            {
                int b0 = (Raw >> 16) & 0xFF;
                int b1 = (Raw >> 8) & 0xFF;

                char system = ((b0 >> 6) & 0x03) switch
                {
                    0 => 'P',       // powertrain
                    1 => 'C',       // chassis
                    2 => 'B',       // body
                    _ => 'U'        // network
                };

                int d1 = (b0 >> 4) & 0x03;
                int d2 = b0 & 0x0F;
                int d3 = (b1 >> 4) & 0x0F;
                int d4 = b1 & 0x0F;

                return $"{system}{d1}{d2:X}{d3:X}{d4:X}";
            }
        }

        /// <summary>Failure type byte — the third DTC byte.</summary>
        public byte FailureType => (byte)(Raw & 0xFF);

        /// <summary>The standard the failure type names come from.</summary>
        public const string FailureTypeStandard = "SAE J2012-DA";

        /// <summary>The failure type byte's names in SAE J2012-DA, untranslated; other values are manufacturer or unassigned.</summary>
        static readonly Dictionary<byte, string> FailureTypes = new()
        {
            [0x00] = "No Sub Type Information",
            [0x01] = "General Electrical Failure",
            [0x02] = "General Signal Failure",
            [0x03] = "FM (Frequency Modulated) / PWM (Pulse Width Modulated) Failures",
            [0x04] = "System Internal Failures",
            [0x05] = "System Programming Failures",
            [0x06] = "Algorithm Based Failures",
            [0x07] = "Mechanical Failures",
            [0x08] = "Bus Signal / Message Failures",
            [0x09] = "Component Failures",
            [0x11] = "Circuit Short To Ground",
            [0x12] = "Circuit Short To Battery",
            [0x13] = "Circuit Open",
            [0x14] = "Circuit Short To Ground Or Open",
            [0x15] = "Circuit Short To Battery Or Open",
            [0x16] = "Circuit Voltage Below Threshold",
            [0x17] = "Circuit Voltage Above Threshold",
            [0x18] = "Circuit Current Below Threshold",
            [0x19] = "Circuit Current Above Threshold",
            [0x1A] = "Circuit Resistance Below Threshold",
            [0x1B] = "Circuit Resistance Above Threshold",
            [0x1C] = "Circuit Voltage Out Of Range",
            [0x1D] = "Circuit Current Out Of Range",
            [0x1E] = "Circuit Resistance Out Of Range",
            [0x1F] = "Circuit Intermittent",
            [0x21] = "Signal Amplitude < Minimum",
            [0x22] = "Signal Amplitude > Maximum",
            [0x23] = "Signal Stuck Low",
            [0x24] = "Signal Stuck High",
            [0x25] = "Signal Shape / Waveform Failure",
            [0x26] = "Signal Rate Of Change Below Threshold",
            [0x27] = "Signal Rate Of Change Above Threshold",
            [0x28] = "Signal Bias Level Out Of Range / Zero Adjustment Failure",
            [0x29] = "Signal Invalid",
            [0x2F] = "Signal Erratic",
            [0x31] = "No Signal",
            [0x32] = "Signal Low Time < Minimum",
            [0x33] = "Signal Low Time > Maximum",
            [0x34] = "Signal High Time < Minimum",
            [0x35] = "Signal High Time > Maximum",
            [0x36] = "Signal Frequency Too Low",
            [0x37] = "Signal Frequency Too High",
            [0x38] = "Signal Frequency Incorrect",
            [0x39] = "Incorrect Has Too Few Pulses",
            [0x3A] = "Incorrect Has Too Many Pulses",
            [0x41] = "General Checksum Failure",
            [0x42] = "General Memory Failure",
            [0x43] = "Special Memory Failure",
            [0x44] = "Data Memory Failure",
            [0x45] = "Program Memory Failure",
            [0x46] = "Calibration / Parameter Memory Failure",
            [0x47] = "Watchdog / Safety µC Failure",
            [0x48] = "Supervision Software Failure",
            [0x49] = "Internal Electronic Failure",
            [0x4A] = "Incorrect Component Installed",
            [0x4B] = "Over Temperature",
            [0x51] = "Not Programmed",
            [0x52] = "Not Activated",
            [0x53] = "Deactivated",
            [0x54] = "Missing Calibration",
            [0x55] = "Not Configured",
            [0x61] = "Signal Calculation Failure",
            [0x62] = "Signal Compare Failure",
            [0x63] = "Circuit / Component Protection Time-Out",
            [0x64] = "Signal Plausibility Failure",
            [0x65] = "Signal Has Too Few Transitions / Events",
            [0x66] = "Signal Has Too Many Transitions / Events",
            [0x67] = "Signal Incorrect After Event",
            [0x68] = "Event Information",
            [0x71] = "Actuator Stuck",
            [0x72] = "Actuator Stuck Open",
            [0x73] = "Actuator Stuck Closed",
            [0x74] = "Actuator Slipping",
            [0x75] = "Emergency Position Not Reachable",
            [0x76] = "Wrong Mounting Position",
            [0x77] = "Commanded Position Not Reachable",
            [0x78] = "Alignment Or Adjustment Incorrect",
            [0x79] = "Mechanical Linkage Failure",
            [0x7A] = "Fluid Leak Or Seal Failure",
            [0x7B] = "Low Fluid Level",
            [0x81] = "Invalid Serial Data Received",
            [0x82] = "Alive / Sequence Counter Incorrect / Not Updated",
            [0x83] = "Value Of Signal Protection Calculation Incorrect",
            [0x84] = "Signal Below Allowable Range",
            [0x85] = "Signal Above Allowable Range",
            [0x86] = "Signal Invalid",
            [0x87] = "Missing Message",
            [0x88] = "Bus Off",
            [0x8F] = "Erratic",
            [0x91] = "Parametric",
            [0x92] = "Performance Or Incorrect Operation",
            [0x93] = "No Operation",
            [0x94] = "Unexpected Operation",
            [0x95] = "Incorrect Assembly",
            [0x96] = "Component Internal Failure",
            [0x97] = "Component Or System Operation Obstructed Or Blocked",
            [0x98] = "Component Or System Over Temperature",
        };

        /// <summary>The SAE J2012-DA name of a failure type byte, or null for one the standard does not name.</summary>
        public static string? FailureTypeName(byte failureType) => FailureTypes.GetValueOrDefault(failureType);

        /// <summary>The failure type of a code written "P0A7F-13", by its SAE J2012-DA name; null without one.</summary>
        public static string? FailureTypeName(string code) =>
            code.LastIndexOf('-') is int i and > 0 && i + 3 == code.Length
            && byte.TryParse(code.AsSpan(i + 1), System.Globalization.NumberStyles.HexNumber, null, out var b) ? FailureTypeName(b) : null;

        /// <summary>The standard the status bits and their names come from.</summary>
        public const string StatusStandard = "ISO 14229-1";

        /// <summary>The ISO 14229-1 names of the status bits, bit 0 first.</summary>
        public static IReadOnlyList<string> IsoBitNames => BitNames;

        static readonly string[] BitNames =
        {
            "testFailed", "testFailedThisOperationCycle", "pendingDTC", "confirmedDTC",
            "testNotCompletedSinceLastClear", "testFailedSinceLastClear", "testNotCompletedThisOperationCycle", "warningIndicatorRequested"
        };

        /// <summary>The set bits by their ISO 14229-1 names; "clear" when none is set.</summary>
        public string StatusText
        {
            get
            {
                var bits = Enumerable.Range(0, 8).Where(b => (Status & (1 << b)) != 0).Select(b => BitNames[b]).ToList();
                return bits.Count > 0 ? string.Join(", ", bits) : "clear";
            }
        }

        /// <summary>The ISO 14229-1 name of a bit as saved fault reads may hold it in a shorter form.</summary>
        public static string IsoBitName(string bit) => bit switch
        {
            "failedThisCycle" => "testFailedThisOperationCycle",
            "pending" => "pendingDTC",
            "confirmed" => "confirmedDTC",
            "notCompletedSinceClear" => "testNotCompletedSinceLastClear",
            "failedSinceClear" => "testFailedSinceLastClear",
            "notCompletedThisCycle" => "testNotCompletedThisOperationCycle",
            "warningIndicator" => "warningIndicatorRequested",
            _ => bit
        };

        /// <summary>
        /// What the status byte means in practice. With mask 0xFF an ECU
        /// also lists codes whose only bits are "test not completed" - those
        /// are monitors that have not run yet, not faults.
        /// </summary>
        public string Severity =>
            (Status & 0x01) != 0 ? "ACTIVE" :           // failing right now
            (Status & 0xAE) != 0 ? "stored" :           // failed this cycle, pending, confirmed, failed since clear or warning light
            "no fault";

        public override string ToString() =>
            $"{Severity,-8} {Code}-{FailureType:X2}  [{Status:X2}] {StatusText}" + (FailureTypeName(FailureType) is string ft ? $"  ({ft})" : "");

        /// <summary>
        /// Decodes the record list returned by sub-function 0x02.
        /// Data layout after the sub-function echo: one availability mask
        /// byte, then repeating 4-byte records of DTC + status.
        /// </summary>
        public static List<Dtc> ParseList(byte[] data)
        {
            var list = new List<Dtc>();
            if (data.Length < 2) return list;

            // data[0] = echoed sub-function, data[1] = availability mask
            for (int i = 2; i + 3 < data.Length; i += 4)
            {
                int raw = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
                if (raw == 0) continue;                 // padding
                list.Add(new Dtc { Raw = raw, Status = data[i + 3] });
            }

            return list;
        }

        public string RawHex => $"{Raw:X6}";
    }

    public static class DtcRequests
    {
        // Status mask 0xFF asks for every fault the ECU is holding, whatever
        // its state - confirmed, pending, historic. A narrower mask (0x08 for
        // confirmed only) returns less but is what a workshop tool sends.
        public const byte AllStatuses = 0xFF;

        /// <summary>19 02 mask — list the faults.</summary>
        public static string List() => $"1902{AllStatuses:X2}";

        /// <summary>
        /// 19 04 dtc FF — freeze frame. The vehicle's state captured at the
        /// moment the fault was recorded.
        /// </summary>
        public static string Snapshot(int dtc) => $"1904{dtc:X6}FF";

        /// <summary>
        /// 19 06 dtc FF — extended data. Occurrence counters, ageing counters,
        /// how many operation cycles ago it happened. This is the closest
        /// thing to a fault history the standard defines.
        /// </summary>
        public static string Extended(int dtc) => $"1906{dtc:X6}FF";
    }
}
