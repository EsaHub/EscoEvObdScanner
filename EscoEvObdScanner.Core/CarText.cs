using System.Text.Json;
using System.Text.Json.Serialization;

namespace EscoEvObdScanner
{
    /// <summary>
    /// Names in a car file come per language: "name": { "fi": "...", "en": "..." }.
    /// The app sets <see cref="Language"/> from its UI language; the command
    /// line stays in English.
    /// </summary>
    public static class CarText
    {
        /// <summary>A language code such as "fi" or "fi-FI".</summary>
        public static string Language { get; set; } = "en";

        /// <summary>
        /// The text for the language: the exact code, then the language without
        /// its region ("fi" for "fi-FI"), then English; null if none of these.
        /// </summary>
        public static string? Pick(IReadOnlyDictionary<string, string>? texts)
        {
            if (texts == null || texts.Count == 0) return null;
            string lang = Language;
            string? Get(string key) =>
                texts.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(t.Value)).Value;
            return Get(lang) ?? (lang.Contains('-') ? Get(lang[..lang.IndexOf('-')]) : null) ?? Get("en");
        }

        /// <summary>Any text at all, for when none in the wanted languages exists.</summary>
        public static string? Any(IReadOnlyDictionary<string, string>? texts) =>
            texts?.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        /// <summary>The English text, else any text; null if there is none. For the command line and saved files.</summary>
        public static string? English(IReadOnlyDictionary<string, string>? texts) =>
            texts?.FirstOrDefault(t => string.Equals(t.Key, "en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(t.Value)).Value
            ?? Any(texts);

        /// <summary>
        /// Reads a "name": an object of texts per language, or a plain string,
        /// which counts as English. Anything else reads as no name. Writes the object.
        /// </summary>
        public sealed class NamesConverter : JsonConverter<Dictionary<string, string>?>
        {
            public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                        string? text = reader.GetString();
                        return string.IsNullOrWhiteSpace(text) ? null : new(StringComparer.OrdinalIgnoreCase) { ["en"] = text };
                    case JsonTokenType.StartObject:
                        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            string language = reader.GetString()!;
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String && reader.GetString() is string value && !string.IsNullOrWhiteSpace(value))
                                texts.TryAdd(language, value);
                            else reader.Skip();
                        }
                        return texts.Count > 0 ? texts : null;
                    default:
                        reader.Skip();
                        return null;
                }
            }

            public override void Write(Utf8JsonWriter writer, Dictionary<string, string>? value, JsonSerializerOptions options)
            {
                if (value == null) { writer.WriteNullValue(); return; }
                writer.WriteStartObject();
                foreach (var (language, text) in value) writer.WriteString(language, text);
                writer.WriteEndObject();
            }
        }
    }
}
