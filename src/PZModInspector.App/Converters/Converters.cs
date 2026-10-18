using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PZModInspector.App.Converters;

/// <summary>Visible when the value is not null, not an empty string and not an empty collection. "Invert" flips it.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            ICollection c => c.Count > 0,
            bool b => b,
            int i => i > 0,
            _ => true,
        };
        if (parameter is "Invert")
            has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
