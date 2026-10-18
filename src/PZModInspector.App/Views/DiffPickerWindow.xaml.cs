using System.Windows;
using System.Windows.Controls;
using PZModInspector.App.Services;

namespace PZModInspector.App.Views;

/// <summary>
/// More than two copies of a file: the user ticks the two to compare. At the start the copy the game uses (the last) and
/// the one just before it, which it overwrites.
/// </summary>
public partial class DiffPickerWindow : Window
{
    private readonly List<(CheckBox Box, DiffSide Side)> _items = [];

    public DiffPickerWindow(string file, IReadOnlyList<DiffSide> sides)
    {
        InitializeComponent();
        FileText.Text = file;
        for (var i = 0; i < sides.Count; i++)
        {
            var box = new CheckBox
            {
                Content = new TextBlock { Text = sides[i].Label, TextWrapping = TextWrapping.Wrap },
                ToolTip = sides[i].Path,
                IsChecked = i >= sides.Count - 2,
                Margin = new Thickness(0, 0, 0, 4),
            };
            box.Click += (_, _) => UpdateOk();
            _items.Add((box, sides[i]));
            Copies.Children.Add(box);
        }
        UpdateOk();
    }

    /// <summary>The two copies chosen, in load order: the older on the left.</summary>
    public (DiffSide Left, DiffSide Right)? Result { get; private set; }

    private List<DiffSide> Checked() => _items.Where(i => i.Box.IsChecked == true).Select(i => i.Side).ToList();

    private void UpdateOk() => OkButton.IsEnabled = Checked().Count == 2;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Checked();
        if (chosen.Count != 2)
            return;
        Result = (chosen[0], chosen[1]);
        DialogResult = true;
    }
}
