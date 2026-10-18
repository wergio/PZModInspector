using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;
using PZModInspector.App.Services;
using PZModInspector.Core.Environment;
using PZModInspector.Core.Localization;
using PZModInspector.Core.Model;
using PZModInspector.Core.Settings;

namespace PZModInspector.App.Views;

public partial class SettingsWindow : Window
{
    private sealed record LanguageOption(string Code, string Title);

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        var L = Loc.Instance;
        // What automatic detection finds, ignoring the current overrides (cheap: no mod scan).
        var detected = EnvironmentDetector.Detect();

        LanguageBox.ItemsSource = new[]
        {
            new LanguageOption(Loc.Auto, L["Language.Auto"]),
            new LanguageOption("it", "Italiano"),
            new LanguageOption("en", "English"),
        };
        LanguageBox.SelectedValue = settings.Language;
        if (LanguageBox.SelectedIndex < 0)
            LanguageBox.SelectedIndex = 0;

        SteamBox.Text = settings.SteamDirectory ?? "";
        ZomboidBox.Text = settings.ZomboidDirectory ?? "";
        ApiKeyBox.Password = settings.SteamApiKey ?? "";
        DiffBox.Text = settings.DiffToolPath ?? "";
        _detectedDiff = DiffTool.DetectWinMerge();
        if (_detectedDiff is not null && !string.Equals(_detectedDiff, settings.DiffToolPath, StringComparison.OrdinalIgnoreCase))
        {
            DiffDetectedRun.Text = L.Format("Settings.Diff.Detected", _detectedDiff) + " ";
            DiffDetectedText.Visibility = Visibility.Visible;
        }

        SteamDetected.Text = L.Format("Settings.Detected", detected.SteamRoot ?? L["Env.NotFound"]);
        ZomboidDetected.Text = L.Format("Settings.Detected", detected.ZomboidDirectory);
    }

    private readonly string? _detectedDiff;

    public AppSettings Result { get; }

    private void BrowseSteam_Click(object sender, RoutedEventArgs e) => Browse(SteamBox);

    private void BrowseDiff_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Loc.Instance["Settings.Diff.Filter"] };
        var current = string.IsNullOrWhiteSpace(DiffBox.Text) ? _detectedDiff : DiffBox.Text.Trim();
        if (System.IO.Path.GetDirectoryName(current) is { } folder && System.IO.Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true)
            DiffBox.Text = dialog.FileName;
    }

    private void UseDetectedDiff_Click(object sender, RoutedEventArgs e)
    {
        DiffBox.Text = _detectedDiff ?? "";
        DiffDetectedText.Visibility = Visibility.Collapsed;
    }

    private void BrowseZomboid_Click(object sender, RoutedEventArgs e) => Browse(ZomboidBox);

    private void Browse(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Multiselect = false };
        if (!string.IsNullOrWhiteSpace(target.Text) && System.IO.Directory.Exists(target.Text))
            dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password.Trim();
        if (apiKey.Length > 0 && !AppSettings.IsValidApiKey(apiKey))
        {
            ShowError("Settings.ApiKey.Invalid");
            return;
        }
        var diff = Blank(DiffBox.Text);
        if (diff is not null && !(System.IO.File.Exists(diff) && diff.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("Settings.Diff.Invalid");
            return;
        }

        Result.Language = LanguageBox.SelectedValue as string ?? Loc.Auto;
        Result.SteamDirectory = Blank(SteamBox.Text);
        Result.ZomboidDirectory = Blank(ZomboidBox.Text);
        Result.SteamApiKey = Blank(apiKey);
        Result.DiffToolPath = diff;
        DialogResult = true;
    }

    private void ShowError(string key)
    {
        ErrorText.Text = Loc.Instance[key];
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
        e.Handled = true;
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
