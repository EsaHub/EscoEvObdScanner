namespace EscoEvObdScanner
{
    /// <summary>
    /// The serial port failed (unplugged, closed, refused): the adapter is gone.
    /// Kept apart from other I/O errors, such as a full disk while writing a
    /// result file, which must not drop the connection.
    /// </summary>
    public sealed class PortLostException : IOException
    {
        public PortLostException(string port, string message, Exception inner) : base(message, inner) { Port = port; }

        /// <summary>The serial port that went away, e.g. COM4.</summary>
        public string Port { get; }
    }
}
