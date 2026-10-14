namespace EscoEvObdScanner
{
    /// <summary>
    /// A line-oriented ELM327-style link: send one command, get back the
    /// reply lines. Implemented by the real serial adapter and by the
    /// simulated car used for tests and as the demo car.
    /// </summary>
    public interface IElmLink : IDisposable
    {
        event Action<string>? Trace;

        bool IsOpen { get; }

        void Open();

        string[] Send(string command, int timeoutMs = 5000);

        /// <summary>
        /// Whether the last reply began with the command echoed back: an adapter that reset itself
        /// is on its factory settings again, where echo is on. The echo is not in the lines returned.
        /// </summary>
        bool LastReplyEchoed => false;
    }
}
