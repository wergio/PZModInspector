using PZModInspector.Core.Model;

namespace PZModInspector.Core.Environment;

/// <summary>Why the program cannot work with this installation.</summary>
public enum GameProblem
{
    /// <summary>Steam or Project Zomboid among its games not found.</summary>
    GameNotFound,

    /// <summary>projectzomboid.jar missing or not readable.</summary>
    VersionUnreadable,

    /// <summary>Before build 42: mod folders and rules are different.</summary>
    VersionTooOld,
}

/// <summary>Thrown by <see cref="Analysis.Inspector.Load(PzEnvironment, Workshop.WorkshopService)"/> when <see cref="PzEnvironment.GameProblem"/> is set.</summary>
public sealed class GameCheckException(PzEnvironment environment) : Exception($"Project Zomboid: {environment.GameProblem}")
{
    public PzEnvironment Environment { get; } = environment;
}

/// <summary>Paths the user can force from the settings when automatic detection is wrong.</summary>
public sealed record EnvironmentOverrides(string? SteamRoot = null, string? ZomboidDirectory = null);

/// <summary>Where Project Zomboid, its Workshop items and the user folder are on this PC.</summary>
public sealed class PzEnvironment
{
    public string? SteamRoot { get; init; }
    public string? GameDirectory { get; init; }
    public long? GameBuildId { get; init; }
    public DateTimeOffset? GameLastUpdated { get; init; }
    public string? WorkshopContentDirectory { get; init; }
    public string? WorkshopManifestPath { get; init; }
    public required string ZomboidDirectory { get; init; }
    public bool ZomboidDirectoryFromLaunchOptions { get; init; }

    /// <summary>Read from projectzomboid.jar (<see cref="GameJarVersion"/>); meaningless when <see cref="GameProblem"/> is set.</summary>
    public required GameVersion GameVersion { get; init; }

    /// <summary>The jar the version comes from, also when it cannot be read.</summary>
    public string? GameJarPath { get; init; }

    /// <summary>Set when the program cannot work: no game, no readable version, or a version before 42.</summary>
    public GameProblem? GameProblem { get; init; }

    public bool ZombieBuddyInstalled =>
        GameDirectory is not null && File.Exists(Path.Combine(GameDirectory, "ZombieBuddy.jar"));

    public string ModsDirectory => Path.Combine(ZomboidDirectory, "mods");
    public string SavesDirectory => Path.Combine(ZomboidDirectory, "Saves");
    public string StagedWorkshopDirectory => Path.Combine(ZomboidDirectory, "Workshop");
    public string DefaultModsFile => Path.Combine(ModsDirectory, "default.txt");
}

public static class EnvironmentDetector
{
    public static PzEnvironment Detect(EnvironmentOverrides? overrides = null)
    {
        overrides ??= new EnvironmentOverrides();

        var steamRoot = !string.IsNullOrWhiteSpace(overrides.SteamRoot) ? overrides.SteamRoot : SteamLocator.FindSteamRoot();
        SteamAppInstall? game = null;
        if (steamRoot is not null)
            game = SteamLocator.FindApp(SteamLocator.GetLibraries(steamRoot), SteamLocator.ProjectZomboidAppId);

        string? workshopContent = null, workshopManifest = null;
        if (game is not null)
        {
            workshopContent = Path.Combine(game.LibraryPath, "steamapps", "workshop", "content", SteamLocator.ProjectZomboidAppId);
            workshopManifest = Path.Combine(game.LibraryPath, "steamapps", "workshop", $"appworkshop_{SteamLocator.ProjectZomboidAppId}.acf");
        }

        var (zomboidDir, fromLaunchOptions) = FindZomboidDirectory(overrides, steamRoot);

        // Only the version compiled into the installed game: no console.txt, no guess.
        var jar = game is null ? null : Path.Combine(game.InstallDirectory, "projectzomboid.jar");
        var version = GameVersion.MinRequired;
        GameProblem? problem = null;
        if (jar is null)
            problem = GameProblem.GameNotFound;
        else if (GameJarVersion.Read(jar) is not { } fromJar)
            problem = GameProblem.VersionUnreadable;
        else
        {
            version = fromJar;
            if (fromJar < GameVersion.MinRequired)
                problem = GameProblem.VersionTooOld;
        }

        return new PzEnvironment
        {
            SteamRoot = steamRoot,
            GameDirectory = game?.InstallDirectory,
            GameBuildId = game?.BuildId,
            GameLastUpdated = game?.LastUpdated,
            WorkshopContentDirectory = workshopContent,
            WorkshopManifestPath = workshopManifest,
            ZomboidDirectory = zomboidDir,
            ZomboidDirectoryFromLaunchOptions = fromLaunchOptions,
            GameVersion = version,
            GameJarPath = jar,
            GameProblem = problem,
        };
    }

    private static (string Directory, bool FromLaunchOptions) FindZomboidDirectory(EnvironmentOverrides overrides, string? steamRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrides.ZomboidDirectory))
            return (Path.GetFullPath(overrides.ZomboidDirectory), false);

        if (steamRoot is not null)
        {
            var fromOptions = SteamLocator.FindCacheDirLaunchOptions(steamRoot).FirstOrDefault(Directory.Exists);
            if (fromOptions is not null)
                return (Path.GetFullPath(fromOptions), true);
        }

        // ZomboidFileSystem.getCacheDir: on Windows always user.home + "\Zomboid".
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return (Path.Combine(home, "Zomboid"), false);
    }
}
