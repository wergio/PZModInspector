using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Logs;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;

namespace PZModInspector.App.ViewModels;

/// <summary>A mod linked to a log entry; <paramref name="SelectId"/> is null when the mod is not a row of the grid.</summary>
public sealed record LogModRow(string Text, string? SelectId);

/// <summary>A copy of a file in a loaded mod; the one marked <paramref name="IsUsed"/> is the copy the game uses.</summary>
public sealed record FileCopyRow(string Text, string Path, string Note, bool IsUsed, string? SelectId)
{
    public static List<FileCopyRow> From(string relativePath, IReadOnlyList<FileOwner> owners, RowContext context)
    {
        var uses = context.Files.CopyUses(relativePath, owners);
        return owners.Select((o, i) => new FileCopyRow($"#{o.LoadIndex} {context.Label(o.ModId)}", o.AbsolutePath,
            NoteOf(uses[i]), uses[i] is CopyUse.Used or CopyUse.Merged, context.SelectId(o.ModId))).ToList();
    }

    private static string NoteOf(CopyUse use) => Loc.Instance[use switch
    {
        CopyUse.Used => "Files.Used",
        CopyUse.ReplacedBySame => "Files.ReplacedSame",
        CopyUse.Merged => "Files.Merged",
        CopyUse.Unused => "Files.Unused",
        _ => "Files.Replaced",
    }];
}

/// <summary>A Lua file of a stack trace with its copies in load order: the last one ran, the others are the ones it replaces.</summary>
public sealed record LogFileRow(string Path, string? InGameText, IReadOnlyList<FileCopyRow> Copies);

/// <summary>A group of log entries that differ only by numbers, linked to the mods; the details show when it is selected.</summary>
public sealed class LogGroupRowViewModel
{
    public LogGroupRowViewModel(LogFinding finding, RowContext context)
    {
        var L = Loc.Instance;
        var group = finding.Group;
        var entry = group.First;
        Severity = entry.Severity == LogSeverity.Error ? Core.Model.Severity.Error : Core.Model.Severity.Warning;
        SeverityText = Text.Severity(Severity);
        Count = group.Count;
        FirstLine = entry.LineNumber;
        // Plain numbers, as an editor shows them.
        var first = entry.LineNumber.ToString(CultureInfo.InvariantCulture);
        var last = group.LastLineNumber.ToString(CultureInfo.InvariantCulture);
        LinesText = group.Count == 1 ? first : $"{first}–{last}";
        var lines = group.Count == 1 ? L.Format("Log.Line", first) : L.Format("Log.Lines", first, last);
        HeaderText = $"{SeverityText} ×{Count} · {lines} · {entry.Category} · f:{entry.Frame.ToString(CultureInfo.InvariantCulture)}";
        Message = entry.Location is null ? entry.Message : $"{entry.Location} > {entry.Message}";
        Exception = entry.Exception;
        LuaFrames = entry.LuaFrames.Select(f => f.ToString()).ToList();
        ModIds = finding.ModIds;
        ModsText = string.Join(", ", finding.ModIds.Select(id => context.NameOf(id).Trim()));
        Mods = finding.ModIds.Select(id =>
        {
            var index = context.Result.Plan.Find(id)?.LoadIndex;
            var where = index is { } i ? $"#{i}" : L["Status.NotLoaded"];
            return new LogModRow($"{context.Label(id)} ({where})", context.SelectId(id));
        }).ToList();
        Files = finding.Files
            .Select(f => new LogFileRow(f.RelativePath, context.InGame(f.RelativePath) ? L["Files.InGame"] : null, FileCopyRow.From(f.RelativePath, f.Owners, context)))
            .ToList();
        RequireText = finding.Require is { } require ? Explain(require, context) : null;
    }

    public Severity Severity { get; }
    public string SeverityText { get; }
    public int Rank => (int)Severity;
    public int Count { get; }
    public int FirstLine { get; }
    public string LinesText { get; }
    public string HeaderText { get; }
    public string Message { get; }
    public string? Exception { get; }
    public IReadOnlyList<string> LuaFrames { get; }
    public IReadOnlyList<string> ModIds { get; }
    public string ModsText { get; }
    public IReadOnlyList<LogModRow> Mods { get; }
    public IReadOnlyList<LogFileRow> Files { get; }
    public string? RequireText { get; }
    public bool IsModRelated => ModIds.Count > 0;

    /// <summary>Why require("module") failed: the folder was not loaded yet, the path is wrong, or the file is nowhere.</summary>
    private static string Explain(LogRequireMatch require, RowContext context)
    {
        var L = Loc.Instance;
        if (require.Found.Count > 0)
            return L.Format("Log.Require.Found", require.Module, Where(require.Found, context));
        return require.SameName.Count > 0
            ? L.Format("Log.Require.SameName", require.Module, Where(require.SameName, context))
            : L.Format("Log.Require.Missing", require.Module);
    }

    /// <summary>"path (base game, Mod [id] #n); path (...)".</summary>
    private static string Where(IEnumerable<LuaFileLocation> locations, RowContext context) =>
        string.Join("; ", locations.Select(l =>
        {
            IEnumerable<string> who = l.InGame ? [Loc.Instance["Log.BaseGame"]] : [];
            return $"{l.RelativePath} ({string.Join(", ", who.Concat(l.Owners.Select(o => $"{context.Label(o.ModId)} #{o.LoadIndex}")))})";
        }));
}

/// <summary>
/// The Console tab: the errors and warnings of console.txt (or of another log) grouped and linked to the mods of the
/// current analysis. The file is read again only when asked or after "Reload"; a new analysis only links the groups again.
/// </summary>
public sealed partial class LogViewModel : ObservableObject
{
    private RowContext? _context;
    private LogFile? _file;
    private string? _customPath;
    private int _version;

    public LogViewModel()
    {
        GroupsView = CollectionViewSource.GetDefaultView(Groups);
        GroupsView.Filter = o => o is LogGroupRowViewModel row && (!OnlyErrors || row.Severity == Severity.Error) && (!OnlyMods || row.IsModRelated);
        _header = Loc.Instance["Tabs.Console.NoCount"];
    }

    public ObservableCollection<LogGroupRowViewModel> Groups { get; } = [];
    public ICollectionView GroupsView { get; }

    [ObservableProperty] private LogGroupRowViewModel? _selectedGroup;
    [ObservableProperty] private bool _onlyErrors;
    [ObservableProperty] private bool _onlyMods;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private bool _isCustomPath;
    [ObservableProperty] private string _header;
    [ObservableProperty] private string _summaryText = "";
    /// <summary>Shown in place of the grid: file missing or unreadable, no entries, nothing left by the filters.</summary>
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isBusy;

    private string DefaultPath => _context is null ? "" : Path.Combine(_context.Inspector.Environment.ZomboidDirectory, "console.txt");

    /// <summary>After each analysis (and language change): links the groups to the new load order, reading the file the first time.</summary>
    public Task SetAnalysisAsync(RowContext context)
    {
        _context = context;
        var path = _customPath ?? DefaultPath;
        return LoadAsync(path, read: _file is null || _file.Path != path);
    }

    /// <summary>The next analysis reads the file again: "Reload" in the toolbar rereads everything.</summary>
    public void Invalidate() => _file = null;

    [RelayCommand]
    private Task Reload() => _context is null ? Task.CompletedTask : LoadAsync(_customPath ?? DefaultPath, read: true);

    [RelayCommand]
    private Task UseConsoleTxt()
    {
        _customPath = null;
        return _context is null ? Task.CompletedTask : LoadAsync(DefaultPath, read: true);
    }

    [RelayCommand]
    private Task Choose()
    {
        if (_context is null)
            return Task.CompletedTask;
        var zomboid = _context.Inspector.Environment.ZomboidDirectory;
        var logs = Path.Combine(zomboid, "Logs");
        var dialog = new OpenFileDialog
        {
            Filter = Loc.Instance["Log.FileFilter"],
            InitialDirectory = _customPath is not null ? Path.GetDirectoryName(_customPath) : Directory.Exists(logs) ? logs : zomboid,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
            return Task.CompletedTask;
        var path = Path.GetFullPath(dialog.FileName);
        _customPath = path.Equals(DefaultPath, StringComparison.OrdinalIgnoreCase) ? null : path;
        return LoadAsync(path, read: true);
    }

    partial void OnOnlyErrorsChanged(bool value) => Refresh();
    partial void OnOnlyModsChanged(bool value) => Refresh();

    private async Task LoadAsync(string path, bool read)
    {
        var context = _context!;
        var version = ++_version;
        FilePath = path;
        IsCustomPath = _customPath is not null;
        IsBusy = true;
        try
        {
            var cached = read ? null : _file;
            var (file, rows) = await Task.Run(() =>
            {
                var file = cached ?? LogFile.Read(path);
                var findings = LogAnalyzer.Analyze(file.Groups, context.Result.Plan, context.Inspector.Catalog, context.Files,
                    context.Inspector.Environment.GameDirectory);
                return (file, findings.Select(f => new LogGroupRowViewModel(f, context)).ToList());
            });
            if (version != _version)
                return;
            _file = file;
            Show(file, rows, keepSelection: cached is not null);
        }
        catch (Exception ex)
        {
            // A log the parser does not expect must not close the program: the tab says what went wrong.
            if (version != _version)
                return;
            _file = new LogFile(path, [], ReadError: ex.Message);
            Show(_file, [], keepSelection: false);
        }
        finally
        {
            if (version == _version)
                IsBusy = false;
        }
    }

    private void Show(LogFile file, List<LogGroupRowViewModel> rows, bool keepSelection)
    {
        var L = Loc.Instance;
        var selected = keepSelection ? SelectedGroup?.FirstLine : null;
        Groups.Clear();
        foreach (var row in rows)
            Groups.Add(row);
        var linked = rows.Count(r => r.IsModRelated);
        Header = file.Missing || file.ReadError is not null ? L["Tabs.Console.NoCount"] : L.Format("Tabs.Console", rows.Count);
        SummaryText = file.ModifiedUtc is { } modified
            ? L.Format("Log.Summary", Text.Date(modified), file.Errors, file.Warnings, rows.Count, linked)
            : "";
        SelectedGroup = selected is null ? null : Groups.FirstOrDefault(g => g.FirstLine == selected);
        Refresh();
    }

    private void Refresh()
    {
        GroupsView.Refresh();
        var L = Loc.Instance;
        EmptyText = _file switch
        {
            null => "",
            { Missing: true } => L.Format("Log.NotFound", _file.Path),
            { ReadError: { } error } => L.Format("Log.ReadError", error),
            _ when Groups.Count == 0 => L["Log.Empty"],
            _ when GroupsView.IsEmpty => L["Log.NoneFiltered"],
            _ => "",
        };
    }

    /// <summary>What was read from the log: kept so a new analysis does not read it again.</summary>
    private sealed record LogFile(string Path, IReadOnlyList<LogGroup> Groups, DateTimeOffset? ModifiedUtc = null, int Errors = 0,
        int Warnings = 0, bool Missing = false, string? ReadError = null)
    {
        public static LogFile Read(string path)
        {
            if (!File.Exists(path))
                return new LogFile(path, [], Missing: true);
            try
            {
                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                var entries = ConsoleLogParser.ReadFile(path);
                return new LogFile(path, ConsoleLogParser.Group(entries), modified, entries.Count(e => e.Severity == LogSeverity.Error),
                    entries.Count(e => e.Severity == LogSeverity.Warning));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new LogFile(path, [], ReadError: ex.Message);
            }
        }
    }
}
