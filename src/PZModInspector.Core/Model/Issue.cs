namespace PZModInspector.Core.Model;

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Kinds of problems. The user-facing text lives in the app's string tables under "Issue.&lt;code&gt;",
/// formatted with <see cref="Issue.Args"/>.
/// </summary>
public enum IssueCode
{
    // Whole list
    ModsFileMissing,
    ModsFileVersionUnsupported,
    WorkshopQueryFailed,

    // Mod not loaded by the game
    ModNotInstalled,
    ModOnlyForOlderVersions,
    ModInfoInvalid,
    GameVersionTooOld,
    GameVersionTooNew,
    RequirementMissing,
    RequirementUnavailable,
    RequirementHasSpaces,
    RequirementCycle,

    // Loaded, but something is off
    IncompatibleModActive,
    LoadAfterViolated,
    LoadBeforeViolated,
    DuplicateModId,
    NotInSteamManifest,
    JavaModWithoutZombieBuddy,
    WorkshopItemRemoved,
    WorkshopItemBanned,
    WorkshopItemNotPublic,
    UpdatePending,
    WorkshopRequiredItemMissing,
    FilesOverridden,

    // Informative
    RequirementAutoLoaded,
    WorkshopRequiredItemOlderBuild,
    UpdatedSinceLastPlayed,
    ListedTwice,
}

/// <param name="ModId">The mod the issue is about, or null for issues about the whole list.</param>
public sealed record Issue(Severity Severity, IssueCode Code, string? ModId, IReadOnlyList<string> Args)
{
    public static Issue Of(Severity severity, IssueCode code, string? modId, params string[] args) =>
        new(severity, code, modId, args);
}
