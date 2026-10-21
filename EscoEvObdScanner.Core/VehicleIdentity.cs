using System.Security.Cryptography;

namespace EscoEvObdScanner
{
    /// <summary>
    /// A short hash that tells one car from another without keeping its serial number or VIN.
    /// Stored identities are compared with new ones, so the formula must never change.
    /// </summary>
    public static class VehicleIdentity
    {
        /// <summary>
        /// The first of the ECU serial number (ISO 14229 0xF18C) or the VIN (0xF190) that an ECU of
        /// the car file's dashboard values gives, the ECU of "soc" first, as a hash. An ECU is passed
        /// over when it answers without giving either, or when the adapter cannot address it; when one
        /// that comes earlier is addressed but gives no answer at all, the result is null, so the hash
        /// always comes from the same ECU of the same car.
        /// Null when none of them tells.
        /// </summary>
        public static string? Read(Scanner scanner, Catalog catalog) => Check(scanner, catalog).Id;

        /// <summary>
        /// As <see cref="Read"/>, and whether any of those ECUs answered at all, even with a refusal:
        /// none answering means the car file's addresses are not this car's, or the car is off.
        /// <c>Silent</c>: an ECU that would have given the identity was addressed but did not answer,
        /// which is why there is none (the car not fully awake).
        /// </summary>
        public static (string? Id, bool Answered, bool Silent) Check(Scanner scanner, Catalog catalog)
        {
            bool answered = false, earlierSilent = false;
            var ecus = new[] { catalog.LiveOf("soc")?.Ecu }.Concat(catalog.Live.Values.Select(l => l.Ecu))
                .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => catalog.TryEcu(id!)).OfType<EcuDef>();
            foreach (var ecu in ecus)
            {
                bool heard = false, addressed = false;
                try
                {
                    scanner.Target(ecu.Request, ecu.Response);
                    addressed = true;
                    foreach (int did in new[] { 0xF18C, 0xF190 })
                    {
                        var reply = scanner.ReadDid(did);
                        if (reply.Kind is ResponseKind.Positive or ResponseKind.Negative) answered = heard = true;
                        if (reply.Kind == ResponseKind.Positive && !IsBlank(reply.Data))
                            // an earlier ECU that did not answer would have given the hash: this one cannot stand in for it
                            return (earlierSilent ? null : Hash(did, reply.Data), true, earlierSilent);
                    }
                }
                catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException) { }    // this ECU cannot be addressed; the next may tell
                // an ECU the adapter cannot address is passed over on every connection alike
                if (addressed && !heard) earlierSilent = true;
            }
            return (null, answered, earlierSilent);
        }

        /// <summary>
        /// An unprogrammed serial number: empty, or one byte repeated (all 0x00, 0xFF, spaces, '0'...).
        /// Every car of a model may give the same, so it tells cars apart no better than nothing.
        /// </summary>
        internal static bool IsBlank(byte[] data) => data.Length == 0 || data.All(b => b == data[0]);

        /// <summary>
        /// The VIN (ISO 14229 0xF190) from the first ECU that gives a valid one: those whose identifiers
        /// in the car file list it first, then the ECUs of the dashboard values. Null when none does.
        /// </summary>
        public static string? ReadVin(Scanner scanner, Catalog catalog)
        {
            var listed = catalog.Ecus.Where(e => e.Dids.Concat(e.Discovered).Any(d => d.Did == 0xF190)).Select(e => e.Id);
            var ecus = listed.Concat(catalog.Live.Values.Select(l => l.Ecu))
                .Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => catalog.TryEcu(id!)).OfType<EcuDef>();
            foreach (var ecu in ecus)
            {
                try
                {
                    scanner.Target(ecu.Request, ecu.Response);
                    var reply = scanner.ReadDid(0xF190);
                    if (reply.Kind == ResponseKind.Positive && Vin(reply.Data) is string vin) return vin;
                }
                catch (Exception ex) when (ex is AdapterException { Problem: not AdapterProblem.SetupRefused } or ArgumentOutOfRangeException) { }
            }
            return null;
        }

        /// <summary>A VIN from a reply: 17 letters and digits (padding around them dropped), never I, O or Q; else null.</summary>
        public static string? Vin(byte[] data)
        {
            string text = new string(data.Select(b => (char)b).ToArray()).Trim('\0', ' ', '\xFF');
            return text.Length == 17 && text.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' && c is not ('I' or 'O' or 'Q')) ? text : null;
        }

        /// <summary>The first 16 hex digits of SHA-256 over the identifier (4 bytes, little-endian) and its reply.</summary>
        public static string Hash(int did, byte[] data) =>
            Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(did).Concat(data).ToArray()))[..16];
    }
}
