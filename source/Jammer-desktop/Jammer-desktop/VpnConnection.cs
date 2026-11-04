using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jammer.Core;
using Serilog;

namespace Jammer_desktop;

// TCP/7777, ECDH, AES and packet framing recovered from the supplied release.
public sealed class VpnConnection
{
    public static VpnConnection Current { get; } = new();
    private readonly Func<ITun> _createTun;
    private readonly Func<IRoute> _createRoute;
    private CancellationTokenSource? _stop;
    private Socket? _socket;
    private bool _disconnectRequested;
    public bool IsConnected { get; private set; }
    public bool IsConnecting { get; private set; }
    public event Action? Changed;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public VpnConnection(Func<ITun>? createTun = null, Func<IRoute>? createRoute = null)
    {
        _createTun = createTun ?? ITun.CreateTun;
        _createRoute = createRoute ?? IRoute.CreateRoute;
    }

    public void Disconnect()
    {
        _disconnectRequested = true;
        _stop?.Cancel();
        _socket?.Dispose();
    }

    public Task ConnectAsync(string address) => Completion = RunAsync(address);

    private async Task RunAsync(string address)
    {
        if (_stop is not null) throw new InvalidOperationException("Connection is already running");
        using var stop = new CancellationTokenSource();
        _stop = stop;
        _disconnectRequested = false;
        IsConnecting = true;
        Changed?.Invoke();
        ITun? tun = null;
        IRoute? route = null;
        LocalProxy? proxy = null;
        bool adapterCreated = false, routesStarted = false;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _socket = socket;
        try
        {
            Log.Debug("Connecting TCP to VPN server {Server}:7777", address);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), 7777), stop.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            Log.Debug("VPN TCP connected; starting key exchange");
            var key = await new Crypto.ECDH().CreateAesKey(socket).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            var frame = await Frame.ReadFrameAsync(socket).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
            var identifier = Crypto.AES.Decrypt(frame, key);
            if (identifier.Length != 4) throw new InvalidOperationException("Invalid server response");
            int id = BitConverter.ToInt32(identifier);
            if (id is < 2 or > 254) throw new InvalidOperationException("Invalid tunnel address");
            stop.Token.ThrowIfCancellationRequested();
            tun = _createTun();
            tun.InitializeTunnel();
            adapterCreated = true;
            tun.StartSession();
            string tunnelIp = $"172.16.0.{id}";
            Log.Debug("VPN assigned tunnel address {TunnelIp}; configuring adapter and routes", tunnelIp);
            tun.ConfigureIpAddress(tunnelIp, 24);
            route = _createRoute();
            routesStarted = true;
            route.Route(address, tunnelIp, null, null);
            route.DNS();
            var diagnostics = new TunnelDiagnostics(tunnelIp);

            async Task Report()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                try { while (await timer.WaitForNextTickAsync(stop.Token)) diagnostics.Report("10s"); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }

            async Task Receive()
            {
                while (!stop.IsCancellationRequested)
                {
                    var frame = await Frame.ReadFrameAsync(socket);
                    diagnostics.FrameReceived();
                    var packet = Crypto.AES.Decrypt(frame, key);
                    tun.SendPacket(packet);
                    diagnostics.ToTun(packet);
                }
            }
            async Task Send()
            {
                using var readEvent = (tun as WinTun)?.GetReadWaitEvent();
                WaitHandle[]? waits = readEvent is null ? null : new WaitHandle[] { stop.Token.WaitHandle, readEvent };
                while (!stop.IsCancellationRequested)
                {
                    var packet = tun.ReceivePacket();
                    if (packet is null)
                    {
                        if (waits is not null)
                        {
                            if (WaitHandle.WaitAny(waits) == 0) stop.Token.ThrowIfCancellationRequested();
                        }
                        else await Task.Delay(1, stop.Token);
                        continue;
                    }
                    diagnostics.FromTun(packet);
                    await Frame.WriteFrameAsync(socket, Crypto.AES.Encrypt(packet, key));
                    diagnostics.FrameSent();
                }
            }
            var report = Report();
            var receive = Receive();
            // Native packet waiting blocks; keep it off the setup path so readiness can run.
            var send = Task.Run(Send);
            try
            {
                if (tun is WinTun)
                {
                    Log.Debug("Checking DNS through JammerTun before marking VPN connected");
                    var ready = TunnelReadiness.CheckAsync(tunnelIp, stop.Token);
                    await await Task.WhenAny(ready, receive, send);
                    await ready;
                    proxy = LocalProxy.FromSettings(tunnelIp, stop.Token);
                    try { await ProxyDiagnostics.ReportAsync(stop.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { Log.Warning(ex, "Could not inspect system proxy; settings were left unchanged"); }
                }
                IsConnected = true;
                IsConnecting = false;
                Changed?.Invoke();
                Log.Information("VPN connected; tunnel address {TunnelIp}", tunnelIp);
                await await Task.WhenAny(receive, send);
            }
            finally
            {
                stop.Cancel();
                if (proxy is not null)
                    try { await proxy.DisposeAsync(); }
                    catch (Exception ex) { Log.Error(ex, "Could not stop local proxy"); }
                socket.Dispose();
                try { await Task.WhenAll(receive, send); } catch { /* First failure is propagated above. */ }
                await report;
                diagnostics.Report("session end");
            }
        }
        catch (Exception) when (_disconnectRequested) { }
        finally
        {
            socket.Dispose();
            if (routesStarted)
                try { route!.Clean(address, "JammerTun"); }
                catch (Exception ex) { Log.Error(ex, "Could not remove VPN routes"); }
            if (adapterCreated && tun is WinTun)
                try { WinTun.Close(); } catch (Exception ex) { Log.Error(ex, "Could not close TUN adapter"); }
            _socket = null;
            _stop = null;
            IsConnected = IsConnecting = false;
            Changed?.Invoke();
            Log.Information("VPN disconnected");
        }
    }
}
