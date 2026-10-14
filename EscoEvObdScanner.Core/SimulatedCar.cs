using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace EscoEvObdScanner
{
    /// <summary>
    /// A stand-in for an ELM327 plugged into a car. It answers AT
    /// commands like the adapter and UDS requests like the ECUs, formatted the
    /// way a real ELM327 prints them (single frames, multi-frame "0:" "1:"
    /// lines, headers on or off), from values recorded on a real car.
    ///
    /// Used by the tests and as the demo car of the app and the command line.
    /// With <see cref="Dynamic"/> on, speed, current, state of charge, rpm,
    /// pack voltage and the cells move over time so the dashboard has something to show;
    /// which identifiers those are, and how they are encoded, comes from the car file's
    /// dashboard sources, and the motor speed per km/h from its demo_drive.
    /// </summary>
    public sealed class SimulatedCar : IElmLink
    {
        public const string Version = "ELM327 v1.4b (simulated)";

        public event Action<string>? Trace;

        /// <summary>Artificial delay per request, to make the demo feel like a real adapter.</summary>
        public int LatencyMs { get; set; }

        /// <summary>Animate the live values (speed, current, SOC, rpm, cells).</summary>
        public bool Dynamic { get; set; }

        /// <summary>
        /// Identifiers that answer "responsePending" first. The first request
        /// gets only 7F 22 78, as when the adapter stops listening too early;
        /// the retry gets 7F 22 78 followed by the real answer.
        /// </summary>
        public HashSet<(int Request, int Did)> PendingFirst { get; } = new();

        /// <summary>Every request payload sent to the car, AT commands excluded; the latest <see cref="MaxRequests"/> at most.</summary>
        public List<string> Requests { get; } = new();

        /// <summary>The demo car runs for hours: older requests make way beyond this many.</summary>
        public const int MaxRequests = 20000;

        readonly Dictionary<int, int> _responseFor = new();
        readonly Dictionary<(int Request, int Did), byte[]> _data = new();
        readonly Dictionary<int, List<(int Code, byte Status, byte[]? Frame)>> _dtcs = new();
        readonly HashSet<int> _ignoresTesterPresent = new();
        readonly HashSet<(int, int)> _pendingSeen = new();
        readonly DateTime _start = DateTime.UtcNow;
        readonly object _sync = new();
        readonly Catalog _catalog;

        int _header = 0x7DF;
        bool _headers;
        bool _open;

        /// <param name="catalog">Supplies the request/response ID of each ECU.</param>
        /// <param name="lines">
        /// JSON lines with ecu, did and hex, as written by snap and scan; a line
        /// with ecu, dtc (3 bytes hex), status and optionally frame (the freeze
        /// frame records, hex) is a stored fault code instead.
        /// </param>
        public SimulatedCar(Catalog catalog, IEnumerable<string> lines)
        {
            _catalog = catalog;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // a malformed line is skipped, as merge and diff do
                JsonObject? o;
                try { o = JsonNode.Parse(line)?.AsObject(); }
                catch (System.Text.Json.JsonException) { continue; }

                string? ecuId = Text(o, "ecu"), hex = Text(o, "hex");
                if (ecuId != null && Text(o, "dtc") is string dtc && Text(o, "status") is string status
                    && catalog.TryEcu(ecuId) is { } faultEcu
                    && int.TryParse(NoHexPrefix(dtc), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code)
                    && byte.TryParse(NoHexPrefix(status), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte st))
                {
                    _responseFor[faultEcu.Request] = faultEcu.Response;
                    byte[]? frame = Text(o, "frame") is string fh && fh.Length % 2 == 0 && fh.All(Uri.IsHexDigit) ? Convert.FromHexString(fh) : null;
                    AddDtc(faultEcu.Request, code, st, frame);
                    continue;
                }
                int did = Catalog.ParseHexOrDefault(Text(o, "did"));
                if (ecuId == null || hex == null || did < 0 || hex.Length % 2 != 0 || !hex.All(Uri.IsHexDigit)) continue;

                var ecu = catalog.TryEcu(ecuId);
                if (ecu == null) continue;

                _responseFor[ecu.Request] = ecu.Response;
                _data[(ecu.Request, did)] = Convert.FromHexString(hex);
            }
        }

        static string NoHexPrefix(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;

        static string? Text(JsonObject? o, string name) =>
            o?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

        /// <summary>
        /// The built-in demo car: values and fault codes from a real Mokka-e
        /// (in Ready, gear D, 80 % charge), serial numbers replaced and the
        /// identifiers that identify the car left out. The ECUs the car file's
        /// demo_drive names ignore TesterPresent, as the real ones do.
        /// </summary>
        public static SimulatedCar Demo(Catalog catalog)
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EscoEvObdScanner.Resources.demo-car.jsonl")
                ?? throw new InvalidOperationException("demo data missing from the build");
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');

            var car = new SimulatedCar(catalog, lines);
            foreach (var id in catalog.Platform.DemoDrive?.IgnoreTesterPresent ?? new())
                if (catalog.TryEcu(id) is { } ecu) car.IgnoreTesterPresent(ecu.Request);
            return car;
        }

        internal void IgnoreTesterPresent(int request) => _ignoresTesterPresent.Add(request);

        /// <param name="frame">The freeze frame records a 0x19 04 read returns, after the status byte; none if null.</param>
        internal void AddDtc(int request, int code, byte status, byte[]? frame = null)
        {
            if (!_dtcs.TryGetValue(request, out var list)) _dtcs[request] = list = new();
            list.Add((code, status, frame));
        }

        internal void SetData(int request, int did, byte[] data) => _data[(request, did)] = data;

        public bool IsOpen => _open;

        public void Open() => _open = true;

        public void Dispose() => _open = false;

        // -------------------------------------------------------------------

        public string[] Send(string command, int timeoutMs = 5000)
        {
            if (!_open) throw new InvalidOperationException("The port is closed.");
            if (LatencyMs > 0) Thread.Sleep(LatencyMs);

            var cmd = command.Replace(" ", "").ToUpperInvariant();
            string[] lines;
            lock (_sync) lines = cmd.StartsWith("AT") ? At(cmd) : Request(cmd);
            Trace?.Invoke($"[{command}] -> {string.Join("|", lines)}");
            return lines;
        }

        string[] At(string cmd)
        {
            if (cmd == "ATI") return new[] { Version };
            if (cmd == "ATZ") { _header = 0x7DF; _headers = false; return new[] { Version }; }   // back to defaults, like a real adapter
            if (cmd.StartsWith("ATSH")) _header = int.Parse(cmd.Substring(4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else if (cmd == "ATH0") _headers = false;
            else if (cmd == "ATH1") _headers = true;
            return new[] { "OK" };
        }

        string[] Request(string cmd)
        {
            Requests.Add(cmd);
            if (Requests.Count > MaxRequests) Requests.RemoveRange(0, Requests.Count - MaxRequests / 2);

            if (!_responseFor.TryGetValue(_header, out int response) || cmd.Length < 2 || cmd.Length % 2 != 0)
                return new[] { "NO DATA" };

            byte[] req = Convert.FromHexString(cmd);
            byte sid = req[0];

            switch (sid)
            {
                case 0x3E:
                    return _ignoresTesterPresent.Contains(_header)
                        ? new[] { "NO DATA" }
                        : Format(response, new byte[] { 0x7E, 0x00 });

                case 0x22 when req.Length == 3:
                {
                    int did = (req[1] << 8) | req[2];
                    if (!TryRead(_header, did, out var data))
                        return Format(response, new byte[] { 0x7F, 0x22, 0x31 });

                    var answer = Format(response, new byte[] { 0x62, req[1], req[2] }.Concat(data).ToArray());
                    if (!PendingFirst.Contains((_header, did))) return answer;

                    // first time: only the pending notice; afterwards notice + answer
                    return _pendingSeen.Add((_header, did))
                        ? new[] { "7F2278" }
                        : new[] { "7F2278" }.Concat(answer).ToArray();
                }

                case 0x19 when req.Length == 3 && req[1] == 0x02:
                {
                    var body = new List<byte> { 0x59, 0x02, 0xFF };
                    if (_dtcs.TryGetValue(_header, out var list))
                        foreach (var (code, status, _) in list.Where(d => (d.Status & req[2]) != 0))
                            body.AddRange(new[] { (byte)(code >> 16), (byte)(code >> 8), (byte)code, status });
                    return Format(response, body.ToArray());
                }

                case 0x19 when req.Length == 6 && req[1] == 0x04:
                {
                    // freeze frame of one stored fault: 59 04, the code, its status, then the records
                    int code = (req[2] << 16) | (req[3] << 8) | req[4];
                    if (!_dtcs.TryGetValue(_header, out var list) || list.FindIndex(d => d.Code == code) is var i && i < 0)
                        return Format(response, new byte[] { 0x7F, 0x19, 0x31 });
                    var fault = list[i];
                    return Format(response, new byte[] { 0x59, 0x04, req[2], req[3], req[4], fault.Status }
                        .Concat(fault.Frame ?? Array.Empty<byte>()).ToArray());
                }

                case 0x24:
                    // like most real ECUs: scaling data is not implemented
                    return Format(response, new byte[] { 0x7F, 0x24, 0x11 });

                default:
                    // everything else (0x19 06 ...) is politely refused
                    return Format(response, new byte[] { 0x7F, sid, 0x31 });
            }
        }

        bool TryRead(int request, int did, out byte[] data)
        {
            if (!_data.TryGetValue((request, did), out data!)) return false;
            if (Dynamic) data = Animate(request, did, data);
            return true;
        }

        /// <summary>
        /// A gentle drive cycle: speed swings between about 5 and 105 km/h
        /// over two minutes, current follows speed, SOC drifts down slowly.
        /// Each value goes to the identifier the car file's dashboard reads it
        /// from, encoded with that source's expression; several values sharing
        /// one identifier all go into the same reply. One the car file does
        /// not give, or that cannot be encoded, keeps its recorded value.
        /// </summary>
        byte[] Animate(int request, int did, byte[] recorded)
        {
            double t = (DateTime.UtcNow - _start).TotalSeconds;
            double speed = Math.Max(0, 55 + 50 * Math.Sin(t * 2 * Math.PI / 120));
            double current = speed < 1 ? 1.0 : speed * 0.9 + 15 * Math.Sin(t * 1.3);

            var reply = recorded;
            foreach (var (name, src) in _catalog.Live)
            {
                if (name.Equals("cells", StringComparison.OrdinalIgnoreCase))
                {
                    // every part of a cell array that lives in this reply sags, each with its own layout
                    if (_catalog.TryEcu(src.Ecu)?.Request != request) continue;
                    foreach (var piece in src.Pieces.Where(p => p.Did == did))
                        reply = Sag(src, piece.Skip, piece.Count, reply, current * 0.0007);
                    continue;
                }
                if (Source(src) is not { } at || at.Request != request || at.Did != did) continue;
                double? value = name.ToLowerInvariant() switch
                {
                    "speed" => speed,
                    "motor_rpm" => _catalog.Platform.DemoDrive?.RpmPerKmh is double ratio ? speed * ratio : null,
                    "current" => current,
                    "soc" => Recorded(src, at) - t / 600.0,
                    "voltage" => Recorded(src, at) - current * 0.08,
                    _ => null
                };
                if (value is double v && Encode(src, at.Expression, reply, v) is { } encoded) reply = encoded;
            }
            return reply;
        }

        /// <summary>The ECU request, identifier and decoding expression a dashboard source reads; null if the car file lacks the ECU.</summary>
        (int Request, int Did, string? Expression)? Source(LiveSource src) =>
            _catalog.TryEcu(src.Ecu) is { } ecu && src.Dids is [int did, ..]
                ? (ecu.Request, did, src.Expression ?? _catalog.Find(ecu.Id, did)?.Expression) : null;

        /// <summary>The recorded value of a source, decoded as the dashboard decodes it; null if none is recorded.</summary>
        double? Recorded(LiveSource src, (int Request, int Did, string? Expression) at) =>
            _data.TryGetValue((at.Request, at.Did), out var b) && at.Expression is string e && Expr.Evaluate(e, b) is double raw
                ? raw * src.Scale + src.Offset : null;

        /// <summary>
        /// The recorded reply with <paramref name="value"/> written into it: the bytes of the expression's
        /// first byte reference, set so that the expression (with the source's scale and offset) gives the value.
        /// Works for linear expressions in those bytes, such as B4/2, S[B4:B5]/10 or [B4:B7]/64-1200; null when
        /// the expression does not give the value back (not linear, or the first reference is not the value's bytes)
        /// or the value does not fit the field.
        /// </summary>
        internal static byte[]? Encode(LiveSource src, string? expression, byte[] recorded, double value)
        {
            if (expression == null) return null;
            var m = System.Text.RegularExpressions.Regex.Match(expression, @"(S?)(?:\[B(\d+):B(\d+)\]|B(\d+))");
            if (!m.Success) return null;
            int from = int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value, CultureInfo.InvariantCulture) - 4;
            int to = int.Parse(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value, CultureInfo.InvariantCulture) - 4;
            if (from < 0 || to < from || to >= recorded.Length || to - from >= 7) return null;
            bool signed = m.Groups[1].Value == "S";

            byte[] With(long raw)
            {
                var copy = (byte[])recorded.Clone();
                for (int i = to; i >= from; i--, raw >>= 8) copy[i] = (byte)raw;
                return copy;
            }
            double? Shown(long raw) => Expr.Evaluate(expression, With(raw)) is double d ? d * src.Scale + src.Offset : null;

            // a straight line through two points inside the field gives the raw value for the one wanted
            long span = 1L << (8 * (to - from + 1));
            long second = Math.Min(1000, (signed ? span / 2 : span) - 1);
            if (Shown(0) is not double at0 || Shown(second) is not double atSecond || atSecond == at0) return null;
            long wanted = (long)Math.Round((value - at0) / (atSecond - at0) * second);
            if (signed ? wanted < -span / 2 || wanted >= span / 2 : wanted < 0 || wanted >= span) return null;
            // the expression must give the value back to within half a step, else it is not the line assumed
            if (Shown(wanted) is not double back || Shown(1) is not double at1) return null;
            double step = Math.Abs(at1 - at0);
            if (Math.Abs(back - value) > step / 2 + 1e-9 * Math.Max(1, Math.Abs(value))) return null;
            return With(wanted);
        }

        /// <summary>
        /// The cells of one reply (after <paramref name="skip"/> bytes, at most <paramref name="count"/> of them)
        /// sag with the load by <paramref name="volts"/> each, keeping each cell's own offset.
        /// </summary>
        static byte[] Sag(LiveSource src, int skip, int? count, byte[] recorded, double volts)
        {
            int size = Math.Clamp(src.Bytes, 1, 4);
            long step = src.Scale > 0 ? (long)Math.Round(volts / src.Scale) : 0;
            var copy = (byte[])recorded.Clone();
            int end = count is int n && n >= 0 ? Math.Min(copy.Length, Math.Max(0, skip) + n * size) : copy.Length;
            for (int i = Math.Max(0, skip); i + size <= end; i += size)
            {
                long raw = 0;
                for (int k = 0; k < size; k++) raw = (raw << 8) | copy[i + k];
                if (raw == 0) break;            // unused slots after the cells
                raw = Math.Max(0, raw - step);
                for (int k = size - 1; k >= 0; k--, raw >>= 8) copy[i + k] = (byte)raw;
            }
            return copy;
        }

        /// <summary>
        /// Prints a response the way an ELM327 with CAF1 and S0 does. With
        /// headers off, anything over 7 bytes is shown as the adapter's
        /// assembled multi-frame form: a length line, then "0:" "1:" ...
        /// </summary>
        string[] Format(int response, byte[] payload)
        {
            string hex = Convert.ToHexString(payload);

            if (_headers)
                return new[] { $"{response:X3}{payload.Length:X2}{hex}" };

            if (payload.Length <= 7)
                return new[] { hex };

            var lines = new List<string> { payload.Length.ToString("X3") };
            int offset = 0, index = 0;
            while (offset < payload.Length)
            {
                int take = Math.Min(index == 0 ? 6 : 7, payload.Length - offset);
                var sb = new StringBuilder();
                sb.Append((index % 16).ToString("X")).Append(':');
                sb.Append(Convert.ToHexString(payload, offset, take));
                lines.Add(sb.ToString());
                offset += take;
                index++;
            }
            return lines.ToArray();
        }
    }
}
