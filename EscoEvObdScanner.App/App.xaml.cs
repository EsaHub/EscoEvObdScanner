using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using EscoEvObdScanner.AppLogic.Infrastructure;
using EscoEvObdScanner.App.Services;
using EscoEvObdScanner.AppLogic.Services;

namespace EscoEvObdScanner.App
{
    public partial class App : Application
    {
        /// <summary>Set by <c>--selftest &lt;folder&gt;</c>: run the UI self-test and exit.</summary>
        public static string? SelfTestFolder { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppHost.I = new WpfHost();

            int i = Array.IndexOf(e.Args, "--selftest");
            if (i >= 0 && i + 1 < e.Args.Length)
            {
                SelfTestFolder = Path.GetFullPath(e.Args[i + 1]);
                AppPaths.UseIsolatedFolders(SelfTestFolder);
            }

            // The language picks the formats of dates and numbers (Loc.CultureFor). Bindings format
            // with the element language, which defaults to en-US: give it the same culture, so
            // bindings and code (CurrentCulture) write dates and decimals alike.
            Loc.I.Choose(Settings.Load().Language);
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

            // Last line of defence: show the problem instead of vanishing.
            DispatcherUnhandledException += (_, args) =>
            {
                args.Handled = true;
                Fail(args.Exception, fatal: false);
            };
            // a worker thread's failure ends the process: leave a trace of why
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try
                {
                    var file = SelfTestFolder != null ? Path.Combine(SelfTestFolder, "crash.txt") : Path.Combine(AppPaths.ConfigDir, "crash.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {args.ExceptionObject}{Environment.NewLine}");
                }
                catch { }
            };

            base.OnStartup(e);

            // Created here rather than through StartupUri, so a failure while
            // building the window ends the process instead of leaving it
            // running with no window.
            try
            {
                new MainWindow().Show();
            }
            catch (Exception ex)
            {
                Fail(ex, fatal: true);
            }
        }

        void Fail(Exception ex, bool fatal)
        {
            // XAML wraps constructor failures; the inner message is the useful one
            while (ex is XamlParseException or System.Reflection.TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException!;

            if (SelfTestFolder != null)
            {
                File.AppendAllText(Path.Combine(SelfTestFolder, "crash.txt"), ex + Environment.NewLine);
                Shutdown(2);
                return;
            }

            // an error that repeats on every render or timer tick would stack message boxes: one at a time,
            // and a fifth error within a minute ends the app instead
            var now = DateTime.Now;
            _errors.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            _errors.Add(now);
            if (_errors.Count >= 5) fatal = true;
            if (_showingError && !fatal) return;
            _showingError = true;
            try { MessageBox.Show(ex.Message, "Esco EV OBD Scanner", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { _showingError = false; }
            if (fatal) Shutdown(1);
        }

        bool _showingError;
        readonly List<DateTime> _errors = new();
    }
}
