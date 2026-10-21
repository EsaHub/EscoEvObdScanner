namespace EscoEvObdScanner
{
    /// <summary>
    /// The adapter's channel failed (a serial port unplugged, closed or refused, or a
    /// Bluetooth LE link dropped): the adapter is gone. Kept apart from other I/O errors,
    /// such as a full disk while writing a result file, which must not drop the connection.
    /// </summary>
    public sealed class PortLostException : IOException
    {
        public PortLostException(string port, string message, Exception inner) : base(message, inner) { Port = port; }

        /// <summary>The port or link that went away, e.g. COM4, or a Bluetooth LE adapter by name.</summary>
        public string Port { get; }
    }
}
