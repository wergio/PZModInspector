using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PZModInspector.Core.Environment;

namespace PZModInspector.Core.Settings;

/// <summary>User preferences, saved in %USERPROFILE%\PZModInspector\settings.json next to the Workshop cache.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = "PZModInspector.SteamApiKey"u8.ToArray();
    private static readonly string[] DataFiles = ["settings.json", "workshop-cache.json"];

    /// <summary>
    /// Outside AppData on purpose: Windows gives packaged (MSIX) apps, like the Claude desktop app, a private copy of what
    /// they write in AppData, so pzmi started from Claude and the program started from Windows would see different files.
    /// </summary>
    public static string DataDirectory { get; } =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "PZModInspector");

    /// <summary>Where earlier versions kept settings and cache: copied once into <see cref="DataDirectory"/>.</summary>
    public static string LegacyDataDirectory { get; } =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "PZModInspector");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string WorkshopCachePath => Path.Combine(DataDirectory, "workshop-cache.json");

    public string Language { get; set; } = "auto";
    public string? SteamDirectory { get; set; }
    public string? ZomboidDirectory { get; set; }

    /// <summary>Steam Web API key encrypted with DPAPI (readable only by this Windows user), Base64.</summary>
    public string? SteamApiKeyProtected { get; set; }

    /// <summary>Executable of a program that compares two files (e.g. WinMergeU.exe); null: no Diff buttons.</summary>
    public string? DiffToolPath { get; set; }

    public string? LastSource { get; set; }

    /// <summary>Columns of the mod list changed by the user; columns missing here keep their defaults.</summary>
    public List<ColumnLayout>? ModColumns { get; set; }

    /// <summary>Widths of the columns of the other lists: list name → column key → width.</summary>
    public Dictionary<string, Dictionary<string, double>>? ColumnWidths { get; set; }

    /// <summary>Steam Web API key, needed for the "Required items" of the Workshop; null when not set. DPAPI exists only on Windows.</summary>
    [JsonIgnore]
    public string? SteamApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(SteamApiKeyProtected) || !OperatingSystem.IsWindows())
                return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(SteamApiKeyProtected), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !OperatingSystem.IsWindows())
                SteamApiKeyProtected = null;
            else
                SteamApiKeyProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }
    }

    /// <summary>The 32 hexadecimal characters steamcommunity.com/dev/apikey shows.</summary>
    public static bool IsValidApiKey(string key) => key.Length == 32 && key.All(char.IsAsciiHexDigit);

    public EnvironmentOverrides ToOverrides() => new(Blank(SteamDirectory), Blank(ZomboidDirectory));

    public static AppSettings Load()
    {
        CopyLegacyData(LegacyDataDirectory, DataDirectory);
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Only while the new folder does not exist yet; the old folder is left as it is.</summary>
    internal static void CopyLegacyData(string legacyDirectory, string dataDirectory)
    {
        if (Directory.Exists(dataDirectory) || !Directory.Exists(legacyDirectory))
            return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            foreach (var name in DataFiles)
            {
                var source = Path.Combine(legacyDirectory, name);
                if (File.Exists(source))
                    File.Copy(source, Path.Combine(dataDirectory, name), overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Width, visibility and position of a grid column chosen by the user; <see cref="Key"/> names the column.</summary>
public sealed record ColumnLayout(string? Key, double Width, bool Visible, int DisplayIndex);
