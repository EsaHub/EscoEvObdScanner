using System.Globalization;
using System.Text;

namespace EscoEvObdScanner
{
    public enum ResponseKind
    {
        Positive,       // service + 0x40 (0x62 for a read): the request was answered
        Negative,       // 0x7F <service> <NRC>
        NoData,         // adapter timed out waiting for the ECU
        Pending,        // only 0x7F <sid> 0x78 arrived; the real answer came too late
        AdapterError    // CAN ERROR, BUFFER FULL, ?, etc.
    }

    public sealed class UdsResponse
    {
        public ResponseKind Kind;
        public byte[] Data = Array.Empty<byte>();   // after the response service byte, and after the echoed DID for ReadDid/ReadScaling
        public byte Nrc;

        /// <summary>The reply held a late answer to an earlier request: the ECU answers slower than the adapter waits.</summary>
        public bool LateAnswer;

        /// <summary>
        /// True when the identifier is real. A plain 0x31 (requestOutOfRange)
        /// means it does not exist; 0x33 and 0x22 mean it exists but is locked
        /// or unavailable right now, which is worth recording.
        ///
        /// 0x11 and 0x7F are about the service, not the identifier: an ECU
        /// that sends them sends them for every identifier alike.
        /// </summary>
        public bool Exists =>
            Kind == ResponseKind.Positive ||
            (Kind == ResponseKind.Negative && (Nrc == 0x33 || Nrc == 0x22));

        public string KindLabel => Kind switch
        {
            ResponseKind.Positive => "positive",
            ResponseKind.Negative => $"negative:{NrcName(Nrc)}",
            ResponseKind.NoData => "no-data",
            ResponseKind.Pending => "pending",
            _ => "adapter-error"
        };

        public static string NrcName(byte nrc) => nrc switch
        {
            0x10 => "generalReject",
            0x11 => "serviceNotSupported",
            0x12 => "subFunctionNotSupported",
            0x13 => "incorrectMessageLength",
            0x14 => "responseTooLong",
            0x21 => "busyRepeatRequest",
            0x22 => "conditionsNotCorrect",
            0x31 => "requestOutOfRange",
            0x33 => "securityAccessDenied",
            0x78 => "responsePending",
            0x7E => "subFunctionNotSupportedInSession",
            0x7F => "serviceNotSupportedInSession",
            _ => $"unknown0x{nrc:X2}"
        };
    }

    public sealed class Scanner
    {
        readonly IElmLink _elm;
        /// <summary>This program's normal wait (ATST, 4 ms units): 0x20 = 128 ms (the ELM327's own default is 200 ms).</summary>
        const int DefaultTimeout = 0x20;
        int _stTimeout = DefaultTimeout;

        /// <summary>The adapter's wait (ATST, 4 ms units) once an ECU has proved slower than the normal 128 ms: 400 ms.</summary>
        const int SlowEcuTimeout = 0x64;

        /// <summary>ECUs that have answered slower than the default wait.</summary>
        readonly HashSet<(int Request, int Response)> _slowTargets = new();

        /// <summary>Set when a reply shows the adapter reset (banner, echo or default formatting); the next request or Target re-runs Init.</summary>
        bool _needsInit;

        /// <summary>The ECU the adapter is pointed at, to restore after a reset.</summary>
        (int Request, int Response)? _target;

        /// <summary>
        /// Lowest CAN ID a discovery sweep transmits on when the car file does not
        /// say (platform.probe_from). Lower IDs often carry the car's ordinary
        /// messages, and a probe there can be taken for one of them; on e-CMP such
        /// a probe sets stored fault codes in the VCU and the power steering. Known
        /// diagnostic IDs below the floor are probed one by one through
        /// <see cref="ProbeKnown"/>.
        /// </summary>
        public const int DefaultProbeFloor = 0x600;

        /// <summary>ELM327 protocol when the car file does not say: 6 = ISO 15765-4 CAN, 11-bit ID, 500 kbit/s.</summary>
        public const string DefaultProtocol = "6";

        /// <summary>Lowest CAN ID <see cref="DiscoverEcus"/> accepts; set from the car file.</summary>
        public int ProbeFloor { get; set; } = DefaultProbeFloor;

        readonly string _protocol;

        /// <summary>The only services this program sends: read data, read scaling, read fault codes, TesterPresent.</summary>
        public static readonly IReadOnlySet<byte> ReadOnlyServices = new HashSet<byte> { 0x19, 0x22, 0x24, 0x3E };

        /// <summary>
        /// The ELM327 protocols this code can drive: 11-bit CAN at 500 (6) or
        /// 250 kbit/s (8). Headers, filters and discovery all assume 11-bit IDs,
        /// and an automatic search would read as an adapter reset.
        /// </summary>
        public static readonly IReadOnlySet<string> SupportedProtocols = new HashSet<string> { "6", "8" };

        public Scanner(IElmLink elm, string protocol = DefaultProtocol)
        {
            _elm = elm;
            _protocol = string.IsNullOrWhiteSpace(protocol) ? DefaultProtocol : protocol.Trim().ToUpperInvariant();
            // the value goes to the adapter as part of a command: nothing else may get through
            if (!SupportedProtocols.Contains(_protocol))
                throw new ArgumentException($"Protocol '{protocol}' is not supported; use 6 (11-bit CAN, 500 kbit/s) or 8 (250 kbit/s).", nameof(protocol));
        }

        /// <summary>
        /// Adapter reset and protocol setup, with the protocol the car file
        /// names (6, ISO 15765-4 CAN 11-bit 500 kbit/s, unless it says otherwise).
        /// </summary>
        public void Init()
        {
            // a setup cut short is done again before the next request
            try { Setup(); }
            catch { _needsInit = true; throw; }
        }

        void Setup()
        {
            _elm.Send("ATZ", 8000);
            Thread.Sleep(1200);                 // the reset takes a moment
            _elm.Send("ATE0");                  // no command echo
            _elm.Send("ATL0");                  // no linefeeds
            // echo still on would make every reply look like one after a reset: ATE0 is tried once more
            if (_elm.LastReplyEchoed)
            {
                _elm.Send("ATE0");
                _elm.Send("ATL0");
            }
            if (_elm.LastReplyEchoed)
                throw new AdapterException(AdapterProblem.SetupRefused, "The adapter did not accept ATE0: it still echoes every command.", "ATE0");
            ExpectSetup("ATS0");                // no spaces in responses: the parser depends on it
            _elm.Send("ATH1");                  // headers on; Target turns them off, discovery on again
            ExpectSetup("ATCAF1");              // auto formatting: adapter handles ISO-TP
            ExpectSetup($"ATSP{_protocol}");    // CAN protocol, from the car file
            _elm.Send("ATAT0");                 // fixed timing — predictable scan rate
            _elm.Send($"ATST{_stTimeout:X2}");  // current wait, units of 4 ms (128 ms, or 400 ms once an ECU proved slow)
        }

        public string Version() => string.Join(" ", _elm.Send("ATI"));

        /// <summary>
        /// Sends one request under the current header and returns the CAN ID
        /// of the first line whose payload starts with one of the expected
        /// prefixes, or -1 if nothing answered (or the adapter reset and then
        /// refused the header, see _probeHeaderLost).
        /// </summary>
        int Probe(string request, params string[] expected)
        {
            var lines = _elm.Send(request, 400);

            // A reset drops the wide filter and protocol, and every later ID would
            // look silent. Set the adapter up again and ask once more.
            if (_elm.LastReplyEchoed || lines.Any(l => IsResetBanner(Clean(l)) || LooksLikeDefaults(l)))
            {
                string header = _probeHeader;
                Init();
                _needsInit = false;
                SetUpProbing();
                // refused, the probe would go out under the default header and its answer be filed under this ID
                if (!_elm.Send(header).Any(l => l.Contains("OK", StringComparison.OrdinalIgnoreCase)))
                {
                    _probeHeaderLost = true;
                    return -1;
                }
                lines = _elm.Send(request, 400);
            }

            foreach (var line in lines)
            {
                var clean = line.Replace(" ", "");
                if (clean.Length < 5) continue;
                if (clean.Contains("NODATA") || clean.Contains("ERROR") ||
                    clean.Contains("SEARCHING") || clean.StartsWith("?")) continue;

                if (!int.TryParse(clean.Substring(0, 3), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture, out int respId)) continue;

                // With headers on, the adapter also prints the ISO-TP PCI
                // byte: "7E8 02 7E 00" for a single frame of two bytes.
                // A line without a single-frame PCI is other traffic, never an answer.
                var payload = clean.Substring(3);
                if (payload.Length < 2 || payload[0] != '0'
                    || !int.TryParse(payload.AsSpan(1, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int length)
                    || payload.Length - 2 < length * 2) continue;
                payload = payload.Substring(2, length * 2);

                // Other traffic in the range can arrive too, so keep looking
                // until a line is actually an answer to this request.
                if (expected.Any(p => payload.StartsWith(p))) return respId;
            }
            return -1;
        }

        /// <summary>
        /// Finds ECUs that answer TesterPresent (service 0x3E), or failing
        /// that a read of the active session (0x22 F186).
        /// Neither request changes anything in the ECU.
        ///
        /// Some IDs are never probed: 0x7DF is the broadcast address every ECU
        /// hears, and 0x7E8-0x7EF are the standard OBD reply IDs, so a probe
        /// there would appear on the bus under another node's name. Callers add
        /// the reply IDs they already know through <paramref name="skip"/>.
        /// </summary>
        public List<(int Request, int Response)> DiscoverEcus(
            int from, int to = 0x7EF,
            Action<int>? probing = null,
            Action<int, int>? foundEcu = null,
            CancellationToken ct = default,
            IEnumerable<int>? skip = null)
        {
            // Lower IDs carry ordinary application messages; a probe there
            // would look like one of them (see DefaultProbeFloor).
            if (from < ProbeFloor || to > 0x7FF || from > to)
                throw new ArgumentOutOfRangeException(nameof(from),
                    $"Probe range must be within {ProbeFloor:X3}-7FF, low end first.");

            return Probe(Enumerable.Range(from, to - from + 1), probing, foundEcu, ct, skip);
        }

        /// <summary>
        /// Checks whether ECUs answer on request IDs already known to be
        /// diagnostic addresses (from the catalogue), below the probe floor too:
        /// those are IDs the ECU itself listens on for diagnostics.
        /// </summary>
        public List<(int Request, int Response)> ProbeKnown(
            IEnumerable<int> requestIds,
            Action<int>? probing = null,
            Action<int, int>? foundEcu = null,
            CancellationToken ct = default,
            IEnumerable<int>? skip = null)
        {
            var ids = requestIds.Where(id => id is >= 0x100 and <= 0x7FF).Distinct().OrderBy(id => id).ToList();
            return Probe(ids, probing, foundEcu, ct, skip);
        }

        List<(int Request, int Response)> Probe(
            IEnumerable<int> ids, Action<int>? probing, Action<int, int>? foundEcu, CancellationToken ct, IEnumerable<int>? skip)
        {
            var found = new List<(int, int)>();
            var never = new HashSet<int>(skip ?? Enumerable.Empty<int>()) { 0x7DF };
            for (int id = 0x7E8; id <= 0x7EF; id++) never.Add(id);

            // probing changes header, filter and header display: whoever reads next must target again
            _target = null;
            SetUpProbing();

            foreach (int id in ids)
            {
                if (ct.IsCancellationRequested) break;
                probing?.Invoke(id);
                if (never.Contains(id)) continue;

                _probeHeader = $"ATSH{id:X3}";
                // refused, the probe would go out under the previous ID and its answer be filed under this one
                if (!_elm.Send(_probeHeader).Any(l => l.Contains("OK", StringComparison.OrdinalIgnoreCase))) continue;
                _probeHeaderLost = false;
                int respId = Probe("3E00", "7E00", "7F3E");

                // Some ECUs (on e-CMP, the VCU at 0x6A2) ignore
                // TesterPresent but still answer reads. Reading the active
                // session (F186) is harmless, and any answer to it proves an
                // ECU is there, a refusal (7F 22) included.
                if (respId < 0 && !_probeHeaderLost) respId = Probe("22F186", "62F186", "7F22");

                if (respId >= 0)
                {
                    foundEcu?.Invoke(id, respId);
                    found.Add((id, respId));
                    never.Add(respId);          // that ID is now known to be an ECU's reply
                }
            }

            return found;
        }

        /// <summary>
        /// Targets one ECU: transmit header, receive filter, flow control.
        /// Headers go off so the adapter returns its assembled multi-frame form.
        /// </summary>
        public void Target(int requestId, int responseId)
        {
            // only 11-bit IDs, and no request below 0x100, where the car's own messages live: a garbled or
            // mistyped address in a car file must not reach the bus (discovery sweeps stay above ProbeFloor)
            if (requestId is < 0x100 or > 0x7FF || responseId is < 0 or > 0x7FF)
                throw new ArgumentOutOfRangeException(nameof(requestId), $"CAN IDs 0x{requestId:X}/0x{responseId:X} are not usable: requests 0x100-0x7FF, replies 0x000-0x7FF.");
            // the adapter reset itself (brown-out, LV RESET): its settings are gone
            if (_needsInit) Init();

            // each ECU gets its own wait: a slow one keeps the longer wait whenever it is addressed again
            // (the live view moves between ECUs every round), the others the default
            int wait = _slowTargets.Contains((requestId, responseId)) ? SlowEcuTimeout : DefaultTimeout;
            if (wait != _stTimeout)
            {
                _stTimeout = wait;
                _elm.Send($"ATST{_stTimeout:X2}");
            }
            _target = (requestId, responseId);

            // If one of these is refused, reads would go to the previous ECU and
            // be filed under this one. Stop instead. The reset is only counted as
            // handled once the adapter is set up and targeted again.
            Expect("ATH0");
            // the adapter reset while idle and no reply has shown it yet: its echo is back on (ATE0 lost)
            if (_elm.LastReplyEchoed)
            {
                Init();
                Expect("ATH0");
            }
            Expect($"ATSH{requestId:X3}");
            Expect($"ATCRA{responseId:X3}");
            _needsInit = false;

            // Flow control only matters for multi-frame answers, and some cheap
            // clones lack these commands. Best effort.
            _elm.Send($"ATFCSH{requestId:X3}");
            _elm.Send("ATFCSD300000");
            _elm.Send("ATFCSM1");
        }

        /// <summary>A setup command the adapter must accept: refused, the adapter itself is the problem, not one ECU.</summary>
        void ExpectSetup(string command)
        {
            var reply = _elm.Send(command);
            if (!reply.Any(l => l.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase)))
                throw new AdapterException(AdapterProblem.SetupRefused, $"The adapter did not accept {command}: {string.Join(" ", reply)}", command);
        }

        void Expect(string command)
        {
            var reply = _elm.Send(command);
            if (!reply.Any(l => l.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase)))
                throw new AdapterException(AdapterProblem.CommandRefused, $"The adapter did not accept {command}: {string.Join(" ", reply)}", command);
        }

        /// <summary>ReadDataByIdentifier. The identifier is echoed back, so we check it.</summary>
        public UdsResponse ReadDid(int did, int timeoutMs = 600)
            => did is < 0 or > 0xFFFF ? throw new ArgumentOutOfRangeException(nameof(did)) : Exchange($"22{did:X4}", timeoutMs, lines => ParseForDid(lines, did, 0x22));

        /// <summary>
        /// ReadScalingDataByIdentifier. Asks the ECU how an identifier's bytes
        /// should be interpreted rather than what its value is. Optional in
        /// the standard and rarely implemented, but read-only and cheap to try.
        /// </summary>
        public UdsResponse ReadScaling(int did, int timeoutMs = 800)
            => did is < 0 or > 0xFFFF ? throw new ArgumentOutOfRangeException(nameof(did)) : Exchange($"24{did:X4}", timeoutMs, lines => ParseForDid(lines, did, 0x24));

        /// <summary>
        /// Generic request for services that do not echo a data identifier —
        /// 0x19 ReadDTCInformation in particular, where the second byte is a
        /// sub-function. Data comes back as everything after the response SID.
        ///
        /// Throws ArgumentException for any service outside ReadOnlyServices (and FormatException for non-hex input).
        /// </summary>
        public UdsResponse Request(string payloadHex, byte service, int timeoutMs = 3000)
        {
            byte[] request = Convert.FromHexString(payloadHex);
            if (request.Length == 0 || request[0] != service || !ReadOnlyServices.Contains(service))
                throw new ArgumentException($"Not a read-only request: {payloadHex}", nameof(payloadHex));

            // The positive reply echoes what followed the service byte: the
            // sub-function, and for DTC snapshot/extended requests the DTC too.
            // Check it, so a late reply to 19 04 is not taken for 19 06.
            int echo = service == 0x19 && request.Length >= 5 ? 4 : Math.Min(1, request.Length - 1);
            byte[] expected = request.Skip(1).Take(echo).ToArray();

            return Exchange(payloadHex, timeoutMs, lines =>
                Parse(lines, service, data => data.Length >= expected.Length && data.AsSpan(0, expected.Length).SequenceEqual(expected), skip: 0));
        }

        /// <summary>
        /// Sends one request and asks once more when: the adapter reset during it
        /// (after restoring the setup and the target); a late answer to the previous
        /// request came in (after switching this ECU to the 400 ms wait); the ECU said
        /// "busy, repeat the request" (after a moment); or the ECU only said
        /// "responsePending" before the adapter stopped listening (with the adapter's
        /// timeout at its maximum, 0xFF x 4 ms, about one second). An answer that came
        /// while a reset is unhandled is never filed under the target. Safe to repeat
        /// because every request this program sends is read-only.
        /// </summary>
        UdsResponse Exchange(string payloadHex, int timeoutMs, Func<string[], UdsResponse> parse)
        {
            Recover();
            var r = parse(SendRequest(payloadHex, timeoutMs));

            // A reply that came in after a reset went out on the default header,
            // so any ECU may have sent it. Never file it under the target.
            UdsResponse Trusted(UdsResponse x) =>
                _needsInit ? new UdsResponse { Kind = ResponseKind.AdapterError } : x;

            // The adapter reset during this request. Without recovery every
            // following read would go out on the broadcast ID 0x7DF and whatever
            // answered would be filed under the current ECU. Restore the setup
            // and the target, then ask once more.
            if (_needsInit)
            {
                Recover();
                r = parse(SendRequest(payloadHex, timeoutMs));
            }

            // A late answer to the previous request came in this one's window: the ECU (or a gateway)
            // answers slower than the adapter waits, so each answer would land under the next request,
            // the positive ones rejected and the refusals filed one identifier too far. Wait longer from
            // now on and ask again.
            // (not while a reset is unhandled: the adapter would send on its default broadcast header)
            if (r.LateAnswer && r.Kind != ResponseKind.Positive && _stTimeout < SlowEcuTimeout && !_needsInit)
            {
                if (_target is { } slow) _slowTargets.Add(slow);
                _stTimeout = SlowEcuTimeout;
                _elm.Send($"ATST{_stTimeout:X2}");
                r = parse(SendRequest(payloadHex, timeoutMs));
            }

            // "busy, repeat the request": once more after a moment, else it would read as a missing identifier
            if (r.Kind == ResponseKind.Negative && r.Nrc == 0x21 && !_needsInit)
            {
                Thread.Sleep(100);
                r = parse(SendRequest(payloadHex, timeoutMs));
            }

            // (not while a reset is unhandled: the adapter would send on its default broadcast header)
            if (r.Kind != ResponseKind.Pending || _needsInit) return Trusted(r);

            _elm.Send("ATSTFF");
            try
            {
                r = parse(SendRequest(payloadHex, Math.Max(timeoutMs, 5000)));
            }
            finally
            {
                _elm.Send($"ATST{_stTimeout:X2}");
            }

            if (r.Kind == ResponseKind.Pending) r.Kind = ResponseKind.NoData;
            return Trusted(r);
        }

        string _probeHeader = "";

        /// <summary>The adapter refused the probe header again after a reset: nothing more goes out for this ID.</summary>
        bool _probeHeaderLost;

        /// <summary>
        /// Headers on, and the reply filter widened: the adapter's automatic filter
        /// only expects the usual OBD reply IDs, but ECUs answer elsewhere (on e-CMP,
        /// 0x6B4 on 0x694 and 0x590 on 0x58F). This accepts every ID with bit 0x400
        /// set, i.e. 0x400-0x7FF: an ECU replying lower is not found.
        /// </summary>
        void SetUpProbing()
        {
            // the probes are timed for the default wait, whatever ECU was read before
            if (_stTimeout != DefaultTimeout)
            {
                _stTimeout = DefaultTimeout;
                _elm.Send($"ATST{_stTimeout:X2}");
            }
            _elm.Send("ATH1");
            _elm.Send("ATCRA");
            _elm.Send("ATCF400");
            _elm.Send("ATCM400");
        }

        /// <summary>After an adapter reset: set it up again and point it back at the last ECU.</summary>
        void Recover()
        {
            if (!_needsInit) return;
            if (_target is var (request, response)) Target(request, response);   // runs Init first
            else { Init(); _needsInit = false; }
        }

        /// <summary>Replies in a row that kept their spaces although the adapter was set up again in between.</summary>
        int _spacedInARow;

        string[] SendRequest(string payloadHex, int timeoutMs)
        {
            var lines = _elm.Send(payloadHex, timeoutMs);
            bool reset = _elm.LastReplyEchoed || lines.Any(l => IsResetBanner(Clean(l)));
            bool spaced = !reset && lines.Any(l => LooksLikeDefaults(l) && !l.TrimStart().StartsWith("SEARCHING", StringComparison.OrdinalIgnoreCase));
            // a clone that says OK to ATS0 but keeps the spaces would look like an adapter reset on every read
            _spacedInARow = spaced ? _spacedInARow + 1 : 0;
            if (_spacedInARow >= 3)
                throw new AdapterException(AdapterProblem.SetupRefused,
                    "The adapter keeps spaces in its replies although it accepted ATS0, so its replies cannot be read.", "ATS0");
            if (reset || lines.Any(LooksLikeDefaults)) _needsInit = true;
            return lines;
        }

        static UdsResponse ParseForDid(string[] lines, int did, byte service) =>
            Parse(lines, service, data => data.Length >= 2 && ((data[0] << 8) | data[1]) == did, skip: 2);

        /// <summary>
        /// Turns the adapter's reply lines into one response.
        ///
        /// A reply can hold several messages: a late answer to the previous
        /// request, "responsePending" notices, then the real answer. Each
        /// message is decoded on its own and the one that answers this request
        /// is picked. Bytes are never joined across messages, and a message
        /// that arrived incomplete or garbled is dropped rather than guessed at.
        /// </summary>
        static UdsResponse Parse(string[] lines, byte service, Func<byte[], bool>? isOurs, int skip)
        {
            var r = new UdsResponse();
            var clean = lines.Select(Clean).Where(l => l.Length > 0).ToList();

            if (clean.Count == 0) { r.Kind = ResponseKind.NoData; return r; }

            // the adapter restarted mid-request: whatever else is here is not trustworthy
            if (clean.Any(IsResetBanner)) { r.Kind = ResponseKind.AdapterError; return r; }

            if (clean.Any(l => l.Contains("ERROR") || l.Contains("BUFFERFULL") || l.Contains('?') || l.Contains("STOPPED")))
            {
                r.Kind = ResponseKind.AdapterError;
                return r;
            }

            var messages = SplitMessages(clean);
            if (messages.Count == 0)
            {
                r.Kind = clean.Any(l => l.Contains("NODATA") || l.Contains("TIMEOUT"))
                    ? ResponseKind.NoData : ResponseKind.AdapterError;
                return r;
            }

            // A negative reply carries no identifier, so a late one for the
            // previous request cannot be told apart by content - only by order:
            // it comes first. The last refusal or pending notice is ours.
            byte? last = null;
            bool someoneElse = false, garbled = false;

            foreach (var bytes in messages)
            {
                if (bytes == null || bytes.Length == 0) { garbled = true; continue; }

                if (bytes[0] == 0x7F && bytes.Length >= 2 && bytes[1] == service)
                {
                    last = bytes.Length >= 3 ? bytes[2] : (byte)0;     // 0x78 = still working
                    continue;
                }

                if (bytes[0] == (byte)(service + 0x40))
                {
                    var data = bytes[1..];
                    if (isOurs != null && !isOurs(data)) { someoneElse = true; continue; }   // someone else's (late) answer

                    r.Kind = ResponseKind.Positive;
                    r.Data = data[skip..];
                    return r;
                }
            }

            r.LateAnswer = someoneElse;
            if (last == 0x78) { r.Kind = ResponseKind.Pending; return r; }
            if (last is byte n) { r.Kind = ResponseKind.Negative; r.Nrc = n; return r; }
            // only the late answer to an earlier request: no answer to this one; a garbled message may have been it
            r.Kind = someoneElse && !garbled ? ResponseKind.NoData : ResponseKind.AdapterError;
            return r;
        }

        /// <summary>
        /// Groups reply lines into messages. With headers off and CAF1 the
        /// adapter prints a single frame as one hex line, and a multi-frame
        /// answer as a length line followed by "0:", "1:" ... "F:", "0:" ...
        /// A null entry is a message that was incomplete or garbled.
        /// </summary>
        static List<byte[]?> SplitMessages(List<string> lines)
        {
            var messages = new List<byte[]?>();
            int i = 0;

            while (i < lines.Count)
            {
                string line = lines[i];
                if (IsChatter(line)) { i++; continue; }

                bool lengthLine = line.Length <= 3 && IsHex(line) &&
                                  i + 1 < lines.Count && lines[i + 1].StartsWith("0:");
                if (lengthLine)
                {
                    int declared = int.Parse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var hex = new StringBuilder();
                    bool intact = true;
                    int expected = 0;
                    i++;

                    while (i < lines.Count && lines[i].Length >= 2 && lines[i][1] == ':' && Uri.IsHexDigit(lines[i][0]))
                    {
                        int index = Convert.ToInt32(lines[i].Substring(0, 1), 16);
                        string body = lines[i].Substring(2);
                        if (index != expected % 16 || body.Length % 2 != 0 || !IsHex(body)) intact = false;
                        hex.Append(body);
                        expected++;
                        i++;
                    }

                    // a lost frame leaves it short; every byte after the gap would be wrong
                    messages.Add(intact && hex.Length >= declared * 2
                        ? Convert.FromHexString(hex.ToString(0, declared * 2))
                        : null);
                    continue;
                }

                // single frame; an odd digit count means a character was lost on the line
                messages.Add(line.Length % 2 == 0 && IsHex(line) ? Convert.FromHexString(line) : null);
                i++;
            }

            return messages;
        }

        static string Clean(string line) => line.Replace(" ", "").ToUpperInvariant();

        static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

        /// <summary>
        /// Adapter status text, not payload. SEARCHING in particular must never
        /// reach the hex decoder: S, E, A, C and H are hex digits.
        /// </summary>
        static bool IsChatter(string l) =>
            l.StartsWith("SEARCHING") || l.StartsWith("BUSINIT") || l.StartsWith("OK") ||
            l.StartsWith("UNABLE") || l.Contains("NODATA") || l.Contains("TIMEOUT");

        /// <summary>
        /// Signs of an adapter back on its factory settings although no banner
        /// was seen (it reset while idle and the banner was discarded): spaces
        /// between the hex bytes (we set ATS0), also in the frames of a multi-frame
        /// reply ("0: 62 01 01"), or an automatic protocol search (we set a fixed
        /// protocol). Neither can happen while our setup is in place. The echo
        /// that comes back with them is seen by the link (LastReplyEchoed).
        /// </summary>
        static bool LooksLikeDefaults(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("SEARCHING", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Length >= 2 && t[1] == ':' && Uri.IsHexDigit(t[0])) t = t[2..].Trim();
            return t.Contains(' ') && t.Length >= 5 && t.Replace(" ", "").All(Uri.IsHexDigit);
        }

        static bool IsResetBanner(string l) =>
            // Contains: after a brown-out the banner often follows a few junk bytes
            l.Contains("ELM327") || l.Contains("LVRESET") || l.Contains("ACTALERT");

    }
}