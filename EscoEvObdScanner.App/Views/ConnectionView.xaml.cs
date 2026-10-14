using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;

namespace EscoEvObdScanner.App.Views
{
    public partial class ConnectionView : UserControl
    {
        bool _scrollPending;

        public ConnectionView()
        {
            InitializeComponent();

            // keep the newest log line in view: once per batch of lines, after they are laid out, not per line
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
            {
                if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending) return;
                _scrollPending = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _scrollPending = false;
                    // to the very end, not to the first line equal to the last one (lines repeat within a second)
                    if (LogList.Items.Count > 0) (FindScroller(LogList) is { } sv ? (Action)sv.ScrollToEnd : () => LogList.ScrollIntoView(LogList.Items[^1]))();
                });
            };
        }

        static System.Windows.Controls.ScrollViewer? FindScroller(System.Windows.DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.ScrollViewer sv) return sv;
                if (FindScroller(child) is { } found) return found;
            }
            return null;
        }
    }
}
