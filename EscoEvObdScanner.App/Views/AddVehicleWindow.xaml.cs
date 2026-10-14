using System.Windows;
using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.App.Views
{
    /// <summary>Names a new vehicle and picks its car file; the caller adds it and says why a name cannot be used.</summary>
    public partial class AddVehicleWindow : Window
    {
        readonly Func<string, string, string?> _add;

        /// <param name="add">Adds the vehicle (name, car file path); returns the problem to show, or null when added.</param>
        public AddVehicleWindow(IReadOnlyList<CarFileChoice> files, string? carPath, Func<string, string, string?> add)
        {
            InitializeComponent();
            _add = add;
            CarBox.ItemsSource = files;
            if (carPath != null)
                CarBox.SelectedItem = files.FirstOrDefault(f => string.Equals(EscoEvObdScanner.CarFiles.NameOf(f.Path),
                                          EscoEvObdScanner.CarFiles.NameOf(carPath), StringComparison.OrdinalIgnoreCase));
            Loaded += (_, _) => NameBox.Focus();
        }

        void OnAdd(object sender, RoutedEventArgs e)
        {
            string? problem = CarBox.SelectedItem is CarFileChoice car ? _add(NameBox.Text, car.Path) : Loc.I["AddVehicleChooseCarFile"];
            if (problem != null)
            {
                ProblemText.Text = problem;
                ProblemText.Visibility = Visibility.Visible;
                return;
            }
            DialogResult = true;
        }
    }
}
