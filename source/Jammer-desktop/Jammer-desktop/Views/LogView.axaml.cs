using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Jammer_desktop.ViewModels;
using Avalonia.VisualTree;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog;
using Avalonia.Platform.Storage;

namespace Jammer_desktop.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        LogPath.Text = AppLogging.Folder;
    }

    private void OpenLog_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = Directory.GetFiles(AppLogging.Folder, "log-*.txt").OrderBy(p => p).LastOrDefault();
            if (file is null) { LogStatus.Text = AppText.Get("NoLogs"); return; }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            LogStatus.Text = "";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open log file");
            LogStatus.Text = AppText.Get("OpenLogError");
        }
    }

    private async void ChooseLogFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = AppText.Get("ChooseLogFolder"), AllowMultiple = false });
        string? folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return;
        try
        {
            AppLogging.ChangeFolder(folder);
            LogPath.Text = AppLogging.Folder;
            LogStatus.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error(ex, "Could not change log folder");
            LogStatus.Text = AppText.Get("LogFolderError");
        }
    }

    private void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var settingsView = this.FindAncestorOfType<SettingsView>();
        var settingsViewModel = (SettingsViewModel)settingsView.DataContext;
        
        settingsViewModel.RouteBack();
    }
}
