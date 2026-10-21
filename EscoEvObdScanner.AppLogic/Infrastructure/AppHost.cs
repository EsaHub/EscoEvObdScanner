namespace EscoEvObdScanner.AppLogic.Infrastructure
{
    /// <summary>How a question or a notice to the user looks.</summary>
    public enum Notice { Information, Question, Warning }

    /// <summary>A car file in the list of a new vehicle: its path, platform name and the cars it covers.</summary>
    public sealed record CarFileChoice(string Path, string Title, string? Cars);

    /// <summary>One recording in the list: its main file and how it reads.</summary>
    public sealed record RecordingChoice(string Path, string Text);

    /// <summary>
    /// Where the adapter may be: a serial port with the device name the system gives it, or a
    /// Bluetooth LE adapter, whose <see cref="Name"/> is "BLE:" and its address and whose
    /// description is its advertised name.
    /// </summary>
    public sealed record PortInfo(string Name, string Description)
    {
        public override string ToString() =>
            IsBluetoothLe ? $"{Description}  –  Bluetooth LE"
            : string.IsNullOrEmpty(Description) ? Name : $"{Name}  –  {Description}";

        /// <summary>The prefix of a Bluetooth LE adapter's <see cref="Name"/>, before its address in hex.</summary>
        public const string BlePrefix = "BLE:";

        public bool IsBluetoothLe => Name.StartsWith(BlePrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>USB serial chips and Bluetooth links used in OBD adapters, to pre-select the likely port.</summary>
        public static bool LooksLikeAdapter(PortInfo p) => p.IsBluetoothLe ||
            System.Text.RegularExpressions.Regex.IsMatch(p.Description, "USB|FTDI|CH340|CP210|Prolific|Bluetooth|Serial",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            !p.Description.Contains("Management", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A list shown with a filter (and on screen, the user's sort): enumerates what is on screen, in that order.</summary>
    public interface IRowView : System.Collections.IEnumerable
    {
        /// <summary>Applies the filter again after it changed.</summary>
        void Refresh();
    }

    /// <summary>
    /// What the view models need from the platform the app runs on: dialogs,
    /// opening files, the adapter ports, filtered lists, a timer on the UI
    /// thread and the refresh of command states. The Windows app sets <see cref="AppHost.I"/>
    /// at start; the view models use nothing else of the platform.
    /// </summary>
    public interface IAppHost
    {
        /// <summary>A yes/no question; true for yes.</summary>
        bool Ask(string text, Notice kind, bool defaultNo = false);

        void Tell(string text, Notice kind);

        /// <summary>A car file (JSON) chosen by the user, starting in <paramref name="folder"/>; null when cancelled.</summary>
        string? PickCarFile(string? folder);

        /// <summary>The add-vehicle dialog, with <paramref name="carPath"/> chosen when given; <paramref name="add"/> adds a vehicle by name and car file and returns an error text or null.</summary>
        void AddVehicle(IReadOnlyList<CarFileChoice> files, string? carPath, Func<string, string, string?> add);

        /// <summary>A recording chosen by the user; null when cancelled.</summary>
        string? PickRecording(IReadOnlyList<RecordingChoice> choices);

        /// <summary>Opens a file in its program or a folder in the file browser.</summary>
        void Open(string path);

        /// <summary>The serial ports the adapter may be on, and on Windows the Bluetooth LE adapters found nearby.</summary>
        IReadOnlyList<PortInfo> Ports();

        /// <summary>The link to the adapter on <paramref name="port"/> (a <see cref="PortInfo.Name"/>), not opened yet:
        /// a serial port here; the Windows app also opens the Bluetooth LE adapters it lists.</summary>
        IElmLink CreateLink(string port, int baud) => new Elm327(port, baud);

        /// <summary>The UI language changed; dates and numbers on screen follow the new culture.</summary>
        void LanguageChanged();

        /// <summary>Runs <paramref name="tick"/> on the UI thread every <paramref name="interval"/> until disposed.</summary>
        IDisposable Every(TimeSpan interval, Action tick);

        /// <summary>A filtered view of <paramref name="rows"/> for a list on screen.</summary>
        IRowView RowView(System.Collections.IList rows, Predicate<object> filter);

        /// <summary>Commands tell the UI to ask them again whether they can run.</summary>
        event EventHandler? RequerySuggested;

        void RequeryCommands();
    }

    public static class AppHost
    {
        /// <summary>The platform the app runs on; set before the first view model is made.</summary>
        public static IAppHost I { get; set; } = null!;
    }
}
