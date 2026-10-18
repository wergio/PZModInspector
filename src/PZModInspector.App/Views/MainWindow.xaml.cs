using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using PZModInspector.App.ViewModels;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Settings;

namespace PZModInspector.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly List<ColumnLayout> _defaultModColumns;
    private GridLength _detailsWidth;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ShowDetails(viewModel.DetailsVisible);
        ShowDiffColumns();
        // Before the grid is loaded DisplayIndex is not set yet: the default order is that of the XAML.
        _defaultModColumns = GetColumnLayout(ModsGrid).Select((c, i) => c with { DisplayIndex = i }).ToList();
        if (viewModel.ColumnWidths is { } widths)
        {
            foreach (var (name, grid) in WidthGrids)
            {
                if (widths.TryGetValue(name, out var saved))
                    ApplyColumnWidths(grid, saved);
            }
        }
        Loaded += async (_, _) =>
        {
            if (viewModel.ModColumns is { } saved)
                ApplyColumnLayout(ModsGrid, saved);
            await viewModel.InitializeAsync();
        };
        Closing += (_, _) =>
        {
            viewModel.ModColumns = GetColumnLayout(ModsGrid);
            viewModel.ColumnWidths = WidthGrids.ToDictionary(g => g.Name, g => GetColumnWidths(g.Grid));
            viewModel.Shutdown();
        };
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewExecuted);
        viewModel.Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.SelectedGroup))
                LogDetailsScroll.ScrollToTop();
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.DetailsVisible))
            ShowDetails(_viewModel.DetailsVisible);
        if (e.PropertyName == nameof(MainViewModel.DiffAvailable))
            ShowDiffColumns();
        if (e.PropertyName != nameof(MainViewModel.SelectedRow) || _viewModel.SelectedRow is not { } row)
            return;
        ModsGrid.ScrollIntoView(row);
        DetailsScroll.ScrollToTop();
    }

    /// <summary>The Diff columns only with a compare program set; a column is not in the visual tree, so not by binding.</summary>
    private void ShowDiffColumns()
    {
        var visibility = _viewModel.DiffAvailable ? Visibility.Visible : Visibility.Collapsed;
        ConflictsDiffColumn.Visibility = FilesDiffColumn.Visibility = visibility;
    }

    /// <summary>Closed, the list takes the whole width; open again, the panel gets back the width it had.</summary>
    private void ShowDetails(bool visible)
    {
        // Visibility, not IsVisible: that is false until the window is shown.
        if (visible == (DetailsCard.Visibility == Visibility.Visible))
            return;
        if (visible)
        {
            DetailsColumn.MinWidth = 320;
            DetailsColumn.Width = _detailsWidth;
            DetailsSplitterColumn.Width = new GridLength(8);
        }
        else
        {
            _detailsWidth = DetailsColumn.Width;
            DetailsColumn.MinWidth = 0;
            DetailsColumn.Width = new GridLength(0);
            DetailsSplitterColumn.Width = new GridLength(0);
        }
        DetailsCard.Visibility = DetailsSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A click on the mod already selected does not change the selection, but it opens the details too.</summary>
    private void ModsGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ModsGrid, e.OriginalSource as DependencyObject) is DataGridRow)
            _viewModel.DetailsVisible = true;
    }

    /// <summary>The theme's clear button shows only while the box has focus: ours is there whenever there is text.</summary>
    private void ClearableBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        box.ApplyTemplate();
        // A local value wins over the template triggers that show it.
        if (box.Template?.FindName("DeleteButton", box) is UIElement themeButton)
            themeButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>One check per column, in the order on screen; the last visible column cannot be hidden.</summary>
    private void ModColumnsMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        var items = new List<MenuItem>();
        void UpdateEnabled()
        {
            var lastVisible = items.Count(i => i.IsChecked) == 1;
            foreach (var i in items)
                i.IsEnabled = !(lastVisible && i.IsChecked);
        }

        foreach (var column in ModsGrid.Columns.OrderBy(c => c.DisplayIndex))
        {
            var item = new MenuItem
            {
                // The dot column has no header text.
                Header = column.Header is string { Length: > 0 } header ? header : Loc.Instance["Column.Status"],
                ToolTip = ColumnInfo.GetTip(column),
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                UpdateEnabled();
            };
            items.Add(item);
            menu.Items.Add(item);
        }
        UpdateEnabled();

        var reset = new MenuItem { Header = Loc.Instance["Columns.Reset"] };
        reset.Click += (_, _) => ApplyColumnLayout(ModsGrid, _defaultModColumns);
        menu.Items.Add(new Separator());
        menu.Items.Add(reset);
    }

    /// <summary>Like the default sorting, but equal values stay in load order (<see cref="MainViewModel.SortRows"/>).</summary>
    private void ModsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column;
        // Problems and recent updates first.
        var first = ColumnInfo.GetKey(column) is "Status" or "Updated" ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var direction = column.SortDirection switch
        {
            ListSortDirection.Ascending => ListSortDirection.Descending,
            ListSortDirection.Descending => ListSortDirection.Ascending,
            _ => first,
        };
        _viewModel.SortRows(column.SortMemberPath, direction);
        // After the sort: the grid marks every column of the sort, load order included.
        foreach (var c in ModsGrid.Columns)
            c.SortDirection = null;
        column.SortDirection = direction;
    }

    /// <summary>The lists of the bottom tabs: only their widths are kept, by list and column key.</summary>
    private (string Name, DataGrid Grid)[] WidthGrids =>
        [("Ignored", IgnoredGrid), ("Problems", IssuesGrid), ("Conflicts", ConflictsGrid), ("FileSearch", FilesGrid), ("Log", LogGrid)];

    private static Dictionary<string, double> GetColumnWidths(DataGrid grid) =>
        grid.Columns.Where(c => ColumnInfo.GetKey(c) is not null)
            .ToDictionary(c => ColumnInfo.GetKey(c)!, c => c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth, StringComparer.Ordinal);

    private static void ApplyColumnWidths(DataGrid grid, Dictionary<string, double> widths)
    {
        foreach (var column in grid.Columns)
        {
            if (ColumnInfo.GetKey(column) is { } key && widths.TryGetValue(key, out var width) && double.IsFinite(width) && width > 0)
                column.Width = new DataGridLength(Math.Max(width, column.MinWidth));
        }
    }

    private static List<ColumnLayout> GetColumnLayout(DataGrid grid) =>
        grid.Columns.Select(c => new ColumnLayout(ColumnInfo.GetKey(c), c.Width.IsAbsolute ? c.Width.Value : c.ActualWidth,
            c.Visibility == Visibility.Visible, c.DisplayIndex)).ToList();

    /// <summary>
    /// Saved columns get their width, visibility and place; columns that are not in the layout (added by a later
    /// version) keep their defaults and go right after the column that precedes them in the XAML.
    /// </summary>
    private static void ApplyColumnLayout(DataGrid grid, IReadOnlyList<ColumnLayout> layout)
    {
        var saved = new Dictionary<string, ColumnLayout>(StringComparer.Ordinal);
        foreach (var l in layout)
        {
            if (l.Key is { } key && double.IsFinite(l.Width) && l.Width > 0)
                saved.TryAdd(key, l);
        }
        var columns = grid.Columns;
        var order = columns.Where(c => ColumnInfo.GetKey(c) is { } k && saved.ContainsKey(k))
            .OrderBy(c => saved[ColumnInfo.GetKey(c)!].DisplayIndex).ToList();
        if (order.Count == 0)
            return;

        foreach (var column in order)
        {
            var l = saved[ColumnInfo.GetKey(column)!];
            column.Width = new DataGridLength(Math.Max(l.Width, column.MinWidth));
            column.Visibility = l.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (columns.All(c => c.Visibility != Visibility.Visible))
            columns[0].Visibility = Visibility.Visible;

        for (var i = 0; i < columns.Count; i++)
        {
            if (!order.Contains(columns[i]))
                order.Insert(i == 0 ? 0 : order.IndexOf(columns[i - 1]) + 1, columns[i]);
        }
        // In increasing order every move leaves the columns already placed where they are.
        for (var i = 0; i < order.Count; i++)
            order[i].DisplayIndex = i;
    }

    private void IssuesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClickedCell(e) is (IssueRowViewModel { ModId: { } id }, _))
            _viewModel.SelectModCommand.Execute(id);
    }

    /// <summary>On the "replaces" column the mod replaced, elsewhere the one whose copy the game uses.</summary>
    private void ConflictsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClickedCell(e) is (ConflictRowViewModel row, var column))
            _viewModel.SelectModCommand.Execute(column is not null && ColumnInfo.GetKey(column) == "Overridden" ? row.OverriddenId : row.WinnerId);
    }

    private void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClickedCell(e) is (FileMatchRowViewModel row, var column))
            _viewModel.SelectModCommand.Execute(column is not null && ColumnInfo.GetKey(column) == "Overridden" && row.OverriddenId is { } id ? id : row.WinnerId);
    }

    /// <summary>The first linked mod that is in the grid: the details list all of them.</summary>
    private void LogGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClickedCell(e) is (LogGroupRowViewModel row, _) && row.Mods.FirstOrDefault(m => m.SelectId is not null)?.SelectId is { } id)
            _viewModel.SelectModCommand.Execute(id);
    }

    /// <summary>The row and column of a double click; null off the rows (header, empty space, scroll bar).</summary>
    private static (object Item, DataGridColumn? Column)? ClickedCell(MouseButtonEventArgs e)
    {
        DataGridColumn? column = null;
        for (var d = e.OriginalSource as DependencyObject; d is not null and not DataGrid; d = ParentOf(d))
        {
            if (d is DataGridCell cell)
                column ??= cell.Column;
            else if (d is DataGridRow row)
                return (row.Item, column);
        }
        return null;
    }

    /// <summary>Ctrl+C in a list copies the cell clicked last (the current cell), not the whole row.</summary>
    private static void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy || !InDataGrid(e.OriginalSource as DependencyObject, out var grid)
            || grid.CurrentCell is not { Column: { } column, Item: { } item })
            return;
        e.Handled = true;
        var text = column.OnCopyingCellClipboardContent(item)?.ToString() ?? "";
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // The clipboard is held by another program: like a failed Ctrl+C anywhere else.
        }
    }

    private static bool InDataGrid(DependencyObject? element, out DataGrid grid)
    {
        for (var d = element; d is not null; d = ParentOf(d))
        {
            if (d is DataGrid found)
            {
                grid = found;
                return true;
            }
        }
        grid = null!;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
