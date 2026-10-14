using System.Windows;
using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.App.Views
{
    /// <summary>Picks one of the car's recordings, shown by what they hold rather than by file name.</summary>
    public partial class RecordingPicker : Window
    {
        public RecordingPicker(IReadOnlyList<RecordingChoice> choices)
        {
            InitializeComponent();
            List.ItemsSource = choices;
            if (choices.Count > 0) List.SelectedIndex = 0;
        }

        /// <summary>The chosen recording's main file; null until one is opened.</summary>
        public string? Chosen { get; private set; }

        /// <summary>A double click on a recording opens it; one on the scroll bar or the empty space does not.</summary>
        void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && System.Windows.Controls.ItemsControl.ContainerFromElement(List, source) is System.Windows.Controls.ListBoxItem)
                OnOpen(sender, e);
        }

        void OnOpen(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is not RecordingChoice choice) return;
            Chosen = choice.Path;
            DialogResult = true;
        }
    }
}
