using System.Text;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Writes everything to two writers at once: the console and a log file.
    /// </summary>
    sealed class TeeWriter : TextWriter
    {
        readonly TextWriter _screen;
        readonly TextWriter _file;

        public TeeWriter(TextWriter screen, TextWriter file)
        {
            _screen = screen;
            _file = file;
        }

        public override Encoding Encoding => _screen.Encoding;

        public override void Write(char value)
        {
            _screen.Write(value);
            _file.Write(value);
        }

        public override void Write(string? value)
        {
            _screen.Write(value);
            _file.Write(value);
        }

        public override void Flush()
        {
            _screen.Flush();
            _file.Flush();
        }
    }
}
