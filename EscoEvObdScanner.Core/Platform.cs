using System.Text.Json.Serialization;

namespace EscoEvObdScanner
{
    /// <summary>
    /// What a car file says about the car as a whole: its name, how ECU
    /// discovery may probe it, where fault-code freeze frames keep the time and
    /// odometer, and how the dashboard should be scaled. Everything the code
    /// would otherwise have to know about one make.
    /// </summary>
    public sealed class PlatformDef
    {
        /// <summary>Shown in the car list, e.g. "Stellantis e-CMP".</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";

        /// <summary>The cars it covers, e.g. "Opel Mokka-e, Corsa-e, Peugeot e-208 ...".</summary>
        [JsonPropertyName("cars")] public string? Cars { get; set; }

        /// <summary>ELM327 protocol number (ATSP), e.g. 6 for 11-bit CAN at 500 kbit/s. Missing: <see cref="Scanner.DefaultProtocol"/>.</summary>
        [JsonPropertyName("elm_protocol")] public string? Protocol { get; set; }

        /// <summary>
        /// Lowest CAN ID a discovery sweep may transmit on. Below it some IDs
        /// carry the car's ordinary messages and a probe there can be taken for
        /// one of them. Missing: <see cref="Scanner.DefaultProbeFloor"/>.
        /// </summary>
        [JsonPropertyName("probe_from")] public string? ProbeFromHex { get; set; }

        /// <summary>The address range most of the car's ECUs live in, offered as a quick search.</summary>
        [JsonPropertyName("main_range")] public string[]? MainRange { get; set; }

        /// <summary>Identifiers that identify one car (pack serial and the like), on top of the ISO serial and VIN.</summary>
        [JsonPropertyName("identifying")] public List<string> Identifying { get; set; } = new();

        [JsonPropertyName("fault_time")] public FaultTimeDef? FaultTime { get; set; }

        [JsonPropertyName("gauges")] public GaugeLimits Gauges { get; set; } = new();

        /// <summary>The maker's battery warranty with its source, per language, for the report's SOH limit; none if the file does not say.</summary>
        [JsonPropertyName("warranty"), JsonConverter(typeof(CarText.NamesConverter))] public Dictionary<string, string>? Warranties { get; set; }
        [JsonIgnore] public string? Warranty => CarText.Pick(Warranties) ?? CarText.Any(Warranties);

        /// <summary>How the built-in simulated car moves (see <see cref="DemoDrive"/>); only where <see cref="Demo"/> is set.</summary>
        [JsonPropertyName("demo_drive")] public DemoDrive? DemoDrive { get; set; }

        /// <summary>Present when the built-in simulated car (recorded e-CMP data) can stand in for this car; the value itself is not used.</summary>
        [JsonPropertyName("demo")] public string? Demo { get; set; }

        /// <summary>The probe floor; a value discovery cannot use (outside 0x100-0x7EF) falls back to the default.</summary>
        [JsonIgnore] public int ProbeFrom => Catalog.ParseHexOrDefault(ProbeFromHex ?? "") is int p and >= 0x100 and <= 0x7EF ? p : Scanner.DefaultProbeFloor;

        [JsonIgnore]
        public (int From, int To)? Main =>
            MainRange is [var a, var b] && Catalog.ParseHexOrDefault(a) is int f and >= 0 && Catalog.ParseHexOrDefault(b) is int t && t >= f
                ? (f, t) : null;
    }

    /// <summary>
    /// Where freeze frames keep the moment a fault was set: a running clock
    /// that can be read now as well, and the identifiers holding the odometer.
    /// The clock is a plain unsigned number in steps of <see cref="TickSeconds"/>;
    /// the odometer is read through its identifier's catalogue expression, else
    /// taken as a plain number of km.
    /// </summary>
    public sealed class FaultTimeDef
    {
        [JsonPropertyName("clock")] public LiveSource? Clock { get; set; }

        /// <summary>Seconds per clock step.</summary>
        [JsonPropertyName("tick_seconds")] public double TickSeconds { get; set; } = 1;

        [JsonPropertyName("odometer_dids")] public List<string> OdometerDids { get; set; } = new();

        [JsonIgnore] public IReadOnlySet<int> Odometers => (OdometerDids ?? new()).Select(Catalog.ParseDid).Where(d => d >= 0).ToHashSet();
    }

    /// <summary>
    /// What the simulated car needs that the dashboard sources do not say: the motor speed per km/h
    /// (the drive ratio) and the ECUs that ignore TesterPresent, as the real ones do.
    /// </summary>
    public sealed class DemoDrive
    {
        [JsonPropertyName("rpm_per_kmh")] public double? RpmPerKmh { get; set; }
        [JsonPropertyName("ignore_tester_present")] public List<string> IgnoreTesterPresent { get; set; } = new();
    }

    /// <summary>One reply of an array spread over several: the identifier, bytes to skip, elements to take.</summary>
    public sealed class LivePart
    {
        [JsonPropertyName("did")] public string Did { get; set; } = "";
        [JsonPropertyName("skip")] public int Skip { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }
    }

    /// <summary>Dashboard gauge ranges, [min, max]. A car file should set them; the defaults only keep the gauges usable.</summary>
    public sealed class GaugeLimits
    {
        [JsonPropertyName("power_kw")] public double[] PowerKw { get; set; } = { -60, 110 };
        [JsonPropertyName("current_a")] public double[] CurrentA { get; set; } = { -150, 300 };
        [JsonPropertyName("speed_kmh")] public double[] SpeedKmh { get; set; } = { 0, 160 };
    }

    /// <summary>
    /// Where one dashboard value is read: the ECU, the identifier, how often,
    /// and for per-cell arrays how the bytes are laid out.
    ///
    /// A single value is decoded with <see cref="Expression"/> when several
    /// values share one reply (as on cars that pack a whole screen of data into
    /// one identifier), else with the identifier's catalogue expression, else
    /// as the whole reply read as one unsigned number.
    /// </summary>
    public sealed class LiveSource
    {
        [JsonPropertyName("ecu")] public string Ecu { get; set; } = "";

        /// <summary>One identifier, or for arrays split over several replies a list of them, read in order.</summary>
        [JsonPropertyName("did")] public string? Did { get; set; }
        [JsonPropertyName("dids")] public List<string>? DidList { get; set; }

        /// <summary>fast (every round), slow (every few rounds), cells or rare (less often still).</summary>
        [JsonPropertyName("poll")] public string Poll { get; set; } = "slow";

        [JsonPropertyName("expression")] public string? Expression { get; set; }

        /// <summary>Plausible range of the decoded value; anything outside is dropped (0xFFFF "not available" and the like).</summary>
        [JsonPropertyName("min")] public double? Min { get; set; }
        [JsonPropertyName("max")] public double? Max { get; set; }

        /// <summary>Applied after decoding: value * scale + offset. For arrays, to each raw element.</summary>
        [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
        [JsonPropertyName("offset")] public double Offset { get; set; }

        /// <summary>Arrays: bytes per element (1 to 4), bytes to skip at the start of each reply, and how many elements each reply must hold.</summary>
        [JsonPropertyName("bytes")] public int Bytes { get; set; } = 1;
        [JsonPropertyName("skip")] public int Skip { get; set; }
        [JsonPropertyName("count")] public int? Count { get; set; }

        /// <summary>Arrays: elements are two's complement (temperatures below zero).</summary>
        [JsonPropertyName("signed")] public bool Signed { get; set; }

        /// <summary>Arrays spread over replies laid out differently: each part with its own skip and count.</summary>
        [JsonPropertyName("parts")] public List<LivePart>? Parts { get; set; }

        /// <summary>Arrays: elements per row in the battery view.</summary>
        [JsonPropertyName("columns")] public int? Columns { get; set; }

        /// <summary>Yes/no values (charging, see LivePoller.YesNoValues): the codes that mean yes (cable connected); without it any value above 0 is yes. Ignored on other values.</summary>
        [JsonPropertyName("on")] public List<double>? On { get; set; }

        /// <summary>Dates: the byte order of a BCD date, "dmy" (day, month, year; the default) or "ymd".</summary>
        [JsonPropertyName("format")] public string? Format { get; set; }

        /// <summary>Codes to text, e.g. gear 0 → "P".</summary>
        [JsonPropertyName("map")] public Dictionary<string, string>? Map { get; set; }

        [JsonIgnore]
        public IReadOnlyList<int> Dids =>
            (Parts is { Count: > 0 } ? Parts.Select(p => p.Did) : DidList is { Count: > 0 } ? DidList : Did != null ? new List<string> { Did } : new List<string>())
            .Select(Catalog.ParseDid).Where(d => d >= 0).ToList();

        /// <summary>The replies an array is read from, each with where its elements are.</summary>
        [JsonIgnore]
        public IReadOnlyList<(int Did, int Skip, int? Count)> Pieces =>
            Parts is { Count: > 0 }
                ? Parts.Select(p => (Catalog.ParseDid(p.Did), p.Skip, p.Count)).Where(p => p.Item1 >= 0).ToList()
                : Dids.Select(d => (d, Skip, Count)).ToList();

        public string? Text(double? code) =>
            code is double c && Map != null && Map.TryGetValue(((long)c).ToString(System.Globalization.CultureInfo.InvariantCulture), out var t) ? t : null;
    }
}
