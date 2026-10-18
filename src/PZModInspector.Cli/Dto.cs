using PZModInspector.Core.Localization;

namespace PZModInspector.Cli;

// What every command prints: the text output is made from these records, --json prints them as they are.

internal sealed record HeaderDto(string Source, string SourceTitle, string GameVersion, DateTimeOffset? WorkshopDataUtc, bool HasApiKey);

internal sealed record SourceDto(string Key, string Title, string ModsFile, DateTimeOffset? LastPlayedUtc, bool IsLatestSave, bool Selected);

internal sealed record IssueDto(string Severity, string Code, string? ModId, string? ModName, string Message, IReadOnlyList<string> Args);

internal sealed record EnvDto(HeaderDto Header, IReadOnlyList<InfoRow> Rows, IReadOnlyList<IgnoredFolderRow> IgnoredFolders);

internal sealed record ReportDto(HeaderDto Header, int Listed, int Loaded, int Errors, int Warnings, int Infos, int FileConflicts,
    IReadOnlyList<IssueDto> Issues);

internal sealed record ModRowDto(int? LoadIndex, int? ListIndex, string Status, string Id, string Name, ulong? WorkshopId, string? Version,
    DateTimeOffset? UpdatedUtc, int Problems, string? AutoLoadedBy);

internal sealed record ModsDto(HeaderDto Header, IReadOnlyList<ModRowDto> Mods);

/// <param name="Keys">For a translation file: the keys with another text.</param>
internal sealed record FileRowDto(string Path, string OtherModId, string OtherModName, int OtherLoadIndex, IReadOnlyList<string>? Keys);

internal sealed record ModDto(HeaderDto Header, string Id, string Name, string Status, IReadOnlyList<InfoRow> Info,
    IReadOnlyList<DependencyGroup> Dependencies, IReadOnlyList<IssueDto> Issues, IReadOnlyList<FileRowDto> FilesLost,
    IReadOnlyList<FileRowDto> FilesWon, IReadOnlyList<string> OtherCopies);

/// <param name="Used">The game reads this copy: the one of the mod loaded last, or every copy of a file the game merges.</param>
/// <param name="Use">used, replaced, replacedBySame, merged or unused.</param>
internal sealed record OwnerDto(string ModId, string ModName, int LoadIndex, string Path, bool Used, string Use);

/// <param name="InGame">The game has the same file.</param>
/// <param name="FileUse">replaced (the last copy counts), merged (all copies are read) or unused.</param>
internal sealed record FileMatchDto(string RelativePath, bool InGame, string FileUse, IReadOnlyList<OwnerDto> Owners);

internal sealed record FilesDto(HeaderDto Header, string Query, int Total, IReadOnlyList<FileMatchDto> Files);

/// <param name="Files">Relative paths; for a translation file followed by the keys with another text.</param>
internal sealed record ConflictPairDto(string WinnerId, string WinnerName, int WinnerLoadIndex, string LoserId, string LoserName,
    int LoserLoadIndex, IReadOnlyList<string> Files);

internal sealed record ConflictsDto(HeaderDto Header, int Files, int LuaFiles, IReadOnlyList<ConflictPairDto> Pairs);

internal sealed record ModRefDto(string Id, string Name, int? LoadIndex);

/// <param name="Found">The module is at that path, but its folder was not loaded yet.</param>
/// <param name="SameName">Nothing at that path: files with the same name elsewhere.</param>
internal sealed record RequireDto(string Module, IReadOnlyList<FileMatchDto> Found, IReadOnlyList<FileMatchDto> SameName);

internal sealed record LogGroupDto(string Severity, int Count, int FirstLine, int LastLine, int Frame, string Category, string? Location,
    string Message, string? Exception, IReadOnlyList<string> LuaFrames, IReadOnlyList<ModRefDto> Mods, IReadOnlyList<FileMatchDto> Files,
    RequireDto? Require);

internal sealed record LogDto(HeaderDto Header, string Path, DateTimeOffset ModifiedUtc, int Errors, int Warnings, IReadOnlyList<LogGroupDto> Groups);

internal sealed record RefreshDto(bool Ok, DateTimeOffset? WorkshopDataUtc, bool HasApiKey, bool ApiKeyRejected, string? Error);
