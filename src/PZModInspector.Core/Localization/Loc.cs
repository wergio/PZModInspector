using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PZModInspector.Core.Localization;

/// <summary>
/// UI strings from the embedded Strings.&lt;lang&gt;.json files. Bindings to the indexer update when the language changes;
/// English is the fallback for missing keys.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Auto = "auto";
    public static readonly IReadOnlyList<string> Languages = ["it", "en"];

    private Dictionary<string, string> _strings = [];
    private readonly Dictionary<string, string> _fallback;

    private Loc()
    {
        _fallback = Load("en");
        _strings = _fallback;
    }

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string Language { get; private set; } = "en";
    /// <summary>Culture used to format dates and numbers.</summary>
    public CultureInfo Culture { get; private set; } = CultureInfo.CurrentCulture;

    public string this[string key] =>
        _strings.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value) ? value : key;

    public bool Has(string key) => _strings.ContainsKey(key) || _fallback.ContainsKey(key);

    public string Format(string key, params object?[] args) => string.Format(Culture, this[key], args);

    /// <param name="language">"it", "en" or "auto" (the Windows display language, English when not Italian).</param>
    public void SetLanguage(string language)
    {
        var code = language == Auto
            ? (CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "it" || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en")
            : Languages.Contains(language) ? language : "en";

        Language = code;
        // Dates and numbers always follow the Windows regional settings, whatever the UI language.
        Culture = CultureInfo.CurrentCulture;
        _strings = code == "en" ? _fallback : Load(code);
        var uiCulture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        Thread.CurrentThread.CurrentUICulture = uiCulture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Dictionary<string, string> Load(string code)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PZModInspector.Strings.Strings.{code}.json");
        if (stream is null)
            return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
