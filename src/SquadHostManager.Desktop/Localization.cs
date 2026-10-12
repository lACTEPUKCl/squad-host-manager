using System.Globalization;
using System.Text;
using System.Windows;
using System.IO;

namespace SquadHostManager.Desktop;

public static class Localization
{
    public static readonly string[] Languages = ["ru", "en", "de", "es", "fr", "zh-Hans", "tr"];
    public static string Language { get; private set; } = "en";
    private static readonly Dictionary<string, string[]> Strings = Load();
    private static Dictionary<string, string[]> Load()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("SquadHostManager.Desktop.Strings.tsv")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var rows = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.TrimEnd('\r').Split('|')).ToList();
        if (rows.Any(r => r.Length != Languages.Length + 1 || r.Skip(1).Any(string.IsNullOrWhiteSpace))) throw new InvalidDataException("Incomplete localization resource.");
        return rows.ToDictionary(a => a[0], a => a.Skip(1).Select(text=>text.Replace("\\n","\n")).ToArray());
    }
    public static string T(string key, params object?[] args)
    {
        var text = Strings.TryGetValue(key, out var values) ? values[Array.IndexOf(Languages, Language)] : key;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentUICulture, text, args);
    }
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquadHostManager", "language.txt");
    public static void Initialize()
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        var language = File.Exists(PathName) ? File.ReadAllText(PathName).Trim() : culture == "zh" ? "zh-Hans" : culture;
        Apply(Languages.Contains(language) ? language : "en", false);
    }
    public static void Apply(string language, bool save = true)
    {
        if (!Languages.Contains(language)) return; Language = language; CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        if (Application.Current != null)
        {
            Application.Current.Resources["UiLanguage"]=System.Windows.Markup.XmlLanguage.GetLanguage(language);
            foreach (var key in Strings.Keys) Application.Current.Resources[key] = T(key);
        }
        if (save) { Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); File.WriteAllText(PathName, language); }
    }
}
