using System.IO.Ports;

namespace EscoEvObdScanner
{
    /// <summary>An adapter on a serial port: USB, or classic Bluetooth that Windows shows as a COM port.</summary>
    public sealed class SerialChannel : IElmChannel
    {
        readonly SerialPort _port;

        public SerialChannel(string portName, int baud = 115200)
        {
            _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
            {
                WriteTimeout = 2000,
                NewLine = "\r",
                DtrEnable = true,
                RtsEnable = true
            };
        }

        public string Name => _port.PortName;

        public bool IsOpen => _port.IsOpen;

        public void Open()
        {
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }

        public void Write(string text) => _port.Write(text);

        public int Read(int timeoutMs)
        {
            _port.ReadTimeout = Math.Max(1, timeoutMs);
            int b;
            try { b = _port.ReadByte(); }
            catch (TimeoutException) { return -1; }
            // the end of the stream: the port was closed under the read
            if (b < 0) throw new IOException($"{_port.PortName} closed");
            return b;
        }

        public void DiscardInput() => _port.DiscardInBuffer();

        public void Dispose()
        {
            try { if (_port.IsOpen) _port.Close(); } catch { }
            _port.Dispose();
        }
    }
}
