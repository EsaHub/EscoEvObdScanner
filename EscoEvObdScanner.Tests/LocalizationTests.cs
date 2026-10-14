using System.Text.Json;
using System.Text.RegularExpressions;

namespace EscoEvObdScanner.Tests
{
    /// <summary>
    /// The app's language files (EscoEvObdScanner.AppLogic/Localization/*.json):
    /// every language has every text, with the same placeholders, and every
    /// key the app uses (shared code and the Windows app) exists. Read from the source tree.
    /// </summary>
    public class LocalizationTests
    {
        static string SourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EscoEvObdScanner.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("source tree not found");
        }

        static string LogicDir => Path.Combine(SourceRoot(), "EscoEvObdScanner.AppLogic");

        /// <summary>Where the code that looks texts up is.</summary>
        static IEnumerable<string> CodeDirs => new[] { LogicDir, Path.Combine(SourceRoot(), "EscoEvObdScanner.App") };

        static Dictionary<string, Dictionary<string, string>> Languages() =>
            Directory.GetFiles(Path.Combine(LogicDir, "Localization"), "*.json").ToDictionary(
                f => Path.GetFileNameWithoutExtension(f),
                f => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!);

        static IEnumerable<string> Placeholders(string text) =>
            Regex.Matches(text, @"\{(\d+)(:[^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Order();

        [Fact]
        public void FinnishAndEnglishAreThere()
        {
            var langs = Languages();
            Assert.Contains("fi", langs.Keys);
            Assert.Contains("en", langs.Keys);
        }

        [Fact]
        public void EveryLanguageHasEveryTextWithTheSamePlaceholders()
        {
            var langs = Languages();
            var en = langs["en"];
            foreach (var (code, texts) in langs)
            {
                Assert.Empty(en.Keys.Except(texts.Keys));                   // a text missing in this language
                Assert.Empty(texts.Keys.Except(en.Keys));                   // a text only this language has
                foreach (var (key, text) in texts)
                {
                    Assert.False(string.IsNullOrWhiteSpace(text), $"{code}.{key} is empty");
                    Assert.True(Placeholders(text).SequenceEqual(Placeholders(en[key])), $"{code}.{key}: placeholders differ from English");
                }
            }
        }

        [Fact]
        public void EveryKeyTheAppUsesExists()
        {
            var en = Languages()["en"];
            var used = new HashSet<string>();
            foreach (var file in CodeDirs.SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
                         .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\")
                                     && !f.EndsWith("Loc.cs")))     // its doc comment shows Loc.I["key"] as an example
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Regex.Matches(text, @"Loc\.I\[""(\w+)""\]|Loc\.I\.Format\(""(\w+)""|\{i:T (\w+)\}|\bT\(""(\w+)""\)"))
                    used.Add(m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value);
                // keys chosen in an expression (a ternary, a switch arm) on a line that looks a text up
                // (also the report's Add("Key", ...), a ternary's two keys, and records built from a key: new("Key", ...))
                foreach (var line in text.Split('\n').Where(l => l.Contains("Loc.I[") || l.Contains("Loc.I.Format(") || Regex.IsMatch(l, @"\bT\(")
                                                                 || Regex.IsMatch(l, @"=>\s*""[A-Z][a-z]\w*"",?\s*$")
                                                                 || Regex.IsMatch(l, @"\bAdd\(""[A-Z]\w*"",|\?\s*""[A-Z]\w*""\s*:\s*""[A-Z]\w*""|\bnew\(""[A-Z]\w*"",")))
                    foreach (Match m in Regex.Matches(line, @"""([A-Z][a-z][A-Za-z0-9]*(?:_[A-Za-z]+)?)"""))
                        used.Add(m.Groups[1].Value);
            }
            Assert.True(used.Count > 100, $"only {used.Count} keys found: the search is broken");
            Assert.DoesNotContain(used, k => !en.ContainsKey(k));

            // the DTC status bits keep their ISO 14229-1 names; the meaning column has a text for each of the eight bits
            Assert.DoesNotContain(en.Keys, k => k.StartsWith("DtcBit_"));
            Assert.All(Enumerable.Range(0, 8), i => Assert.True(en.ContainsKey($"DtcBit{i}"), $"DtcBit{i}"));
        }
    }
}
