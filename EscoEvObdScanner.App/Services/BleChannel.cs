using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EscoEvObdScanner.App.Services
{
    /// <summary>
    /// An ELM327 / STN adapter over Bluetooth LE. The adapter's text goes over two GATT
    /// characteristics: commands are written to one, replies come
    /// as notifications on another, in pieces of the link's packet size. The service used is the
    /// first one, <see cref="BleAdapters.PreferredServices"/> before the others, that has both a
    /// writable and a notifying characteristic; <see cref="BleAdapters.NeverUsedServices"/> are skipped.
    /// </summary>
    public sealed class BleChannel : IElmChannel
    {
        readonly BleAdapters _settings;
        readonly ulong _address;
        readonly BlockingCollection<byte> _received = new();
        BluetoothLEDevice? _device;
        GattDeviceService? _service;
        GattCharacteristic? _write, _notify;
        GattWriteOption _writeOption;
        int _chunk = 20;
        volatile bool _lost;

        /// <param name="address">The adapter's Bluetooth address.</param>
        /// <param name="name">Its advertised name, for the user and for errors.</param>
        /// <param name="settings">Which services are tried first and which never.</param>
        public BleChannel(ulong address, string name, BleAdapters settings)
        {
            _address = address;
            _settings = settings;
            Name = string.IsNullOrEmpty(name) ? $"Bluetooth LE {address:X12}" : name;
        }

        public string Name { get; }

        public bool IsOpen => _notify != null && !_lost;

        public string? Details { get; private set; }

        /// <summary>
        /// Opens the link. An adapter that lets its characteristics be used only over a paired link
        /// is paired once through Windows (see <see cref="Pair"/>) and the link opened again. A failure
        /// is a <see cref="BleException"/>.
        /// </summary>
        public void Open()
        {
            try
            {
                try { OpenLink(); }
                catch (Exception ex) when (NeedsPairing(ex))
                {
                    Release();
                    Pair();
                    OpenLink();
                }
            }
            catch (Exception ex) when (NeedsPairing(ex)) { throw new BleException(BleProblem.PairingFailed, Name, ex.Message, ex); }
            catch (TimeoutException ex) { throw new BleException(BleProblem.NoAnswer, Name, ex.Message, ex); }
        }

        // ATT errors 0x05 insufficient authentication, 0x08 insufficient authorization, 0x0F insufficient encryption
        static bool NeedsPairing(Exception ex) =>
            unchecked((uint)ex.HResult) >> 8 == 0x806500 && (ex.HResult & 0xFF) is 0x05 or 0x08 or 0x0F
            || ex.InnerException is { } inner && NeedsPairing(inner);

        /// <summary>
        /// Pairs with the adapter through Windows' own pairing. A pairing that asks only for a yes, for
        /// the same number on both sides or for a number shown here is accepted; one that asks for a PIN
        /// is given <see cref="BleAdapters.PairingPin"/>. What the adapter asked for goes to <see cref="Details"/>
        /// when pairing succeeds and into the error when it fails.
        /// </summary>
        void Pair()
        {
            using var device = Wait(BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask())
                ?? throw new BleException(BleProblem.NotFound, Name);
            var pairing = device.DeviceInformation.Pairing;
            if (pairing.IsPaired) return;
            var asked = new List<string>();
            pairing.Custom.PairingRequested += (_, request) =>
            {
                lock (asked) asked.Add(request.PairingKind.ToString());
                if (request.PairingKind == DevicePairingKinds.ProvidePin) request.Accept(_settings.PairingPin);
                else request.Accept();
            };
            string Asked() { lock (asked) return asked.Count == 0 ? "nothing asked" : "asked: " + string.Join(", ", asked); }
            DevicePairingResult result;
            try
            {
                result = Wait(pairing.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch
                    | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin, DevicePairingProtectionLevel.Encryption).AsTask(), 30);
            }
            catch (TimeoutException) { throw new BleException(BleProblem.PairingFailed, Name, $"no answer within 30 s, {Asked()}"); }
            if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                throw new BleException(BleProblem.PairingFailed, Name, $"{result.Status}, {Asked()}");
            _paired = $"paired now ({Asked()})";
        }

        // what pairing on this connection asked for, for Details; null when no pairing was needed
        string? _paired;

        void OpenLink()
        {
            _lost = false;
            // the WinRT calls complete on the thread pool; Open runs off the UI thread
            _device = Wait(BluetoothLEDevice.FromBluetoothAddressAsync(_address).AsTask())
                ?? throw new BleException(BleProblem.NotFound, Name);
            _device.ConnectionStatusChanged += (_, _) =>
            {
                if (_device?.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _lost = true;
            };

            // the first query connects to the adapter, which takes longer than a write
            var services = Wait(_device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(), 20);
            if (services.Status != GattCommunicationStatus.Success)
                throw new BleException(BleProblem.Refused, Name, $"services: {services.Status}");

            var candidates = services.Services
                .Where(s => !_settings.NeverUsedServices.Contains(s.Uuid))
                .OrderBy(s => _settings.PreferredServices.Contains(s.Uuid) ? 0 : 1);
            foreach (var service in candidates)
            {
                var chars = Wait(service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask());
                if (chars.Status != GattCommunicationStatus.Success) continue;
                var notify = chars.Characteristics.FirstOrDefault(c =>
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate));
                var write = chars.Characteristics.FirstOrDefault(c =>
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse) ||
                    c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));
                if (notify == null || write == null) continue;

                _service = service;
                _notify = notify;
                _write = write;
                break;
            }
            if (_notify == null || _write == null)
                throw new BleException(BleProblem.NoService, Name);

            _writeOption = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;

            // the largest write the link takes: the negotiated packet size less the 3-byte header
            try
            {
                var session = Wait(GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask());
                if (session.MaxPduSize > 23) _chunk = session.MaxPduSize - 3;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }

            _notify.ValueChanged += (_, args) =>
            {
                if (_lost) return;
                foreach (var b in args.CharacteristicValue.ToArray()) _received.Add(b);
            };
            var mode = _notify.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            var status = Wait(_notify.WriteClientCharacteristicConfigurationDescriptorAsync(mode).AsTask());
            if (status != GattCommunicationStatus.Success)
                throw new BleException(BleProblem.Refused, Name, $"replies: {status}");

            Details = (_paired != null ? _paired + "; " : "") + $"service {_service!.Uuid}, commands to {_write.Uuid} ({_writeOption}), replies from {_notify.Uuid}, {_chunk} bytes per write";
        }

        public void Write(string text)
        {
            if (_lost || _write == null) throw new IOException($"{Name} disconnected");
            var bytes = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i < bytes.Length; i += _chunk)
            {
                var piece = bytes.AsSpan(i, Math.Min(_chunk, bytes.Length - i)).ToArray();
                var status = Wait(_write.WriteValueAsync(piece.AsBuffer(), _writeOption).AsTask());
                if (status != GattCommunicationStatus.Success)
                    throw new IOException($"{Name}: write failed ({status})");
            }
        }

        public int Read(int timeoutMs)
        {
            if (_received.TryTake(out byte b, Math.Max(1, timeoutMs))) return b;
            if (_lost) throw new IOException($"{Name} disconnected");
            return -1;
        }

        public void DiscardInput()
        {
            while (_received.TryTake(out _)) { }
        }

        /// <summary>Waits for a WinRT call at most <paramref name="seconds"/>, then throws <see cref="TimeoutException"/>.</summary>
        static T Wait<T>(Task<T> task, int seconds = 10)
        {
            if (Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))).GetAwaiter().GetResult() != task)
                throw new TimeoutException($"Bluetooth did not answer within {seconds} s");
            return task.GetAwaiter().GetResult();
        }

        /// <summary>Lets go of the device and its service, so the link can be opened again.</summary>
        void Release()
        {
            try { _service?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            _service = null;
            _device = null;
            _notify = null;
            _write = null;
        }

        public void Dispose()
        {
            _lost = true;
            Release();
        }
    }
}
