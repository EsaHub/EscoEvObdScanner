using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EscoEvObdScanner
{
    /// <summary>
    /// How Bluetooth LE adapters are found and talked to, from ble-adapters.json: which
    /// advertised names are listed as adapters, which GATT services are tried first for the
    /// adapter's text, which are never used, and the PIN given to an adapter that asks for one
    /// when pairing. A missing or broken file gives
    /// <see cref="Default"/>; a file that leaves a list out keeps that list's default, and the
    /// firmware update services of <see cref="Default"/> are never used whatever a file says.
    /// </summary>
    public sealed class BleAdapters
    {
        /// <summary>The file name, next to the program and in the user's config folder.</summary>
        public const string FileName = "ble-adapters.json";

        /// <summary>Advertised names listed as adapters (case does not matter).</summary>
        public Regex AdapterName { get; }

        /// <summary>Services tried before the others for the adapter's text.</summary>
        public IReadOnlyList<Guid> PreferredServices { get; }

        /// <summary>Services never used: standard ones and firmware update ones.</summary>
        public IReadOnlyList<Guid> NeverUsedServices { get; }

        /// <summary>The PIN given to an adapter that asks for one when pairing (OBDLink CX: 123456).</summary>
        public string PairingPin { get; }

        BleAdapters(string adapterName, IEnumerable<Guid> preferred, IEnumerable<Guid> neverUsed, string pairingPin)
        {
            PairingPin = pairingPin;
            AdapterName = new Regex(adapterName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            PreferredServices = preferred.Distinct().ToList();
            NeverUsedServices = neverUsed.Distinct().ToList();
        }

        // firmware update services: never used, whatever a file says
        static readonly Guid[] UpdateServices =
        {
            Uuid("FE59"),                                     // Nordic Secure DFU
            Uuid("00001530-1212-efde-1523-785feabcd123"),     // Nordic legacy DFU
            Uuid("8e400001-f315-4f60-9fb8-838830daea50"),     // Nordic buttonless DFU
            Uuid("f000ffc0-0451-4000-b000-000000000000"),     // TI over-the-air download
        };

        /// <summary>What applies without a file.</summary>
        public static BleAdapters Default { get; } = new(
            "obd|elm|link|vgate|icar|veepeak|konnwei|carista|scan",
            new[] { Uuid("FFF0"), Uuid("FFE0"), Uuid("18F0") },
            new[] { Uuid("1800"), Uuid("1801"), Uuid("180A"), Uuid("180F") }.Concat(UpdateServices),
            "123456");

        sealed class Raw
        {
            [JsonPropertyName("adapter_names")] public string? AdapterNames { get; set; }
            [JsonPropertyName("preferred_services")] public List<string>? PreferredServices { get; set; }
            [JsonPropertyName("never_used_services")] public List<string>? NeverUsedServices { get; set; }
            [JsonPropertyName("pairing_pin")] public string? PairingPin { get; set; }
        }

        /// <summary>
        /// The first of <paramref name="paths"/> that exists, read; <see cref="Default"/> when none does.
        /// <paramref name="problem"/> tells why a file that exists was not used, else null.
        /// </summary>
        public static BleAdapters Load(out string? problem, params string[] paths)
        {
            problem = null;
            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var raw = JsonSerializer.Deserialize<Raw>(File.ReadAllText(path),
                        new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                        ?? throw new JsonException("empty");
                    return new BleAdapters(
                        string.IsNullOrWhiteSpace(raw.AdapterNames) ? Default.AdapterName.ToString() : raw.AdapterNames,
                        raw.PreferredServices?.Select(Uuid) ?? Default.PreferredServices,
                        (raw.NeverUsedServices?.Select(Uuid) ?? Default.NeverUsedServices).Concat(UpdateServices),
                        string.IsNullOrWhiteSpace(raw.PairingPin) ? Default.PairingPin : raw.PairingPin.Trim());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                           or FormatException or ArgumentException)
                {
                    problem = $"{path}: {ex.Message}";
                    return Default;
                }
            }
            return Default;
        }

        /// <summary>A 16-bit short id ("FFF0") as a Bluetooth base UUID, or a full UUID as such.</summary>
        public static Guid Uuid(string text)
        {
            text = text.Trim();
            if (text.Length == 4 && ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort id))
                return new Guid($"0000{id:x4}-0000-1000-8000-00805f9b34fb");
            return Guid.Parse(text);
        }
    }
}
