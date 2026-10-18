using System.IO;
using System.Windows.Media;
using PZModInspector.App.Services;
using System.Windows.Media.Imaging;
using PZModInspector.Core.Analysis;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;

namespace PZModInspector.App.ViewModels;

/// <param name="RelativePath">The file alone, without the translation keys of <paramref name="Path"/>: what the link searches.</param>
public sealed record FileRow(string Path, string RelativePath, string OtherModId, string OtherModName, string FullPath);

public sealed record IssueRowViewModel(Severity Severity, string? ModId, string ModName, string Message)
{
    public string SeverityText => Text.Severity(Severity);
    public int Rank => (int)Severity;
}

public sealed class ConflictRowViewModel(FileConflict conflict, Func<string, string> nameOf)
{
    public string File { get; } = Text.ConflictFile(conflict);
    public string WinnerId { get; } = conflict.Winner.ModId;
    /// <summary>The first mod in the "replaces" column: a double click there selects it.</summary>
    public string OverriddenId { get; } = conflict.Overridden[0].ModId;
    public string Winner { get; } = nameOf(conflict.Winner.ModId);
    public string Overridden { get; } = string.Join(", ", conflict.Overridden.Select(o => nameOf(o.ModId)));
    public int OverriddenCount { get; } = conflict.Overridden.Count;
    public bool IsLua { get; } = conflict.RelativePath.EndsWith(".lua", StringComparison.Ordinal);
    /// <summary>Text or binary, from the copy the game uses (<see cref="FileKinds.IsText"/>).</summary>
    public bool IsText { get; } = FileKinds.IsText(conflict.Winner.AbsolutePath);
    public string WinnerPath { get; } = conflict.Winner.AbsolutePath;

    /// <summary>The different copies in load order, the one the game uses last: what the Diff button compares.</summary>
    public IReadOnlyList<DiffSide> DiffSides { get; } =
        [.. conflict.Overridden.Append(conflict.Winner).Select(o => new DiffSide(o.AbsolutePath, $"{nameOf(o.ModId).Trim()} (#{o.LoadIndex})"))];
}

/// <summary>Shared data the rows need to describe their dependencies, conflicts, files and log entries.</summary>
public sealed class RowContext
{
    public RowContext(InspectionResult result, Inspector inspector, FileIndex files)
    {
        Result = result;
        Inspector = inspector;
        Files = files;
        foreach (var conflict in result.Conflicts)
        {
            Add(Won, conflict.Winner.ModId, conflict);
            foreach (var loser in conflict.Overridden)
                Add(Lost, loser.ModId, conflict);
        }
    }

    public InspectionResult Result { get; }
    public Inspector Inspector { get; }

    /// <summary>Every file of the loaded mods, scanned once per analysis.</summary>
    public FileIndex Files { get; }

    public Dictionary<string, List<FileConflict>> Won { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FileConflict>> Lost { get; } = new(StringComparer.Ordinal);

    public string NameOf(string id) => DetailRows.NameOf(Result, Inspector.Catalog, id);

    /// <summary>"Name [id]", or just the id when the name is the same.</summary>
    public string Label(string id)
    {
        var name = NameOf(id).Trim();
        return name == id ? id : $"{name} [{id}]";
    }

    /// <summary>The id when the mod is a row of the grid, so a link can select it; null otherwise.</summary>
    public string? SelectId(string id) => Result.Find(id) is null ? null : id;

    /// <summary>Whether the base game has the same file: the mods overwrite it.</summary>
    public bool InGame(string relativePath) => GamePath(relativePath) is not null;

    /// <summary>The base game's copy of the file, or null when the game does not have it.</summary>
    public string? GamePath(string relativePath) =>
        Inspector.Environment.GameDirectory is { } game && Path.Combine(game, relativePath) is var path && File.Exists(path) ? path : null;

    private static void Add(Dictionary<string, List<FileConflict>> map, string id, FileConflict conflict)
    {
        if (!map.TryGetValue(id, out var list))
            map[id] = list = [];
        list.Add(conflict);
    }
}

/// <summary>One mod in the grid, plus the details shown when it is selected.</summary>
public sealed class ModRowViewModel
{
    private readonly RowContext _context;
    private bool _detailsBuilt;

    public ModRowViewModel(ModEntry entry, RowContext context)
    {
        Entry = entry;
        _context = context;
        var mod = entry.Mod;

        // Info notes (overridden files, Java mods...) stay in the details; the dot only shows warnings and errors.
        Severity = entry.WorstSeverity is { } worst && worst >= Core.Model.Severity.Warning ? worst : null;
        StatusRank = Severity is { } s ? (int)s + 1 : 0;
        StatusText = entry.IsLoaded ? Text.Severity(Severity) : Loc.Instance["Status.NotLoaded"];
        LoadSortKey = entry.LoadIndex ?? 100_000 + (entry.ListIndex ?? 0);
        ListSortKey = entry.ListIndex ?? 100_000 + (entry.LoadIndex ?? 0);
        Updated = entry.Workshop?.TimeUpdated ?? entry.Install?.TimeUpdated;
        UpdatedText = Updated is { } u ? u.ToLocalTime().ToString("d", Loc.Instance.Culture) : "";
        Author = mod?.Info.Author ?? "";
        Version = mod?.Info.ModVersion ?? "";
        OriginText = Text.Origin(mod?.Origin);
        var mainIssue = entry.Issues.OrderByDescending(i => i.Severity).FirstOrDefault(i => i.Severity >= Core.Model.Severity.Warning);
        ProblemsText = mainIssue is null ? "" : Text.Issue(mainIssue);
        StatusTip = ProblemsText.Length == 0 ? StatusText : StatusText + "\n" + ProblemsText;
        IssueCount = entry.Issues.Count(i => i.Severity >= Core.Model.Severity.Warning);
    }

    public ModEntry Entry { get; }

    public string Id => Entry.Id;
    public string Name => Entry.DisplayName;
    public int? LoadIndex => Entry.LoadIndex;
    public int? ListIndex => Entry.ListIndex;
    public ulong? WorkshopId => Entry.WorkshopId;
    public string WorkshopTitle => Entry.Workshop?.Title ?? "";
    public string? WorkshopUrl => Entry.WorkshopId is { } id ? WorkshopLinks.Item(id) : null;
    public string? ChangeNotesUrl => Entry.WorkshopId is { } id ? WorkshopLinks.ChangeNotes(id) : null;
    public string? SteamUrl => Entry.WorkshopId is { } id ? WorkshopLinks.SteamClient(id) : null;
    public string? FolderPath => Entry.Mod?.Directory;
    public string? ModInfoPath => Entry.Mod?.ModInfoPath;
    public bool IsLoaded => Entry.IsLoaded;
    public bool IsAutoLoaded => Entry.AutoLoadedBy is not null;

    public Severity? Severity { get; }
    public int StatusRank { get; }
    public string StatusText { get; }
    /// <summary>Tooltip of the dot: the status and the main problem, as in the Problems column.</summary>
    public string StatusTip { get; }
    public int LoadSortKey { get; }
    public int ListSortKey { get; }
    public DateTimeOffset? Updated { get; }
    public string UpdatedText { get; }
    public string Author { get; }
    public string Version { get; }
    public string OriginText { get; }
    public string ProblemsText { get; }
    public int IssueCount { get; }

    public bool Matches(string search) =>
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Id.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (WorkshopId?.ToString().Contains(search, StringComparison.Ordinal) ?? false)
        || Author.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    // ---- Details (built on first selection) ----

    public IReadOnlyList<IssueRowViewModel> Issues { get; private set; } = [];
    public IReadOnlyList<InfoRow> InfoRows { get; private set; } = [];
    public IReadOnlyList<DependencyGroup> Dependencies { get; private set; } = [];
    public IReadOnlyList<FileRow> FilesLost { get; private set; } = [];
    public IReadOnlyList<FileRow> FilesWon { get; private set; } = [];
    public IReadOnlyList<InfoRow> RawModInfo { get; private set; } = [];
    public IReadOnlyList<string> OtherCopies { get; private set; } = [];
    public string Description { get; private set; } = "";
    public string WorkshopDescription { get; private set; } = "";
    public ImageSource? Poster { get; private set; }
    public string FilesLostHeader => Loc.Instance.Format("Files.Lost", FilesLost.Count);
    public string FilesWonHeader => Loc.Instance.Format("Files.Won", FilesWon.Count);

    public ModRowViewModel EnsureDetails()
    {
        if (_detailsBuilt)
            return this;
        _detailsBuilt = true;

        Issues = Entry.Issues
            .OrderByDescending(i => i.Severity)
            .Select(i => new IssueRowViewModel(i.Severity, Id, Name, Text.Issue(i)))
            .ToList();
        InfoRows = DetailRows.ModInfo(Entry, _context.Result, _context.Inspector.Catalog);
        Dependencies = DetailRows.Dependencies(Entry, _context.Result, _context.Inspector.Catalog);
        FilesLost = BuildFiles(_context.Lost, c => c.Winner.ModId);
        FilesWon = BuildFiles(_context.Won, c => c.Overridden[^1].ModId);
        RawModInfo = Entry.Mod?.Info.RawEntries.Select(e => new InfoRow(e.Key, e.Value)).ToList() ?? [];
        OtherCopies = Entry.OtherCopies.Select(c => c.Directory).ToList();
        Description = Entry.Mod?.Info.Description.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase).Replace("\\n", "\n") ?? "";
        WorkshopDescription = Text.StripBbCode(Entry.Workshop?.Description);
        Poster = LoadPoster();
        return this;
    }

    private List<FileRow> BuildFiles(Dictionary<string, List<FileConflict>> map, Func<FileConflict, string> other) =>
        map.GetValueOrDefault(Id)?.Select(c =>
        {
            var otherId = other(c);
            var own = c.Winner.ModId == Id ? c.Winner : c.Overridden.First(o => o.ModId == Id);
            return new FileRow(Text.ConflictFile(c), c.RelativePath, otherId, _context.NameOf(otherId), own.AbsolutePath);
        }).ToList() ?? [];

    private ImageSource? LoadPoster()
    {
        try
        {
            var local = Entry.Mod?.Info.PosterPaths.FirstOrDefault(File.Exists);
            if (local is not null)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 480;
                image.UriSource = new Uri(local);
                image.EndInit();
                image.Freeze();
                return image;
            }
            if (Entry.Workshop?.PreviewUrl is { Length: > 0 } url)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.DecodePixelWidth = 480;
                image.UriSource = new Uri(url);
                image.EndInit();
                return image;
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
        }
        return null;
    }
}
