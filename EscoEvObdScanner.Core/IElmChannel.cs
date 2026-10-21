namespace EscoEvObdScanner
{
    /// <summary>
    /// The byte channel under an ELM327 / STN adapter: a serial port, or a Bluetooth LE
    /// link on the Windows app. Carries ASCII text both ways; what the text means
    /// (commands, the '>' prompt, reply lines) is <see cref="ElmLink"/>'s business.
    /// </summary>
    public interface IElmChannel : IDisposable
    {
        /// <summary>How the channel is shown to the user and named in errors, e.g. "COM4".</summary>
        string Name { get; }

        bool IsOpen { get; }

        void Open();

        /// <summary>Sends <paramref name="text"/> as ASCII. Throws <see cref="IOException"/> when the channel is gone.</summary>
        void Write(string text);

        /// <summary>
        /// The next byte received, waiting at most <paramref name="timeoutMs"/>; -1 when nothing came
        /// in that time. Throws <see cref="IOException"/> when the channel is gone.
        /// </summary>
        int Read(int timeoutMs);

        /// <summary>Drops whatever was received and not read yet.</summary>
        void DiscardInput();

        /// <summary>What the channel found when it opened, for the log; null when there is nothing to tell.</summary>
        string? Details => null;
    }
}
