using System.Text;

namespace EscoEvObdScanner
{
    /// <summary>
    /// The ELM327 / STN protocol over a byte channel (<see cref="IElmChannel"/>).
    ///
    /// Everything is line-oriented ASCII terminated by '>' (the prompt).
    /// Commands starting with "AT" are handled by the adapter itself and never
    /// reach the CAN bus. Everything else is interpreted as hex payload bytes
    /// and transmitted as an OBD request. The channel may deliver a reply in
    /// pieces of any size; the reply ends at the prompt, wherever it falls.
    /// </summary>
    public class ElmLink : IElmLink
    {
        readonly IElmChannel _channel;

        /// <summary>Raised with every command and its raw reply, for --trace and the app's log.</summary>
        public event Action<string>? Trace;

        public ElmLink(IElmChannel channel) => _channel = channel;

        public bool IsOpen => _channel.IsOpen;

        public bool LastReplyEchoed { get; private set; }

        /// <summary>Opens the channel; what it found on opening goes to <see cref="Trace"/>.</summary>
        public void Open()
        {
            _channel.Open();
            if (_channel.Details is string details) Trace?.Invoke($"[{_channel.Name}] {details}");
        }

        /// <summary>
        /// Sends a command and reads until the '>' prompt.
        /// Returns the response lines with echo and prompt stripped (see <see cref="Reply"/>).
        /// </summary>
        public string[] Send(string command, int timeoutMs = 5000)
        {
            try
            {
                return Exchange(command, timeoutMs);
            }
            // a USB adapter pulled out mid-read ends the read with OperationCanceledException
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
            {
                throw new PortLostException(_channel.Name, $"{_channel.Name}: {ex.Message}", ex);
            }
        }

        string[] Exchange(string command, int timeoutMs)
        {
            _channel.DiscardInput();
            _channel.Write(command + "\r");

            var sb = new StringBuilder();
            bool prompt = ReadUntilPrompt(sb, timeoutMs);

            // No prompt: the adapter is still busy with this command. The next
            // command's first character would interrupt it and the rest would be
            // read as a new command. Interrupt it deliberately and wait it out.
            // A space, not a carriage return: a busy adapter stops on any
            // character, an idle one ignores spaces, whereas a bare CR would
            // make an idle adapter repeat the last command.
            if (!prompt)
            {
                _channel.Write(" ");
                // still busy: the next command's first character would be swallowed and the rest sent as
                // another request ("22701X" as "2701X"), so the adapter is given up instead
                if (!ReadUntilPrompt(new StringBuilder(), 1000))
                    throw new PortLostException(_channel.Name, $"The adapter on {_channel.Name} does not answer any more. Disconnect, check the adapter and connect again.", new TimeoutException(command));
            }

            var (raw, lines, echoed) = Reply(sb.ToString(), prompt, command);
            Trace?.Invoke($"[{command}] -> {raw.Replace("\r", "|")}");
            LastReplyEchoed = echoed;
            return lines;
        }

        /// <summary>
        /// What was read before the prompt (or the deadline) as reply lines: the
        /// text kept, the lines without the echoed command, and whether the reply
        /// began with that echo.
        /// </summary>
        internal static (string Raw, string[] Lines, bool Echoed) Reply(string received, bool prompt, string command)
        {
            var raw = received;

            // Without the prompt the reply was cut off mid-way. A partial last
            // line ("62D4" of "62D4108996") could still look like valid hex, so
            // keep only lines that were finished with a carriage return.
            if (!prompt)
            {
                int end = raw.LastIndexOf('\r');
                raw = end >= 0 ? raw.Substring(0, end + 1) : "";
            }

            var lines = raw
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            bool echoed = lines.Count > 0 && lines[0].Equals(command, StringComparison.OrdinalIgnoreCase);
            return (raw, lines.Where(l => !l.Equals(command, StringComparison.OrdinalIgnoreCase)).ToArray(), echoed);
        }

        /// <summary>Reads into <paramref name="sb"/> until '>' or the deadline. True if the prompt came.</summary>
        bool ReadUntilPrompt(StringBuilder sb, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                // short read slices, so the deadline is the whole wait and not per byte
                int b = _channel.Read(100);
                if (b < 0) continue;
                if (b == '>') return true;
                sb.Append((char)b);
            }
            return false;
        }

        public void Dispose() => _channel.Dispose();
    }
}
