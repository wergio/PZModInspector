using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PZModInspector.App.Services;
using PZModInspector.Core.Analysis;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Model;
using PZModInspector.Core.Mods;
using PZModInspector.Core.Settings;
using PZModInspector.Core.Workshop;

namespace PZModInspector.App.ViewModels;

public sealed record SourceItem(ModListSource Source)
{
    public string Title => Text.SourceTitle(Source);

    public string Subtitle => Source.Kind == ModListSourceKind.DefaultMods
        ? Loc.Instance["Source.DefaultHint"]
        : Loc.Instance.Format("Source.LastPlayed", Text.Date(Source.LastPlayedUtc)) + (Source.IsLatestSave ? " · " + Loc.Instance["Source.Latest"] : "");
}

/// <summary>The choices of the filter on the overwritten files, in the order of the drop-down.</summary>
public enum ConflictKind
{
    All,
    Lua,
    Text,
    Binary,
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SteamWorkshopClient _client = new();
    private WorkshopService _workshop;
    private Inspector? _inspector;
    private InspectionResult? _result;
    private FileIndex? _files;
    private int _analysisVersion;
    private bool _workshopBusy;
    /// <summary>Set by the settings when a new Web API key is saved; the next Workshop refresh is forced.</summary>
    private bool _newApiKey;
    private bool _rebuildingTexts;
    private bool _restoringSelection;
    private readonly CancellationTokenSource _shutdown = new();

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _workshop = new WorkshopService(_client, AppSettings.WorkshopCachePath);

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is ModRowViewModel row && RowFilter(row);
        IssuesView = CollectionViewSource.GetDefaultView(IssueRows);
        IssuesView.Filter = o => o is IssueRowViewModel row && IssueFilter(row);
        IssuesView.SortDescriptions.Add(new SortDescription(nameof(IssueRowViewModel.Rank), ListSortDirection.Descending));
        ConflictsView = CollectionViewSource.GetDefaultView(ConflictRows);
        ConflictsView.Filter = o => o is ConflictRowViewModel row && MatchesConflictFilter(row);

        // The other orders come from the column headers (MainWindow.ModsGrid_Sorting).
        SortRows(nameof(ModRowViewModel.LoadSortKey), ListSortDirection.Ascending);

        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    public ObservableCollection<IssueRowViewModel> IssueRows { get; } = [];
    public ICollectionView IssuesView { get; }
    public ObservableCollection<ConflictRowViewModel> ConflictRows { get; } = [];
    public ICollectionView ConflictsView { get; }
    public ObservableCollection<InfoRow> EnvironmentRows { get; } = [];
    public ObservableCollection<IgnoredFolderRow> IgnoredFolders { get; } = [];
    public LogViewModel Log { get; } = new();
    public FileSearchViewModel FileSearch { get; } = new();

    [ObservableProperty] private SourceItem? _selectedSource;
    [ObservableProperty] private ModRowViewModel? _selectedRow;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _onlyProblems;
    [ObservableProperty] private bool _showErrors = true;
    [ObservableProperty] private bool _showWarnings = true;
    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private string _conflictFilter = "";
    /// <summary>Which overwritten files to show, a <see cref="ConflictKind"/> as the index of the drop-down.</summary>
    [ObservableProperty] private int _conflictKindIndex;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _workshopStatus = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _problemsHeader = "";
    [ObservableProperty] private string _conflictsHeader = "";
    [ObservableProperty] private string _ignoredHeader = "";
    [ObservableProperty] private bool _isBusy;
    /// <summary>The details panel on the right: closed at start and with its X, open as soon as the user selects a mod.</summary>
    [ObservableProperty] private bool _detailsVisible;
    /// <summary>The tab shown at the bottom.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>Position of "Find files" among the bottom tabs (MainWindow.xaml).</summary>
    private const int FileSearchTab = 2;

    /// <summary>Columns of the mod list as the user left them: the window reads and writes them, <see cref="Shutdown"/> saves them.</summary>
    public List<ColumnLayout>? ModColumns
    {
        get => _settings.ModColumns;
        set => _settings.ModColumns = value;
    }

    /// <summary>Column widths of the lists in the bottom tabs, like <see cref="ModColumns"/>.</summary>
    public Dictionary<string, Dictionary<string, double>>? ColumnWidths
    {
        get => _settings.ColumnWidths;
        set => _settings.ColumnWidths = value;
    }

    /// <summary>A compare program is set in the settings: the lists show their Diff button.</summary>
    public bool DiffAvailable => !string.IsNullOrWhiteSpace(_settings.DiffToolPath);

    /// <summary>
    /// Without a Web API key the "Required items" are missing: the user is told at every start, once the game is known
    /// to be there, and OK opens the settings.
    /// </summary>
    public async Task InitializeAsync()
    {
        await ReloadAsync();
        if (_inspector is null || _settings.SteamApiKey is not null)
            return;
        var answer = MessageBox.Show(Application.Current.MainWindow, Loc.Instance["Startup.NoApiKey"], Loc.Instance["Startup.NoApiKey.Title"],
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
            OpenSettings();
    }

    public void Shutdown()
    {
        _shutdown.Cancel();
        if (SelectedSource is { } s)
            _settings.LastSource = s.Source.Key;
        _settings.Save();
        _workshop.Save();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusText = Loc.Instance["Status.Scanning"];
        Log.Invalidate();
        try
        {
            var overrides = _settings.ToOverrides();
            var workshop = _workshop;
            _inspector = await Task.Run(() => Inspector.Load(overrides, workshop));
        }
        catch (GameCheckException ex)
        {
            // Without the installed game and its version (42 or later) nothing here would be right.
            var message = Text.GameProblem(ex.Environment);
            if (_settings.SteamDirectory is { } steam)
                message += "\n\n" + Loc.Instance.Format("Startup.CustomSteam", steam, AppSettings.SettingsPath);
            MessageBox.Show(Application.Current.MainWindow, message, Loc.Instance["Startup.Title"], MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown(1);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var previous = SelectedSource?.Source.Key ?? _settings.LastSource;
        Sources.Clear();
        foreach (var source in _inspector.Sources)
            Sources.Add(new SourceItem(source));
        BuildEnvironmentRows();

        // Selecting the source triggers the analysis.
        var pick = Sources.FirstOrDefault(s => s.Source.Key == previous)
                   ?? Sources.FirstOrDefault(s => s.Source.IsLatestSave)
                   ?? Sources.FirstOrDefault();
        if (pick is not null && pick == SelectedSource)
            await AnalyzeAndRefreshAsync();
        else
            SelectedSource = pick;
    }

    [RelayCommand]
    private Task RefreshWorkshop() => RefreshWorkshopAsync(force: true);

    [RelayCommand]
    private void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings.Clone()) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true)
            return;

        var changed = window.Result;
        var pathsChanged = changed.SteamDirectory != _settings.SteamDirectory
                           || changed.ZomboidDirectory != _settings.ZomboidDirectory;
        // Compared in clear: DPAPI gives a different ciphertext at every save.
        var keyChanged = changed.SteamApiKey != _settings.SteamApiKey;
        _settings.Language = changed.Language;
        _settings.SteamDirectory = changed.SteamDirectory;
        _settings.ZomboidDirectory = changed.ZomboidDirectory;
        _settings.SteamApiKeyProtected = changed.SteamApiKeyProtected;
        _settings.DiffToolPath = changed.DiffToolPath;
        _settings.Save();
        OnPropertyChanged(nameof(DiffAvailable));

        // A new key: Steam is asked again for every item, whatever the cache holds (also after a reload).
        if (keyChanged && _settings.SteamApiKey is not null)
            _newApiKey = true;
        if (Loc.Instance.Language != changed.Language)
            Loc.Instance.SetLanguage(changed.Language);
        if (pathsChanged)
            _ = ReloadAsync();
        else if (keyChanged)
            _ = RefreshWorkshopAsync(force: false);
    }

    /// <summary>A row of the overwritten files or of the file search: with more than two copies the user picks two.</summary>
    [RelayCommand]
    private void Diff(object? row)
    {
        var (file, sides) = row switch
        {
            ConflictRowViewModel c => (c.File, c.DiffSides),
            FileMatchRowViewModel f => (f.File, f.DiffSides),
            _ => ("", (IReadOnlyList<DiffSide>)[]),
        };
        if (_settings.DiffToolPath is not { } program || sides.Count < 2)
            return;
        var pair = (sides[^2], sides[^1]);
        if (sides.Count > 2)
        {
            var picker = new Views.DiffPickerWindow(file, sides) { Owner = Application.Current.MainWindow };
            if (picker.ShowDialog() != true || picker.Result is not { } chosen)
                return;
            pair = chosen;
        }
        try
        {
            DiffTool.Launch(program, pair.Item1, pair.Item2);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(Application.Current.MainWindow, Loc.Instance.Format("Diff.Error", program, ex.Message),
                Loc.Instance["Startup.Title"], MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    [RelayCommand]
    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    [RelayCommand]
    private void SelectMod(string? id)
    {
        if (id is null)
            return;
        var row = Rows.FirstOrDefault(r => r.Id == id);
        if (row is null)
            return;
        if (!RowsView.Contains(row))
        {
            SearchText = "";
            OnlyProblems = false;
        }
        SelectedRow = row;
        DetailsVisible = true;
    }

    /// <summary>A file in the details of a mod: "Find files" shows every copy of it.</summary>
    [RelayCommand]
    private void FindFile(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return;
        FileSearch.Query = relativePath;
        SelectedTabIndex = FileSearchTab;
    }

    [RelayCommand]
    private void CloseDetails() => DetailsVisible = false;

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    partial void OnSelectedSourceChanged(SourceItem? value)
    {
        if (value is null || _rebuildingTexts)
            return;
        _settings.LastSource = value.Source.Key;
        _ = AnalyzeAndRefreshAsync();
    }

    partial void OnSelectedRowChanged(ModRowViewModel? value)
    {
        value?.EnsureDetails();
        // Not when a new analysis puts the same mod back in the selection.
        if (value is not null && !_restoringSelection)
            DetailsVisible = true;
    }
    partial void OnSearchTextChanged(string value) => RowsView.Refresh();
    partial void OnOnlyProblemsChanged(bool value) => RowsView.Refresh();
    partial void OnShowErrorsChanged(bool value) => IssuesView.Refresh();
    partial void OnShowWarningsChanged(bool value) => IssuesView.Refresh();
    partial void OnShowInfoChanged(bool value) => IssuesView.Refresh();
    partial void OnConflictFilterChanged(string value) => ConflictsView.Refresh();
    partial void OnConflictKindIndexChanged(int value) => ConflictsView.Refresh();

    private async Task AnalyzeAndRefreshAsync()
    {
        await AnalyzeAsync();
        await RefreshWorkshopAsync(force: false);
    }

    private async Task AnalyzeAsync()
    {
        if (_inspector is not { } inspector || SelectedSource is not { } source)
            return;
        var version = ++_analysisVersion;
        IsBusy = true;
        StatusText = Loc.Instance["Status.Analyzing"];
        try
        {
            var (result, files) = await Task.Run(() =>
            {
                var input = inspector.CreateInput(source.Source);
                var plan = ModAnalyzer.Plan(input);
                // Kept for the file search and the log: the files are scanned once per analysis.
                var index = FileIndex.Build(plan.Loaded, _shutdown.Token);
                return (ModAnalyzer.Analyze(input with { Conflicts = FileConflictAnalyzer.Analyze(index, _shutdown.Token) }, plan), index);
            });
            if (version != _analysisVersion)
                return;
            _result = result;
            _files = files;
            ShowResult(result, files);
            StatusText = Loc.Instance["Status.Ready"];
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (version == _analysisVersion)
                IsBusy = false;
        }
    }

    /// <summary>A key entered while Steam is being asked gets one more round of its own, forced, right after.</summary>
    private async Task RefreshWorkshopAsync(bool force)
    {
        if (_workshopBusy || _inspector is null)
            return;
        _workshopBusy = true;
        try
        {
            while (_inspector is { } inspector)
            {
                force |= _newApiKey;
                _newApiKey = false;
                WorkshopStatus = Loc.Instance["Status.QueryingWorkshop"];
                var ok = await _workshop.RefreshDetailsAsync(inspector.AllWorkshopIds(), _settings.SteamApiKey, force, _shutdown.Token);
                inspector.WorkshopQueryFailed = !ok;
                UpdateWorkshopStatus();
                await AnalyzeAsync();
                if (!_newApiKey)
                    break;
                force = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _workshopBusy = false;
        }
    }

    private void UpdateWorkshopStatus()
    {
        var L = Loc.Instance;
        var last = _workshop.LastDetailsFetch;
        var status = _inspector?.WorkshopQueryFailed == true
            ? L.Format("Status.WorkshopOffline", last is null ? L["Status.Never"] : Text.Date(last))
            : L.Format("Status.WorkshopOk", Text.Date(last));
        if (_workshop.ApiKeyRejected)
            status += " · " + L["Status.ApiKeyRejected"];
        else if (_settings.SteamApiKeyProtected is null)
            status += " · " + L["Status.NoApiKey"];
        WorkshopStatus = status;
    }

    private void ShowResult(InspectionResult result, FileIndex files)
    {
        var inspector = _inspector!;
        var context = new RowContext(result, inspector, files);
        var selectedId = SelectedRow?.Id;

        Rows.Clear();
        foreach (var entry in result.Entries)
            Rows.Add(new ModRowViewModel(entry, context));

        IssueRows.Clear();
        foreach (var issue in result.GeneralIssues)
            IssueRows.Add(new IssueRowViewModel(issue.Severity, null, Loc.Instance["Problems.General"], Text.Issue(issue)));
        foreach (var row in Rows)
        {
            // Overwritten files have their own tab; the details of the mod still list them.
            foreach (var issue in row.Entry.Issues.Where(i => i.Code != IssueCode.FilesOverridden))
                IssueRows.Add(new IssueRowViewModel(issue.Severity, row.Id, row.Name, Text.Issue(issue)));
        }

        ConflictRows.Clear();
        foreach (var conflict in result.Conflicts)
            ConflictRows.Add(new ConflictRowViewModel(conflict, context.NameOf));

        var all = result.AllIssues.ToList();
        var errors = all.Count(i => i.Severity == Severity.Error);
        var warnings = all.Count(i => i.Severity == Severity.Warning);
        SummaryText = Loc.Instance.Format("Summary", result.Entries.Count(e => e.ListIndex is not null), result.Plan.Loaded.Count, errors, warnings);
        // Every row of the tab: errors, warnings and information (without the overwritten files, which have their own tab).
        ProblemsHeader = Loc.Instance.Format("Tabs.Problems", IssueRows.Count);
        ConflictsHeader = Loc.Instance.Format("Tabs.Conflicts", result.Conflicts.Count);
        BuildIgnoredRows();

        _restoringSelection = true;
        try
        {
            SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.Id == selectedId);
        }
        finally
        {
            _restoringSelection = false;
        }

        FileSearch.SetAnalysis(context);
        _ = Log.SetAnalysisAsync(context);
    }

    private void BuildEnvironmentRows()
    {
        EnvironmentRows.Clear();
        if (_inspector is { } inspector)
        {
            foreach (var row in DetailRows.Environment(inspector))
                EnvironmentRows.Add(row);
        }
        BuildIgnoredRows();
    }

    /// <summary>Also after each analysis: a Workshop refresh can bring the titles of the items.</summary>
    private void BuildIgnoredRows()
    {
        IgnoredFolders.Clear();
        if (_inspector is { } inspector)
        {
            foreach (var folder in DetailRows.IgnoredFolders(inspector))
                IgnoredFolders.Add(folder);
        }
        IgnoredHeader = Loc.Instance.Format("Tabs.Ignored", IgnoredFolders.Count);
    }

    /// <summary>Texts are built in the view models, so they are rebuilt; the current analysis is reused as is.</summary>
    private void OnLanguageChanged()
    {
        _rebuildingTexts = true;
        try
        {
            var selected = SelectedSource?.Source.Key;
            var items = Sources.ToList();
            Sources.Clear();
            foreach (var item in items)
                Sources.Add(new SourceItem(item.Source));
            SelectedSource = Sources.FirstOrDefault(s => s.Source.Key == selected);
        }
        finally
        {
            _rebuildingTexts = false;
        }
        BuildEnvironmentRows();
        UpdateWorkshopStatus();
        if (_result is not null && _files is not null)
            ShowResult(_result, _files);
        StatusText = Loc.Instance["Status.Ready"];
    }

    /// <summary>Equal values stay in load order.</summary>
    public void SortRows(string property, ListSortDirection direction)
    {
        using (RowsView.DeferRefresh())
        {
            RowsView.SortDescriptions.Clear();
            RowsView.SortDescriptions.Add(new SortDescription(property, direction));
            if (property != nameof(ModRowViewModel.LoadSortKey))
                RowsView.SortDescriptions.Add(new SortDescription(nameof(ModRowViewModel.LoadSortKey), ListSortDirection.Ascending));
        }
    }

    private bool RowFilter(ModRowViewModel row) =>
        (!OnlyProblems || row.IssueCount > 0 || !row.IsLoaded)
        && (string.IsNullOrWhiteSpace(SearchText) || row.Matches(SearchText.Trim()));

    private bool IssueFilter(IssueRowViewModel row) => row.Severity switch
    {
        Severity.Error => ShowErrors,
        Severity.Warning => ShowWarnings,
        _ => ShowInfo,
    };

    private bool MatchesConflictFilter(ConflictRowViewModel row)
    {
        var wanted = (ConflictKind)ConflictKindIndex switch
        {
            ConflictKind.Lua => row.IsLua,
            ConflictKind.Text => row.IsText,
            ConflictKind.Binary => !row.IsText,
            _ => true,
        };
        if (!wanted)
            return false;
        var filter = ConflictFilter.Trim();
        return filter.Length == 0
               || row.File.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || row.Winner.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
               || row.Overridden.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
    }
}
