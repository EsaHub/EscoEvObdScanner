namespace EscoEvObdScanner
{
    /// <summary>
    /// An ELM327 / STN adapter on a serial port (USB, or classic Bluetooth shown as a COM port):
    /// the protocol of <see cref="ElmLink"/> over a <see cref="SerialChannel"/>.
    /// </summary>
    public sealed class Elm327 : ElmLink
    {
        public Elm327(string portName, int baud = 115200) : base(new SerialChannel(portName, baud)) { }
    }
}
