using PdfSharp.Fonts;

namespace EscoEvObdScanner.AppLogic.Reports
{
    /// <summary>
    /// The report's font: Segoe UI regular, bold, italic and bold italic, read
    /// from the Windows font folder for every family the report asks for.
    /// </summary>
    sealed class ReportFonts : IFontResolver
    {
        public const string Family = "Segoe UI";

        static readonly string Folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        /// <summary>Installs the resolver once; later calls do nothing.</summary>
        public static void Use()
        {
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new ReportFonts();
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            new((bold, italic) switch
            {
                (true, true) => "segoeuiz.ttf",
                (true, false) => "segoeuib.ttf",
                (false, true) => "segoeuii.ttf",
                _ => "segoeui.ttf"
            });

        public byte[]? GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Folder, faceName));
    }
}
