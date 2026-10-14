using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace EscoEvObdScanner.AppLogic.Infrastructure
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name!);
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class RelayCommand : ICommand
    {
        readonly Action _execute;
        readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => AppHost.I.RequerySuggested += value;
            remove => AppHost.I.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter)
        {
            try
            {
                _execute();
            }
            catch (Exception ex)
            {
                AsyncCommand.Report(ex);     // to the error bar, same as async commands
            }
        }
    }

    /// <summary>
    /// A command that runs a task, disables itself while running and hands
    /// any failure to <see cref="Failed"/> instead of crashing the app.
    /// </summary>
    public sealed class AsyncCommand : ICommand
    {
        /// <summary>Where failures of every command, and errors reported by the view models, end up (the main window shows them).</summary>
        public static event Action<Exception>? Failed;

        public static void Report(Exception ex) => Failed?.Invoke(ex);

        readonly Func<Task> _execute;
        readonly Func<bool>? _canExecute;
        bool _running;

        public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => AppHost.I.RequerySuggested += value;
            remove => AppHost.I.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            _running = true;
            AppHost.I.RequeryCommands();
            try
            {
                await _execute();
            }
            catch (OperationCanceledException)
            {
                // stopped by the user - not an error
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
            }
            finally
            {
                _running = false;
                AppHost.I.RequeryCommands();
            }
        }
    }
}
