using System;
using System.IO;
using Avalonia.Controls;
using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Jammer_desktop.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            VpnConnection.Current.Changed += UpdateConnection;
            AppText.Changed += UpdateConnection;
            GuiLogSink.Updated += UpdateLog;
            StatusText.Text = GuiLogSink.Text;
            UpdateConnection();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            VpnConnection.Current.Changed -= UpdateConnection;
            AppText.Changed -= UpdateConnection;
            GuiLogSink.Updated -= UpdateLog;
        };
        ServerIpTextBox.Text = AppSettings.Current.SelectedServer;
        ServerIpTextBox.TextChanged += (_, _) =>
        {
            AppSettings.Current.SelectedServer = ServerIpTextBox.Text ?? "";
            try { AppSettings.Current.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Could not save server address");
            }
        };
    }

    private async void ConnectButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var connection = VpnConnection.Current;
        if (connection.IsConnecting) return;
        if (connection.IsConnected) { connection.Disconnect(); return; }
        if (!IPAddress.TryParse(ServerIpTextBox.Text?.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        { Log.Warning("{Message}", AppText.Get("InvalidIp")); return; }
        if (OperatingSystem.IsWindows() && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        { Log.Warning("{Message}", AppText.Get("AdminRequired")); return; }
        ConnectButton.IsEnabled = false;
        try { await Task.Run(() => connection.ConnectAsync(address.ToString())); }
        catch (Exception ex)
        {
            Log.Error(ex, "VPN connection failed");
        }
    }

    private void UpdateLog()
    {
        bool follow = LogScroll.Extent.Height - LogScroll.Viewport.Height - LogScroll.Offset.Y < 24;
        StatusText.Text = GuiLogSink.Text;
        if (follow) Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void UpdateConnection() => Dispatcher.UIThread.Post(() =>
    {
        var connection = VpnConnection.Current;
        ConnectButton.IsEnabled = !connection.IsConnecting;
        ServerIpTextBox.IsEnabled = !connection.IsConnecting && !connection.IsConnected;
        ConnectButton.Content = AppText.Get(connection.IsConnecting ? "Connecting" : connection.IsConnected ? "Disconnect" : "Connect");
        ConnectButton.Background = new SolidColorBrush(connection.IsConnected ? Colors.Green : Color.Parse("#C9444A"));
    });
}
