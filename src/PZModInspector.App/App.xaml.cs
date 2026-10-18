using System.Windows;
using PZModInspector.App.ViewModels;
using PZModInspector.App.Views;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Settings;

namespace PZModInspector.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = AppSettings.Load();
        Loc.Instance.SetLanguage(settings.Language);
        var viewModel = new MainViewModel(settings);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }
}
