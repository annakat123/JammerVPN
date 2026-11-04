using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;

namespace Jammer_desktop;

// Compatibility for applications retaining a local HTTP proxy after Happ closes.
internal sealed class LocalProxy : IAsyncDisposable
{
    private readonly IPAddress _source;
    private readonly int _interface;
    private readonly CancellationTokenSource _stop;
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Task> _acceptors = new();
    private readonly HashSet<Task> _tasks = new();
    private readonly SemaphoreSlim _slots = new(64);
    private readonly Task _monitor;
    private int _connectionSamples;

    public static LocalProxy FromSettings(string tunnelIp, CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return new LocalProxy(IPAddress.Parse(tunnelIp), ParseEndpoints(key?.GetValue("ProxyServer") as string), stop);
    }

    public static IPEndPoint[] ParseEndpoints(string? setting) => (setting ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(entry => !entry.Contains('=') || entry.StartsWith("http=", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
        .Select(entry => entry[(entry.IndexOf('=') + 1)..])
        .Select(endpoint => Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out var uri) ? uri : null)
        .Where(uri => uri is not null && uri.UserInfo.Length == 0 && uri.Port > 0 &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))
        .Select(uri => new IPEndPoint(uri!.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback : IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port))
        .Distinct().Take(8).ToArray();

    public LocalProxy(IPAddress source, IPEndPoint[] endpoints, CancellationToken stop)
    {
        if (endpoints.Any(endpoint => !IPAddress.IsLoopback(endpoint.Address)))
            throw new ArgumentException("Proxy listeners must use loopback addresses");
        _source = source;
        if (OperatingSystem.IsWindows())
            _interface = NetworkInterface.GetAllNetworkInterfaces()
                .Select(nic => nic.GetIPProperties())
                .First(props => props.UnicastAddresses.Any(address => address.Address.Equals(source)))
                .GetIPv4Properties().Index;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _monitor = MaintainAsync(endpoints);
    }

    private void Track(Task task)
    {
        lock (_tasks) _tasks.Add(task);
        _ = task.ContinueWith(done => { lock (_tasks) _tasks.Remove(done); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task MaintainAsync(IPEndPoint[] endpoints)
    {
        var pending = endpoints.ToList();
        var reported = new HashSet<IPEndPoint>();
        try
        {
            while (!_stop.IsCancellationRequested && pending.Count > 0)
            {
                foreach (var endpoint in pending.ToArray())
                {
                    var listener = new TcpListener(endpoint) { ExclusiveAddressUse = true };
                    try
                    {
                        listener.Start(64);
                        _listeners.Add(listener);
                        pending.Remove(endpoint);
                        _acceptors.Add(AcceptAsync(listener));
                        Log.Debug("Jammer local HTTP proxy listening on {Endpoint}; outbound source={Source}, interface={Interface}", endpoint, _source, _interface);
                    }
                    catch (SocketException ex)
                    {
                        listener.Stop();
                        if (reported.Add(endpoint)) Log.Debug("Local proxy port {Endpoint} is unavailable ({Error}); existing listener left untouched", endpoint, ex.SocketErrorCode);
                    }
                }
                if (pending.Count > 0) await Task.Delay(2000, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                Track(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException ex) when (_stop.IsCancellationRequested) { Log.Debug("Local proxy listener stopped: {Error}", ex.SocketErrorCode); }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            var stream = client.GetStream();
            bool established = false;
            int length = 0;
            try
            {
                session.CancelAfter(TimeSpan.FromSeconds(15));
                var token = session.Token;
                byte[] header = new byte[16384];
                while (length < header.Length)
                {
                    await stream.ReadExactlyAsync(header.AsMemory(length, 1), token);
                    length++;
                    if (length >= 4 && header[length - 4] == 13 && header[length - 3] == 10 && header[length - 2] == 13 && header[length - 1] == 10) break;
                }
                if (length == header.Length) throw new IOException("Proxy request header too large");
                var lines = Encoding.Latin1.GetString(header, 0, length).Split("\r\n");
                var request = lines[0].Split(' ');
                if (request.Length != 3 || request[2] is not ("HTTP/1.0" or "HTTP/1.1")) throw new IOException("Invalid HTTP proxy request");
                bool connect = request[0] == "CONNECT";
                if (!Uri.TryCreate(connect ? "http://" + request[1] : request[1], UriKind.Absolute, out var target) ||
                    target.Scheme != "http" || target.UserInfo.Length != 0 || target.Port <= 0) throw new IOException("Unsupported proxy target");
                var addresses = await Dns.GetHostAddressesAsync(target.DnsSafeHost.Trim('[', ']'), AddressFamily.InterNetwork, token);
                if (addresses.Length == 0) throw new IOException("Proxy target has no IPv4 address");
                using var remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                remote.Bind(new IPEndPoint(_source, 0));
                if (OperatingSystem.IsWindows())
                    remote.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* IP_UNICAST_IF */, IPAddress.HostToNetworkOrder(_interface));
                await remote.ConnectAsync(new IPEndPoint(addresses[0], target.Port), token);
                if (AppLogging.Detailed && Interlocked.Increment(ref _connectionSamples) <= 16)
                    Log.Debug("Local proxy upstream connected through {Source}: {Destination}:{Port}, CONNECT={Connect}", _source, addresses[0], target.Port, connect);
                using var upstream = new NetworkStream(remote, ownsSocket: false);
                if (connect) await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                else
                {
                    // One ordinary HTTP request per connection; CONNECT remains a full duplex byte stream.
                    string forwarded = $"{request[0]} {target.PathAndQuery} {request[2]}\r\n" +
                        string.Join("\r\n", lines.Skip(1).Where(line => line.Length > 0 &&
                            !line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) &&
                            !line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))) + "\r\nConnection: close\r\n\r\n";
                    await upstream.WriteAsync(Encoding.Latin1.GetBytes(forwarded), token);
                }
                established = true;
                session.CancelAfter(Timeout.InfiniteTimeSpan);
                async Task Pump(NetworkStream from, NetworkStream to, Socket destination)
                {
                    try { await from.CopyToAsync(to, token); destination.Shutdown(SocketShutdown.Send); }
                    catch { session.Cancel(); throw; }
                }
                await Task.WhenAll(Pump(stream, upstream, remote), Pump(upstream, stream, client.Client));
            }
            catch (EndOfStreamException) when (length == 0 && !established) { /* A listener availability probe sends no HTTP request. */ }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    Log.Warning(ex, "Jammer local proxy connection failed: {Error}", ex is SocketException socketError ? socketError.SocketErrorCode.ToString() : ex.GetType().Name);
                    if (!established)
                        try { await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), _stop.Token); } catch { }
                }
            }
            finally { _slots.Release(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _monitor;
        foreach (var listener in _listeners) listener.Stop();
        await Task.WhenAll(_acceptors);
        Task[] tasks;
        lock (_tasks) tasks = _tasks.ToArray();
        await Task.WhenAll(tasks);
        _stop.Dispose();
        _slots.Dispose();
    }
}
