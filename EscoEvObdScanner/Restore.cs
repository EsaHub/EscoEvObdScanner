namespace EscoEvObdScanner
{
    /// <summary>Runs the given action once, when disposed: puts something back as it was.</summary>
    sealed class Restore(Action undo) : IDisposable
    {
        Action? _undo = undo;

        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }
}
