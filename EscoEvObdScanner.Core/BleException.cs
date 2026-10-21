namespace EscoEvObdScanner
{
    /// <summary>Why a Bluetooth LE adapter could not be opened.</summary>
    public enum BleProblem
    {
        /// <summary>No device at the address: unplugged, switched off, or Bluetooth off.</summary>
        NotFound,
        /// <summary>The adapter wants a paired link and pairing did not succeed.</summary>
        PairingFailed,
        /// <summary>No service with a writable and a notifying characteristic: not an ELM327 / STN adapter.</summary>
        NoService,
        /// <summary>The adapter refused a step of opening the link (listing services, subscribing to replies).</summary>
        Refused,
        /// <summary>Bluetooth did not answer in time.</summary>
        NoAnswer,
    }

    /// <summary>A Bluetooth LE adapter could not be opened; <see cref="Problem"/> says why, <see cref="Detail"/> what Windows reported.</summary>
    public sealed class BleException : IOException
    {
        public BleException(BleProblem problem, string adapter, string? detail = null, Exception? inner = null)
            : base($"{adapter}: {problem}{(detail == null ? "" : $" ({detail})")}", inner)
        {
            Problem = problem;
            Adapter = adapter;
            Detail = detail;
        }

        public BleProblem Problem { get; }

        /// <summary>The adapter's name as listed.</summary>
        public string Adapter { get; }

        /// <summary>What Windows reported, e.g. a status or the pairing asked for; null when nothing more.</summary>
        public string? Detail { get; }
    }
}
