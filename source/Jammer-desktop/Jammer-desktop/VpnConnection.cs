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
        bool adapterCreated = false, routesStarted = false;
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _socket = socket;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), 7777), stop.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
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
            tun.ConfigureIpAddress(tunnelIp, 24);
            route = _createRoute();
            routesStarted = true;
            route.Route(address, tunnelIp, null, null);
            route.DNS();
            IsConnected = true;
            IsConnecting = false;
            Changed?.Invoke();
            Log.Information("VPN connected; tunnel address {TunnelIp}", tunnelIp);

            async Task Receive()
            {
                while (!stop.IsCancellationRequested)
                    tun.SendPacket(Crypto.AES.Decrypt(await Frame.ReadFrameAsync(socket), key));
            }
            async Task Send()
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = tun.ReceivePacket();
                    if (packet is null) { await Task.Delay(1, stop.Token); continue; }
                    await Frame.WriteFrameAsync(socket, Crypto.AES.Encrypt(packet, key));
                }
            }
            var receive = Receive();
            var send = Send();
            try { await await Task.WhenAny(receive, send); }
            finally
            {
                stop.Cancel();
                socket.Dispose();
                try { await Task.WhenAll(receive, send); } catch { /* First failure is propagated above. */ }
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
