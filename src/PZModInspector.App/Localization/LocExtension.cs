using System.Windows.Data;
using System.Windows.Markup;
using PZModInspector.Core.Localization;

namespace PZModInspector.App.Localization;

/// <summary>XAML: Text="{l:Loc Toolbar.Reload}". The binding follows language changes.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
