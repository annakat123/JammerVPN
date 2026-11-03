using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;

namespace Jammer_desktop;

internal static class ProxyDiagnostics
{
    public static async Task ReportAsync(CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Observe the user's proxy; never change its settings or replace its listener.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        bool enabled = Convert.ToInt32(key?.GetValue("ProxyEnable", 0)) != 0;
        Log.Information("Windows manual proxy enabled={Enabled}; settings preserved", enabled);
        if (!enabled) return;
        string server = key?.GetValue("ProxyServer") as string ?? "";
        foreach (string entry in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string endpoint = entry[(entry.IndexOf('=') + 1)..];
            if (!Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out var uri) ||
                !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                  IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))) continue;
            using var socket = new TcpClient();
            try
            {
                await socket.ConnectAsync(uri.Host, uri.Port, stop).AsTask().WaitAsync(TimeSpan.FromSeconds(1), stop);
                Log.Information("Local proxy listener {Host}:{Port} is reachable", uri.Host, uri.Port);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException)
            { Log.Warning("Local proxy {Host}:{Port} is unavailable. Apps using it need the proxy process running; TUN cannot replace it", uri.Host, uri.Port); }
        }
    }
}
