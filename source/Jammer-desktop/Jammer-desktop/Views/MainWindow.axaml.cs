using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Jammer_desktop.ViewModels;

namespace Jammer_desktop.Views;

public partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        HomeIcon.Foreground = new SolidColorBrush(Color.Parse("#C9444A"));
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var connection = VpnConnection.Current;
        if (_closing || (!connection.IsConnected && !connection.IsConnecting)) return;
        e.Cancel = true;
        _closing = true;
        connection.Disconnect();
        try { await connection.Completion; }
        catch (System.Exception ex) { Serilog.Log.Error(ex, "Error while closing VPN"); }
        Close();
    }
    

    private void HomeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        HomeIcon.Foreground = new SolidColorBrush(Color.Parse("#C9444A"));
        SettingsIcon.Foreground = new SolidColorBrush(Color.Parse("#6B6F80"));
        
        var mainViewModel = (MainViewModel)DataContext;
        mainViewModel.RouteToHomePage();
    }
    
    
    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SettingsIcon.Foreground = new SolidColorBrush(Color.Parse("#C9444A"));
        HomeIcon.Foreground = new SolidColorBrush(Color.Parse("#6B6F80"));
        
        var mainViewModel = (MainViewModel)DataContext;
        mainViewModel.RouteToSettingsPage();
    }
}
