using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Jammer_desktop.ViewModels;
using Avalonia.VisualTree;
using Jammer.Core;
using Jammer.Deploy;
using System;
using Serilog;

namespace Jammer_desktop.Views;

public partial class SelfHostedView : UserControl
{
    public SelfHostedView()
    {
        InitializeComponent();
    }

    private void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var settingsView = this.FindAncestorOfType<SettingsView>();
        var settingsViewModel = (SettingsViewModel)settingsView.DataContext;
        
        settingsViewModel.RouteBack();
    }

    private async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        int.TryParse(SshPortTextBox.Text, out var port);
        try { await SshService.DeploySelfHosted(IpTextBox.Text, port, LoginTextBox.Text, PasswordTextBox.Text); }
        catch (Exception ex) { Log.Error(ex, "Self-hosted installation failed"); }
    }
}
