using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EscoEvObdScanner.App.Controls
{
    /// <summary>A labelled number with its unit. Looks come from the implicit style in Theme.xaml.</summary>
    public sealed class Tile : Control
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(Tile), new PropertyMetadata(""));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(string), typeof(Tile), new PropertyMetadata("—"));

        public static readonly DependencyProperty UnitProperty =
            DependencyProperty.Register(nameof(Unit), typeof(string), typeof(Tile), new PropertyMetadata(""));

        public static readonly DependencyProperty NoteProperty =
            DependencyProperty.Register(nameof(Note), typeof(string), typeof(Tile), new PropertyMetadata(""));

        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(Tile), new PropertyMetadata(null));

        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

        /// <summary>A small line under the value, such as what a figure is based on; hidden when empty.</summary>
        public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }
    }
}
