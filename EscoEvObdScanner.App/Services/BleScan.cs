using Windows.Devices.Bluetooth.Advertisement;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.App.Services
{
    /// <summary>
    /// The Bluetooth LE OBD adapters advertising nearby. A watcher starts with the first list
    /// (when the app starts) and keeps listening; the list never waits for it, so an adapter
    /// switched on later shows after a refresh. Only devices whose advertised name matches
    /// <see cref="BleAdapters.AdapterName"/> are listed. Nothing is sent to a device until the user connects to it.
    /// </summary>
    public static class BleScan
    {
        /// <summary>The Bluetooth LE settings (ble-adapters.json), read once.</summary>
        public static BleAdapters Settings { get; } = AppPaths.BleAdapters(out _);

        static readonly object Gate = new();
        static readonly Dictionary<ulong, string> Heard = new();
        static BluetoothLEAdvertisementWatcher? _watcher;

        /// <summary>The adapters heard so far; the first call starts listening.</summary>
        public static List<PortInfo> Get()
        {
            lock (Gate)
            {
                if (_watcher == null) Start();
                return Heard.OrderBy(h => h.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(h => new PortInfo(PortInfo.BlePrefix + h.Key.ToString("X12"), h.Value))
                    .ToList();
            }
        }

        /// <summary>The address in a <see cref="PortInfo.Name"/> made by <see cref="Get"/>; null for a serial port.</summary>
        public static ulong? Address(string port) =>
            port.StartsWith(PortInfo.BlePrefix, StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(port.AsSpan(PortInfo.BlePrefix.Length), System.Globalization.NumberStyles.HexNumber, null, out ulong a)
                ? a : null;

        /// <summary>The advertised name heard for <paramref name="address"/>, or empty.</summary>
        public static string NameOf(ulong address)
        {
            lock (Gate) return Heard.TryGetValue(address, out var n) ? n : "";
        }

        static void Start()
        {
            try
            {
                _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                _watcher.Received += (_, e) =>
                {
                    string name = e.Advertisement.LocalName;
                    if (string.IsNullOrWhiteSpace(name) || !Settings.AdapterName.IsMatch(name)) return;
                    lock (Gate) Heard[e.BluetoothAddress] = name.Trim();
                };
                _watcher.Start();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // the watcher did not start: no Bluetooth LE adapters are listed, serial ports are
                _watcher ??= new BluetoothLEAdvertisementWatcher();
            }
        }
    }
}
