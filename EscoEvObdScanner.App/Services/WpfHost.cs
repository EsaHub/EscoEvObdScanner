using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using EscoEvObdScanner.App.Views;
using EscoEvObdScanner.AppLogic.Infrastructure;
using Microsoft.Win32;

namespace EscoEvObdScanner.App.Services
{
    /// <summary>The Windows side of what the view models need: message boxes, file and recording dialogs, Explorer, COM ports and Bluetooth LE adapters, the dispatcher.</summary>
    public sealed class WpfHost : IAppHost
    {
        const string Title = "Esco EV OBD Scanner";

        static Window? Owner => Application.Current?.MainWindow;

        static MessageBoxImage Image(Notice kind) => kind switch
        {
            Notice.Warning => MessageBoxImage.Warning,
            Notice.Question => MessageBoxImage.Question,
            _ => MessageBoxImage.Information
        };

        public bool Ask(string text, Notice kind, bool defaultNo = false) =>
            MessageBox.Show(text, Title, MessageBoxButton.YesNo, Image(kind), defaultNo ? MessageBoxResult.No : MessageBoxResult.Yes)
            == MessageBoxResult.Yes;

        public void Tell(string text, Notice kind) => MessageBox.Show(text, Title, MessageBoxButton.OK, Image(kind));

        public string? PickCarFile(string? folder)
        {
            var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json", InitialDirectory = folder };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        public void AddVehicle(IReadOnlyList<CarFileChoice> files, string? carPath, Func<string, string, string?> add) =>
            new AddVehicleWindow(files, carPath, add) { Owner = Owner }.ShowDialog();

        public string? PickRecording(IReadOnlyList<RecordingChoice> choices)
        {
            var picker = new RecordingPicker(choices) { Owner = Owner };
            return picker.ShowDialog() == true ? picker.Chosen : null;
        }

        /// <summary>A folder opens in Explorer, a file in its program. The self-test opens nothing.</summary>
        public void Open(string path)
        {
            if (App.SelfTestFolder != null) return;
            Process.Start(Directory.Exists(path)
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(path) { UseShellExecute = true });
        }

        public IReadOnlyList<PortInfo> Ports() => PortList.Get().Concat(BleScan.Get()).ToList();

        /// <summary>A Bluetooth LE adapter from <see cref="BleScan"/> through <see cref="BleChannel"/>; a COM port as on every platform.</summary>
        public IElmLink CreateLink(string port, int baud) =>
            BleScan.Address(port) is ulong address
                ? new ElmLink(new BleChannel(address, BleScan.NameOf(address), BleScan.Settings))
                : new Elm327(port, baud);

        /// <summary>The window's bindings format with its language, set at start; the new culture goes to them too.</summary>
        public void LanguageChanged()
        {
            if (Owner is { } window)
                window.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
        }

        public IDisposable Every(TimeSpan interval, Action tick)
        {
            var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => tick(), Dispatcher.CurrentDispatcher);
            timer.Start();
            return new Stopper(timer);
        }

        sealed class Stopper(DispatcherTimer timer) : IDisposable
        {
            public void Dispose() => timer.Stop();
        }

        public IRowView RowView(IList rows, Predicate<object> filter) => new RowList(rows) { Filter = filter };

        /// <summary>The list as the grid shows it, the user's sort included.</summary>
        sealed class RowList(IList rows) : ListCollectionView(rows), IRowView;

        public event EventHandler? RequerySuggested
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public void RequeryCommands() => CommandManager.InvalidateRequerySuggested();
    }
}
