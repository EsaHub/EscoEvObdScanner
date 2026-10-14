using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EscoEvObdScanner
{
    // -----------------------------------------------------------------------
    // Typed view of a car file (cars/*.json): one per platform.
    //
    // "platform" describes the car (see PlatformDef), "live" says where the
    // dashboard reads each value (see LiveSource), and every ECU carries its
    // own identifiers:
    //
    //   "ecus": [ { "id": "bms", "request": "0x6B4", ...,
    //               "dids":       [ worked-out identifiers ],
    //               "discovered": [ found by scans, meaning still open ],
    //               "scanned":    [ ranges scanned, with when and what was found ] } ]
    //
    // and "ecu_search" lists the request addresses an ECU search has covered, in the same form.
    //
    // Callers also get every identifier in single Dids/Discovered lists, with Ecu set.
    //
    // Note the asymmetry: reading uses these types, but writing back (merge)
    // goes through JsonNode instead. Deserialising into a partial model and
    // re-serialising would silently drop every field not modelled here.
    // Mutating the document tree preserves them.
    // -----------------------------------------------------------------------
    public sealed class EcuDef
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name"), JsonConverter(typeof(CarText.NamesConverter))] public Dictionary<string, string>? Names { get; set; }

        /// <summary>The name in the UI language (see <see cref="CarText"/>), else the id.</summary>
        [JsonIgnore] public string Name => CarText.Pick(Names) ?? CarText.Any(Names) ?? Id;
        [JsonPropertyName("request")] public string RequestHex { get; set; } = "";
        [JsonPropertyName("response")] public string ResponseHex { get; set; } = "";
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("dids")] public List<DidDef> Dids { get; set; } = new();
        [JsonPropertyName("discovered")] public List<DidDef> Discovered { get; set; } = new();

        /// <summary>
        /// False for an ECU that has not answered this car (an address from a
        /// public list): fault code reads skip it unless asked. Missing means it answers.
        /// </summary>
        [JsonPropertyName("answered")] public bool? Answered { get; set; }

        /// <summary>The identifier ranges scanned on this ECU, with when and what was found.</summary>
        [JsonPropertyName("scanned")] public List<ScannedRange> Scanned { get; set; } = new();

        [JsonIgnore] public int Request => Catalog.ParseHexOrDefault(RequestHex);
        [JsonIgnore] public int Response => Catalog.ParseHexOrDefault(ResponseHex);
    }

    /// <summary>
    /// A scanned range, "0xD000-0xDFFF". Step 16 means only every 16th
    /// identifier was read (a sample), so the range is not scanned in full.
    /// </summary>
    public sealed class ScannedRange
    {
        [JsonPropertyName("range")] public string RangeText { get; set; } = "";
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("step")] public int? Step { get; set; }

        // -1 for a malformed range: one bad entry must not break the others
        [JsonIgnore] public int From => Part(0);
        [JsonIgnore] public int To => Part(1);
        [JsonIgnore] public bool IsValid => From >= 0 && To >= From && To <= 0xFFFF;
        [JsonIgnore] public bool IsFull => Step is null or <= 1;

        int Part(int i) => RangeText?.Split('-') is { Length: 2 } p ? Catalog.ParseHexOrDefault(p[i]) : -1;

        /// <summary>
        /// How many identifiers of <paramref name="from"/>..<paramref name="to"/> the full
        /// ranges cover (overlaps counted once), and every range, full or sample, that overlaps it.
        /// </summary>
        public static (int Covered, List<ScannedRange> Overlapping) Coverage(IEnumerable<ScannedRange> ranges, int from, int to)
        {
            var overlapping = ranges.Where(r => r.IsValid && r.From <= to && r.To >= from).OrderBy(r => r.From).ToList();
            int covered = 0, next = from;
            foreach (var r in overlapping.Where(r => r.IsFull))
            {
                int a = Math.Max(r.From, next), b = Math.Min(r.To, to);
                if (b >= a) { covered += b - a + 1; next = b + 1; }
            }
            return (covered, overlapping);
        }
    }

    public sealed class DidDef
    {
        [JsonPropertyName("did")] public string DidHex { get; set; } = "";
        /// <summary>The owning ECU's id. Filled in from the enclosing ECU on load.</summary>
        [JsonIgnore] public string Ecu { get; set; } = "";
        [JsonPropertyName("name"), JsonConverter(typeof(CarText.NamesConverter))] public Dictionary<string, string>? Names { get; set; }

        /// <summary>The name in the UI language (see <see cref="CarText"/>); empty for an identifier without a name.</summary>
        [JsonIgnore] public string Name => CarText.Pick(Names) ?? CarText.Any(Names) ?? "";

        /// <summary>The English name, for the command line and saved files; null without a name.</summary>
        [JsonIgnore] public string? EnglishName => CarText.English(Names);
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("expression")] public string? Expression { get; set; }
        [JsonPropertyName("encoding")] public string? Encoding { get; set; }
        [JsonPropertyName("confidence")] public string? Confidence { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        /// <summary>The ids of the car file's sources the entry rests on ("evdash", "own-scan" ...).</summary>
        [JsonPropertyName("source")] public List<string>? Sources { get; set; }

        /// <summary>The source ids for display: "evdash; own-scan".</summary>
        [JsonIgnore] public string? Source => Sources is { Count: > 0 } s ? string.Join("; ", s) : null;
        [JsonPropertyName("bytes")] public int? ByteCount { get; set; }

        /// <summary>Payload length if the catalogue says or a scan saw it.</summary>
        [JsonIgnore] public int? Length => ByteCount ?? (Samples?.FirstOrDefault()?.Hex is { Length: > 0 } h ? h.Length / 2 : null);

        /// <summary>Raw replies seen for this identifier, one per distinct value, kept by merge and by a car file update.</summary>
        [JsonPropertyName("samples")] public List<DidSample>? Samples { get; set; }

        /// <summary>How the ECU answered the scan: "positive", or a refusal such as "negative:conditionsNotCorrect".</summary>
        [JsonPropertyName("kind")] public string? Kind { get; set; }

        /// <summary>"rarely" for a value that seldom changes (versions, configuration).</summary>
        [JsonPropertyName("changes")] public string? Changes { get; set; }

        [JsonIgnore] public int Did => Catalog.ParseDid(DidHex);
        [JsonIgnore] public bool IsAscii => string.Equals(Encoding, "ascii", StringComparison.OrdinalIgnoreCase);

        /// <summary>The identifier exists but the ECU refused to give its value when scanned.</summary>
        [JsonIgnore] public bool Refused => Kind?.StartsWith("negative:", StringComparison.Ordinal) == true;

        /// <summary>The value seldom changes.</summary>
        [JsonIgnore] public bool Rare => string.Equals(Changes, "rarely", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a snap or a read of the ECUs tab reads this identifier: rare and refused ones only when asked for.</summary>
        public bool ReadWith(bool rare, bool refused) => (rare || !Rare) && (refused || !Refused);
    }

    public sealed class DidSample
    {
        [JsonPropertyName("ts")] public string? Time { get; set; }
        [JsonPropertyName("hex")] public string? Hex { get; set; }
    }

    public sealed class BlockDef
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("range")] public string[]? Range { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        /// <summary>Where the block comes in the scan plan: 0 first.</summary>
        [JsonPropertyName("order")] public int Order { get; set; }
        [JsonPropertyName("ecus")] public string[]? Ecus { get; set; }
        [JsonPropertyName("name"), JsonConverter(typeof(CarText.NamesConverter))] public Dictionary<string, string>? Names { get; set; }

        /// <summary>The name in the UI language (see <see cref="CarText"/>), else the id.</summary>
        [JsonIgnore] public string Name => CarText.Pick(Names) ?? CarText.Any(Names) ?? Id;
        [JsonPropertyName("why")] public string? Why { get; set; }
        [JsonPropertyName("runtime")] public string? Runtime { get; set; }

        [JsonIgnore, MemberNotNullWhen(true, nameof(Range))]
        public bool IsScannable => Range != null && Range.Length == 2;
        // -1 for a block without a range or a malformed one: one bad entry must not break every lookup
        [JsonIgnore] public int From => IsScannable ? Catalog.ParseHexOrDefault(Range[0]) : -1;
        [JsonIgnore] public int To => IsScannable ? Catalog.ParseHexOrDefault(Range[1]) : -1;
    }

    public sealed class Catalog
    {
        [JsonPropertyName("ecus")] public List<EcuDef> Ecus { get; set; } = new();
        [JsonPropertyName("blocks")] public List<BlockDef> Blocks { get; set; } = new();
        [JsonPropertyName("platform")] public PlatformDef Platform { get; set; } = new();

        /// <summary>Dashboard value name (soc, voltage, cells ...) to where it is read.</summary>
        [JsonPropertyName("live")] public Dictionary<string, LiveSource> Live { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Request address ranges an ECU search has covered, with when and what answered.</summary>
        [JsonPropertyName("ecu_search")] public List<ScannedRange> EcuSearch { get; set; } = new();

        /// <summary>
        /// Identifiers whose value identifies one car, so merge never stores it:
        /// the ISO 14229 serial number (F18C) and VIN (F190), which every make
        /// uses, plus whatever the car file lists.
        /// </summary>
        [JsonIgnore]
        public IReadOnlySet<int> Identifying =>
            new[] { 0xF18C, 0xF190 }.Concat(Platform.Identifying.Select(ParseHexOrDefault).Where(d => d >= 0)).ToHashSet();

        /// <summary>The car's name for lists: the platform name, else the file name.</summary>
        [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(Platform.Name) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Platform.Name;

        public LiveSource? LiveOf(string name) => Live.TryGetValue(name, out var s) ? s : null;

        /// <summary>Every worked-out identifier of every ECU, with <see cref="DidDef.Ecu"/> set.</summary>
        [JsonIgnore] public List<DidDef> Dids { get; private set; } = new();

        /// <summary>
        /// Every identifier found by scans but not worked out yet. A snapshot of
        /// the file at load time: merge writes the file directly, so load the
        /// catalogue again to see its result.
        /// </summary>
        [JsonIgnore] public List<DidDef> Discovered { get; private set; } = new();

        [JsonIgnore] public string Path { get; private set; } = "";

        static readonly JsonSerializerOptions Opts = new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static Catalog Load(string path)
        {
            var c = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), Opts)
                    ?? throw new InvalidDataException("the file is empty");
            c.Path = path;
            // a null where a list or object belongs (a hand-edited file) reads as empty, not as a crash
            c.Platform ??= new();
            c.Platform.Identifying ??= new();
            c.Platform.Gauges ??= new();
            c.Blocks = (c.Blocks ?? new()).Where(b => b != null).ToList();
            // an ECU is kept only with an address Scanner.Target accepts: an 11-bit request of 0x100 or more
            // (below it the car's own messages live) and an 11-bit reply address
            c.Ecus = (c.Ecus ?? new()).Where(e => e != null && e.Request is >= 0x100 and <= 0x7FF && e.Response is >= 0 and <= 0x7FF).ToList();
            if (c.Platform.FaultTime is { } ft) ft.OdometerDids ??= new();
            // names compare without case; of two spellings of one name the first wins
            var live = new Dictionary<string, LiveSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, source) in c.Live ?? new())
                if (source != null) live.TryAdd(name, source);
            c.Live = live;
            foreach (var l in c.Live.Values)
            {
                l.Parts = l.Parts?.Where(p => p != null).ToList();
                // a misspelt "poll" would never be read: it reads as the default instead
                l.Poll = (l.Poll ?? "").ToLowerInvariant();
                if (l.Poll is not ("fast" or "slow" or "cells" or "rare")) l.Poll = "slow";
            }

            // ECU search ranges are request addresses: one reaching past 0x7FF is not one
            c.EcuSearch = (c.EcuSearch ?? new()).Where(s => s is { IsValid: true, To: <= 0x7FF }).ToList();
            foreach (var ecu in c.Ecus)
            {
                ecu.Dids = (ecu.Dids ?? new()).Where(d => d != null).ToList();
                ecu.Discovered = (ecu.Discovered ?? new()).Where(d => d != null).ToList();
                ecu.Scanned = (ecu.Scanned ?? new()).Where(s => s is { IsValid: true }).ToList();
                foreach (var d in ecu.Dids) { d.Ecu = ecu.Id; c.Dids.Add(d); }
                foreach (var d in ecu.Discovered) { d.Ecu = ecu.Id; c.Discovered.Add(d); }
            }
            return c;
        }

        public EcuDef Ecu(string id) =>
            Ecus.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No ECU '{id}' in the catalogue");

        public EcuDef? TryEcu(string id) =>
            Ecus.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

        public DidDef? Find(string ecu, int did) =>
            Dids.FirstOrDefault(d => d.Did == did &&
                                     string.Equals(d.Ecu, ecu, StringComparison.OrdinalIgnoreCase));

        /// <summary>A data identifier (0x0000-0xFFFF) in hex, with or without 0x; -1 for anything else.</summary>
        public static int ParseDid(string? s) => ParseHexOrDefault(s) is int d and <= 0xFFFF ? d : -1;

        /// <summary>Parses hex with or without 0x; -1 for anything that is not hex or does not fit an int.</summary>
        public static int ParseHexOrDefault(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return -1;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

    }

    // -----------------------------------------------------------------------
    // Expression evaluator for the catalogue's "expression" field.
    //
    // Byte indexing counts from the start of the assembled response,
    // INCLUDING the length byte:
    //
    //   B0 = length, B1 = 0x62, B2 = DID hi, B3 = DID lo, B4.. = payload
    //
    // UdsResponse.Data holds only the payload, so B4 maps to Data[0].
    //
    // Grammar:  expr := term (('+'|'-') term)*
    //           term := factor (('*'|'/') factor)*
    //           factor := number | ['S'] byteref | '(' expr ')' | '-' factor
    //           byteref := 'B'<n> | '[' 'B'<a> ':' 'B'<b> ']'
    //
    // [B4:B5] is a big-endian concatenation of bytes 4 through 5.
    // An 'S' prefix reads the bytes as two's complement signed:
    // SB4 is -128..127, S[B4:B5] is -32768..32767.
    // -----------------------------------------------------------------------
    public static class Expr
    {
        const int PayloadOffset = 4;

        /// <summary>Longer expressions are refused rather than parsed: deep nesting would exhaust the stack.</summary>
        public const int MaxExpressionLength = 256;

        public static double? Evaluate(string? expression, byte[] payload)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;
            if (expression.Length > MaxExpressionLength) return null;   // the parser recurses per bracket
            try
            {
                int pos = 0;
                double value = ParseExpr(expression, ref pos, payload);
                SkipSpace(expression, ref pos);
                // dividing by a zero byte gives infinity or NaN, which JSON cannot hold
                return pos == expression.Length && double.IsFinite(value) ? value : null;
            }
            catch
            {
                return null;    // malformed expression or payload too short
            }
        }

        static double ParseExpr(string s, ref int i, byte[] p)
        {
            double left = ParseTerm(s, ref i, p);
            while (true)
            {
                SkipSpace(s, ref i);
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    char op = s[i++];
                    double right = ParseTerm(s, ref i, p);
                    left = op == '+' ? left + right : left - right;
                }
                else return left;
            }
        }

        static double ParseTerm(string s, ref int i, byte[] p)
        {
            double left = ParseFactor(s, ref i, p);
            while (true)
            {
                SkipSpace(s, ref i);
                if (i < s.Length && (s[i] == '*' || s[i] == '/'))
                {
                    char op = s[i++];
                    double right = ParseFactor(s, ref i, p);
                    left = op == '*' ? left * right : left / right;
                }
                else return left;
            }
        }

        static double ParseFactor(string s, ref int i, byte[] p)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");

            if (s[i] == '-') { i++; return -ParseFactor(s, ref i, p); }

            if (s[i] == '(')
            {
                i++;
                double v = ParseExpr(s, ref i, p);
                Expect(s, ref i, ')');
                return v;
            }

            if (s[i] == 'S' || s[i] == 's')
            {
                i++;
                SkipSpace(s, ref i);
                var (value, bits) = ParseByteRef(s, ref i, p);
                long sign = 1L << (bits - 1);
                return value >= sign ? value - (sign << 1) : value;
            }

            if (s[i] == '[' || s[i] == 'B' || s[i] == 'b') return ParseByteRef(s, ref i, p).Value;

            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (start == i) throw new FormatException($"unexpected '{s[i]}'");
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Reads B4 or [B4:B5] and returns the unsigned value together with
        /// its width in bits, which the signed prefix needs.
        /// </summary>
        static (long Value, int Bits) ParseByteRef(string s, ref int i, byte[] p)
        {
            if (s[i] != '[')
                return (Byte(p, ParseByteIndex(s, ref i)), 8);

            // [B4:B5] — big-endian run of bytes
            i++;
            int a = ParseByteIndex(s, ref i);
            Expect(s, ref i, ':');
            int b = ParseByteIndex(s, ref i);
            Expect(s, ref i, ']');

            int count = b - a + 1;
            if (count < 1 || count > 7) throw new FormatException($"[B{a}:B{b}] must span 1 to 7 bytes");

            long acc = 0;
            for (int n = a; n <= b; n++) acc = (acc << 8) | Byte(p, n);
            return (acc, count * 8);
        }

        static int ParseByteIndex(string s, ref int i)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length || (s[i] != 'B' && s[i] != 'b'))
                throw new FormatException("expected a byte reference");
            i++;
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            return int.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        static byte Byte(byte[] payload, int index)
        {
            int actual = index - PayloadOffset;
            if (actual < 0 || actual >= payload.Length)
                throw new IndexOutOfRangeException($"B{index} is outside this response");
            return payload[actual];
        }

        static void Expect(string s, ref int i, char c)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != c) throw new FormatException($"expected '{c}'");
            i++;
        }

        static void SkipSpace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
