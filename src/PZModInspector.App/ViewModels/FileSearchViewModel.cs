using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PZModInspector.App.Services;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Mods;

namespace PZModInspector.App.ViewModels;

/// <summary>
/// A file of the loaded mods: the copy the game uses (the mod loaded last) and the ones it replaces, or, for the files the game
/// merges or does not read, that instead.
/// </summary>
public sealed class FileMatchRowViewModel
{
    public FileMatchRowViewModel(string relativePath, IReadOnlyList<FileOwner> owners, RowContext context)
    {
        var used = owners[^1];
        var uses = context.Files.CopyUses(relativePath, owners);
        string Label(FileOwner o) => $"{context.NameOf(o.ModId).Trim()} (#{o.LoadIndex})";
        File = relativePath;
        WinnerId = used.ModId;
        OverriddenId = uses[^1] is CopyUse.Used && owners.Count > 1 ? owners[0].ModId : null;
        Winner = uses[^1] switch
        {
            CopyUse.Merged when owners.Count > 1 => Loc.Instance["Files.AllMerged"],
            CopyUse.Unused => Loc.Instance["Files.NoneRead"],
            _ => Label(used),
        };
        // An identical copy is said so: replacing it changes nothing.
        Overridden = uses[^1] is CopyUse.Used
            ? string.Join(", ", owners.Take(owners.Count - 1).Select((o, i) =>
                uses[i] is CopyUse.ReplacedBySame ? $"{Label(o)} ({Loc.Instance["Files.ReplacedSame"]})" : Label(o)))
            : "";
        var gamePath = context.GamePath(relativePath);
        InGameText = gamePath is null ? "" : Loc.Instance["Common.Yes"];
        var sides = new List<DiffSide>();
        if (gamePath is not null)
            sides.Add(new DiffSide(gamePath, Loc.Instance["Log.BaseGame"]));
        sides.AddRange(owners.Select(o => new DiffSide(o.AbsolutePath, Label(o))));
        DiffSides = sides;
        // In load order, like PZModInspectorCLI file: the arrow marks the copies the game reads.
        CopiesTip = string.Join("\n", FileCopyRow.From(relativePath, owners, context)
            .Select(c => $"{(c.IsUsed ? "→" : "   ")} {c.Text} · {c.Note}\n     {c.Path}"));
    }

    public string File { get; }
    public string WinnerId { get; }
    /// <summary>The first mod in the "replaces" column, when there is one: a double click there selects it.</summary>
    public string? OverriddenId { get; }
    public string Winner { get; }
    public string Overridden { get; }
    public string InGameText { get; }
    public string CopiesTip { get; }

    /// <summary>The base game's copy first, then the mods' in load order: what the Diff button compares.</summary>
    public IReadOnlyList<DiffSide> DiffSides { get; }

    public bool CanDiff => DiffSides.Count >= 2;
}

/// <summary>The file search tab: any file of the loaded mods by name, path or pattern with * and ?, not only the conflicts.</summary>
public sealed partial class FileSearchViewModel : ObservableObject
{
    /// <summary>Enough for any useful search; "*" alone would list every file of every mod.</summary>
    private const int Limit = 1000;

    private RowContext? _context;
    private CancellationTokenSource? _pending;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IReadOnlyList<FileMatchRowViewModel> _results = [];
    [ObservableProperty] private string _summaryText = "";

    /// <summary>After each analysis (and language change): searches again in the new files.</summary>
    public void SetAnalysis(RowContext context)
    {
        _context = context;
        _ = SearchAsync(delay: false);
    }

    [RelayCommand]
    private void Clear() => Query = "";

    partial void OnQueryChanged(string value) => _ = SearchAsync(delay: true);

    private async Task SearchAsync(bool delay)
    {
        _pending?.Cancel();
        var pending = _pending = new CancellationTokenSource();
        var query = Query.Trim();
        var context = _context;
        if (context is null || query.Length == 0)
        {
            Results = [];
            SummaryText = "";
            return;
        }
        try
        {
            // While typing: one search when the user pauses, not one per key.
            if (delay)
                await Task.Delay(250, pending.Token);
            var (total, rows) = await Task.Run(() =>
            {
                var paths = context.Files.MatchPaths(query);
                return (paths.Count, paths.Take(Limit).Select(p => new FileMatchRowViewModel(p, context.Files.Owners(p), context)).ToList());
            }, pending.Token);
            if (pending.IsCancellationRequested)
                return;
            var L = Loc.Instance;
            Results = rows;
            SummaryText = total == 0 ? L.Format("FileSearch.None", query)
                : total > rows.Count ? L.Format("FileSearch.Limited", total, rows.Count)
                : L.Format("FileSearch.Count", total);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
