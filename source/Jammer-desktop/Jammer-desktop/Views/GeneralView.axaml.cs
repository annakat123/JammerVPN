using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Jammer_desktop.ViewModels;
using Avalonia.VisualTree;
using System;
using System.IO;
using Serilog;

namespace Jammer_desktop.Views;

public partial class GeneralView : UserControl
{
    public GeneralView()
    {
        InitializeComponent();
        LoggingToggle.IsChecked = AppSettings.Current.LoggingEnabled;
        LanguageCombo.SelectedIndex = AppSettings.Current.Language == "en" ? 1 : 0;
        LoggingToggle.IsCheckedChanged += (_, _) =>
        {
            AppSettings.Current.LoggingEnabled = LoggingToggle.IsChecked == true;
            AppLogging.SetDetailed(AppSettings.Current.LoggingEnabled);
            Log.Information("Logging mode: {Mode}", AppLogging.Detailed ? "detailed" : "basic");
            SaveSettings();
        };
        LanguageCombo.SelectionChanged += (_, _) =>
        {
            AppSettings.Current.Language = LanguageCombo.SelectedIndex == 1 ? "en" : "ru";
            AppText.Apply(AppSettings.Current.Language);
            SaveSettings();
        };
    }

    private void SaveSettings()
    {
        try { AppSettings.Current.Save(); SettingsStatus.Text = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Could not save preferences");
            SettingsStatus.Text = AppText.Get("SettingsError");
        }
    }

    private void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var settingsView = this.FindAncestorOfType<SettingsView>();
        var settingsViewModel = (SettingsViewModel)settingsView.DataContext;
        
        settingsViewModel.RouteBack();
    }
}
