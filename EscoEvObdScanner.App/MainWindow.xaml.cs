using System.ComponentModel;
using System.Windows;
using EscoEvObdScanner.AppLogic.ViewModels;

namespace EscoEvObdScanner.App
{
    public partial class MainWindow : Window
    {
        readonly MainViewModel _vm;
        bool _closing, _shutDown;
        int? _selfTestResult;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm = new MainViewModel();
            FitToScreen();

            // at logoff or shutdown Windows closes the window without waiting for the adapter: save first
            Application.Current.SessionEnding += (_, _) => _vm.SaveNow();

            // The self-test ends by closing the window the way a user does, so
            // the shutdown path below is tested too.
            if (App.SelfTestFolder is string folder)
                Loaded += async (_, _) =>
                {
                    _selfTestResult = await SelfTest.RunAsync(this, _vm, Tabs, folder);
                    Close();
                };
        }

        /// <summary>
        /// On a small screen (a 1366 x 768 laptop) the design size would put the title bar
        /// above the screen, where the window cannot be moved: fit it to the work area.
        /// </summary>
        void FitToScreen()
        {
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
        }

        /// <summary>Stop the live view and close the port cleanly before the window goes.</summary>
        protected override async void OnClosing(CancelEventArgs e)
        {
            if (_shutDown) return;          // second pass, after the port is closed
            e.Cancel = true;
            if (_closing) return;           // already shutting down: a second click must wait too
            _closing = true;
            try
            {
                // a Bluetooth connect can hold the adapter for long: close anyway after a few seconds, settings saved
                var shutdown = _vm.ShutdownAsync();
                if (await Task.WhenAny(shutdown, Task.Delay(5000)) != shutdown) _vm.SaveNow();
            }
            finally
            {
                _shutDown = true;
                // Not Close() directly: if the shutdown finished without having to
                // wait, we are still inside this closing event, and WPF refuses a
                // Close() from there. Queue it for after the event instead.
                _ = Dispatcher.BeginInvoke(Close);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_selfTestResult is int code) Application.Current.Shutdown(code);
        }
    }
}
