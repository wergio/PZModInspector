using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PZModInspector.Core.Parsing;

namespace PZModInspector.Core.Environment;

public sealed record SteamAppInstall(string LibraryPath, string InstallDirectory, long? BuildId, DateTimeOffset? LastUpdated);

public static partial class SteamLocator
{
    public const string ProjectZomboidAppId = "108600";

    public static string? FindSteamRoot()
    {
        var candidates = new List<string?>();
        if (OperatingSystem.IsWindows())
        {
            // InstallPath keeps the real letter case; SteamPath (per user) is often all lower case.
            candidates.Add(ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            candidates.Add(ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
            candidates.Add(ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
        }
        var programFilesX86 = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
        if (programFilesX86.Length > 0)
            candidates.Add(Path.Combine(programFilesX86, "Steam"));
        if (programFiles.Length > 0)
            candidates.Add(Path.Combine(programFiles, "Steam"));

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Path.GetFullPath(c!.Replace('/', Path.DirectorySeparatorChar)))
            .FirstOrDefault(c => Directory.Exists(Path.Combine(c, "steamapps")));
    }

    /// <summary>Steam libraries from steamapps\libraryfolders.vdf, the Steam folder itself first.</summary>
    public static IReadOnlyList<string> GetLibraries(string steamRoot)
    {
        var libraries = new List<string> { Path.GetFullPath(steamRoot) };
        var vdf = KeyValues.ReadFile(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
        var folders = vdf?["libraryfolders"] ?? vdf?["LibraryFolders"];
        if (folders is not null)
        {
            foreach (var child in folders.Children)
            {
                // New format: "0" { "path" "..." }; old format: "1" "D:\\SteamLibrary".
                var path = child.Value ?? child.GetString("path");
                if (string.IsNullOrWhiteSpace(path) || !int.TryParse(child.Key, out _))
                    continue;
                var full = Path.GetFullPath(path);
                if (!libraries.Contains(full, StringComparer.OrdinalIgnoreCase) && Directory.Exists(full))
                    libraries.Add(full);
            }
        }
        return libraries;
    }

    public static SteamAppInstall? FindApp(IEnumerable<string> libraries, string appId)
    {
        foreach (var library in libraries)
        {
            var manifest = KeyValues.ReadFile(Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"));
            var state = manifest?["AppState"];
            var installDir = state?.GetString("installdir");
            if (state is null || string.IsNullOrWhiteSpace(installDir))
                continue;
            var directory = Path.Combine(library, "steamapps", "common", installDir);
            if (!Directory.Exists(directory))
                continue;
            var lastUpdated = state.GetLong("LastUpdated");
            return new SteamAppInstall(library, directory, state.GetLong("buildid"),
                lastUpdated is > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastUpdated.Value) : null);
        }
        return null;
    }

    /// <summary>"-cachedir=..." from the Steam launch options of Project Zomboid, for every Steam user on this PC.</summary>
    public static IEnumerable<string> FindCacheDirLaunchOptions(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata))
            yield break;
        foreach (var userDir in SafeEnumerateDirectories(userdata))
        {
            var config = KeyValues.ReadFile(Path.Combine(userDir, "config", "localconfig.vdf"));
            var apps = config?["UserLocalConfigStore"]?["Software"]?["Valve"]?["Steam"]?["apps"];
            var options = apps?[ProjectZomboidAppId]?.GetString("LaunchOptions");
            if (string.IsNullOrWhiteSpace(options))
                continue;
            var match = CacheDirRegex().Match(options);
            if (match.Success)
                yield return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }
    }

    internal static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistry(RegistryKey hive, string subKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex("-cachedir=(?:\"([^\"]+)\"|(\\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex CacheDirRegex();
}
