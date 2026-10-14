namespace EscoEvObdScanner
{
    public enum AdapterProblem
    {
        /// <summary>The adapter refused a command that points it at one ECU (ATH0, ATSH, ATCRA): that ECU cannot be addressed.</summary>
        CommandRefused,
        /// <summary>The adapter gave no usable answer to many reads in a row: it lost power or went to sleep.</summary>
        NotAnswering,
        /// <summary>The adapter would not address any ECU, several in a row: the adapter is gone, not one ECU.</summary>
        NoEcuAddressable,
        /// <summary>The adapter refused a setup command (spaces off, auto formatting, the CAN protocol): nothing it reads can be trusted.</summary>
        SetupRefused
    }

    /// <summary>
    /// The adapter itself is the problem. The message is English for the
    /// command line; the app words it from <see cref="Problem"/>, <see cref="Command"/>
    /// and <see cref="Count"/> in the UI language.
    /// </summary>
    public sealed class AdapterException : Exception
    {
        public AdapterException(AdapterProblem problem, string message, string? command = null, int count = 0) : base(message)
        {
            Problem = problem;
            Command = command;
            Count = count;
        }

        public AdapterProblem Problem { get; }
        public string? Command { get; }
        public int Count { get; }
    }
}
