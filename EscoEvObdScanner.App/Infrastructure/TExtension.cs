using System.Windows.Data;
using System.Windows.Markup;
using EscoEvObdScanner.AppLogic.Infrastructure;

namespace EscoEvObdScanner.App.Infrastructure
{
    /// <summary>XAML: <c>Text="{i:T Connect}"</c>. Follows language changes live.</summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class TExtension : MarkupExtension
    {
        public TExtension(string key) { Key = key; }

        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider) =>
            new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
    }
}
