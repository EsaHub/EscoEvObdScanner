using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace EscoEvObdScanner.AppLogic.Infrastructure
{
    /// <summary>
    /// UI texts, one file per language (Localization/fi.json, en.json, ...,
    /// built into the app), switchable at runtime. Another language is another
    /// file with the same keys. The UI binds to the indexer (the Windows app through
    /// its <c>{i:T key}</c> markup); code uses <c>Loc.I["key"]</c>. A key missing in the chosen language falls back
    /// to English, and a key missing everywhere shows as itself, which makes
    /// gaps obvious. The choice also picks the names in car files (<see cref="CarText"/>)
    /// and how dates and numbers are written (<see cref="CultureFor"/>).
    /// </summary>
    public sealed class Loc : INotifyPropertyChanged
    {
        public const string Fallback = "en";

        /// <summary>
        /// The system culture, captured before the app changes it; before <see cref="I"/>, which uses it.
        /// Its standard formats, without the user's own changes in Windows (a point as the Finnish decimal
        /// separator): WPF bindings take the formats from the language tag alone, so code and bindings
        /// write numbers alike only this way.
        /// </summary>
        static readonly CultureInfo SystemCulture = CultureInfo.GetCultureInfo(CultureInfo.CurrentCulture.Name);

        public static Loc I { get; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        readonly Dictionary<string, Dictionary<string, string>> _texts = LoadAll();
        Dictionary<string, string> _current;
        string _language = "fi";

        Loc()
        {
            _current = _texts.GetValueOrDefault(_language) ?? new();
            CarText.Language = _language;
            UseCulture();
        }

        /// <summary>
        /// The remembered language, else the system language if there is a file for it, else English.
        /// </summary>
        public void Choose(string? remembered)
        {
            string system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            Language = remembered != null && Languages.Contains(remembered) ? remembered
                : Languages.Contains(system) ? system : Fallback;
        }

        /// <summary>
        /// Dates and numbers as written in the language: the system formats when they are of that
        /// language, Finnish formats for Finnish (4.10.2026 17.58, where "English (Finland)" would give
        /// 04/10/2026), else the system formats.
        /// </summary>
        public static CultureInfo CultureFor(string language) =>
            SystemCulture.TwoLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase) ? SystemCulture
            : language.Equals("fi", StringComparison.OrdinalIgnoreCase) ? CultureInfo.GetCultureInfo("fi-FI")
            : SystemCulture;

        void UseCulture()
        {
            var culture = CultureFor(_language);
            CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = culture;
        }

        /// <summary>The languages there is a file for, by code.</summary>
        public IReadOnlyList<string> Languages => _texts.Keys.OrderBy(k => k == "fi" ? 0 : 1).ThenBy(k => k).ToList();

        public string Language
        {
            get => _language;
            set
            {
                if (_language == value || !_texts.ContainsKey(value)) return;
                _language = value;
                _current = _texts[value];
                CarText.Language = value;
                UseCulture();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            }
        }

        public string this[string key] =>
            _current.TryGetValue(key, out var t) ? t
            : _texts.TryGetValue(Fallback, out var en) && en.TryGetValue(key, out var e) ? e
            : key;

        public string Format(string key, params object[] args) => string.Format(this[key], args);

        /// <summary>Every Localization/*.json built into the app, by language code.</summary>
        static Dictionary<string, Dictionary<string, string>> LoadAll()
        {
            var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Localization.") && n.EndsWith(".json")))
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                var texts = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
                all[name["Localization.".Length..^".json".Length]] = texts;
            }
            return all;
        }
    }
}
