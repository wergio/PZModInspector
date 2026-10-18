using System.Windows;
using System.Windows.Controls;

namespace PZModInspector.App.Views;

/// <summary>Attached to a DataGridColumn: a stable key for the saved layout and the description shown on its header.</summary>
public static class ColumnInfo
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ColumnInfo));

    public static readonly DependencyProperty TipProperty =
        DependencyProperty.RegisterAttached("Tip", typeof(string), typeof(ColumnInfo));

    public static string? GetKey(DataGridColumn column) => (string?)column.GetValue(KeyProperty);
    public static void SetKey(DataGridColumn column, string? value) => column.SetValue(KeyProperty, value);

    public static string? GetTip(DataGridColumn column) => (string?)column.GetValue(TipProperty);
    public static void SetTip(DataGridColumn column, string? value) => column.SetValue(TipProperty, value);
}
