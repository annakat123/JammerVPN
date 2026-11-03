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
        AttachedToVisualTree += (_, _) => { GuiLogSink.InstallUpdated += UpdateLog; UpdateLog(); };
        DetachedFromVisualTree += (_, _) => GuiLogSink.InstallUpdated -= UpdateLog;
    }

    private void UpdateLog()
    {
        if (GuiLogSink.InstallText.Length == 0) return;
        bool follow = InstallScroll.Extent.Height - InstallScroll.Viewport.Height - InstallScroll.Offset.Y < 24;
        InstallStatus.Text = GuiLogSink.InstallText;
        if (follow) Avalonia.Threading.Dispatcher.UIThread.Post(() => InstallScroll.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Loaded);
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
        var button = (Button)sender!;
        button.IsEnabled = false;
        try { await SshService.DeploySelfHosted(IpTextBox.Text ?? "", port, LoginTextBox.Text ?? "", PasswordTextBox.Text ?? ""); }
        catch (Exception ex) { Log.ForContext("SourceContext", "SshService").Error(ex, "Self-hosted installation failed"); }
        finally { button.IsEnabled = true; }
    }
}
