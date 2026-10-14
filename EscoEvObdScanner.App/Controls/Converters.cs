using System.Globalization;
using System.Windows.Data;

namespace EscoEvObdScanner.App.Controls
{
    /// <summary>Unit scaling for bindings: V → mV, kΩ → MΩ and so on.</summary>
    public sealed class ScaleConverter : IValueConverter
    {
        public static readonly ScaleConverter Times1000 = new(1000);
        public static readonly ScaleConverter Div1000 = new(0.001);

        readonly double _factor;

        ScaleConverter(double factor) { _factor = factor; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is double d ? d * _factor : value is int i ? i * _factor : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A cell statistic: [value, format] or [value, format, cell number] gives
    /// "3,581" or "3,581  (#88)"; "—" until there is data.
    /// </summary>
    public sealed class CellStatConverter : IMultiValueConverter
    {
        public static readonly CellStatConverter Instance = new();

        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double v) return "—";
            string text = v.ToString(values[1] as string ?? "F3", culture);
            return values.Length > 2 && values[2] is int n ? $"{text}  (#{n})" : text;
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>"12 / 15" from two nullable numbers; "—" when either is missing.</summary>
    public sealed class PairConverter : IMultiValueConverter
    {
        public static readonly PairConverter Instance = new();

        public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            string format = parameter as string ?? "F0";
            return values.Length == 2 && values[0] is double a && values[1] is double b
                ? $"{a.ToString(format, culture)} / {b.ToString(format, culture)}"
                : "—";
        }

        public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
