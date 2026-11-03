using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Jammer_desktop;

static class ProxyCheck
{
    static readonly Type Proxy = typeof(VpnConnection).Assembly.GetType("Jammer_desktop.LocalProxy")!;
    static IAsyncDisposable Start(int port, IPAddress? source = null) => (IAsyncDisposable)Activator.CreateInstance(Proxy,
        new object[] { source ?? IPAddress.Loopback, new[] { new IPEndPoint(IPAddress.Loopback, port) }, CancellationToken.None })!;
    static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = Port(listener); listener.Stop(); return port; }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    static async Task<string> Header(NetworkStream stream, CancellationToken stop)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            await stream.ReadExactlyAsync(one, stop); bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) return Encoding.Latin1.GetString(bytes.ToArray());
        }
        throw new Exception("Header too long");
    }

    public static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var stop = deadline.Token;
        var parsed = (IPEndPoint[])Proxy.GetMethod("ParseEndpoints")!.Invoke(null,
            new object[] { "http=127.0.0.1:10809;https=127.0.0.1:10809;socks=127.0.0.1:10808;192.0.2.1:8080;0.0.0.0:8080" })!;
        Check(parsed.Length == 1 && IPAddress.IsLoopback(parsed[0].Address), "only distinct local proxy endpoints are eligible");
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        int proxyPort = FreePort();
        var service = Start(proxyPort);
        try
        {
            byte[] payload = new byte[200_000]; Random.Shared.NextBytes(payload);
            var echo = Task.Run(async () =>
            {
                using var server = await origin.AcceptTcpClientAsync(stop);
                Check(((IPEndPoint)server.Client.RemoteEndPoint!).Address.Equals(IPAddress.Loopback), "upstream connection uses the supplied source interface");
                var input = new byte[payload.Length];
                await server.GetStream().ReadExactlyAsync(input, stop);
                Check(input.SequenceEqual(payload), "CONNECT preserves all outgoing bytes including bytes following its header");
                await server.GetStream().WriteAsync(input, stop);
            });
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, stop);
                var stream = client.GetStream();
                byte[] header = Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{Port(origin)} HTTP/1.1\r\nHost: localhost\r\n\r\n");
                // Send application data immediately: a proxy must not consume/drop it with the header.
                var send = stream.WriteAsync(header.Concat(payload).ToArray(), stop).AsTask();
                Check((await Header(stream, stop)).StartsWith("HTTP/1.1 200"), "CONNECT becomes ready after upstream connects");
                var reply = new byte[payload.Length];
                await stream.ReadExactlyAsync(reply, stop); await send;
                Check(reply.SequenceEqual(payload), "CONNECT returns the full binary response");
            }
            await echo;

            var http = Task.Run(async () =>
            {
                using var server = await origin.AcceptTcpClientAsync(stop);
                string request = await Header(server.GetStream(), stop);
                Check(request.StartsWith("GET /test?q=1 HTTP/1.1\r\n") && request.Contains("Connection: close") && !request.Contains("Proxy-Authorization"),
                    "ordinary HTTP rewrites the target and removes proxy credentials");
                await server.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray(), stop);
            });
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxyPort, stop);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{Port(origin)}/test?q=1 HTTP/1.1\r\nHost: localhost\r\nProxy-Authorization: Basic test\r\n\r\n"), stop);
                Check((await Header(client.GetStream(), stop)).StartsWith("HTTP/1.1 200"), "ordinary HTTP response reaches the client");
                byte[] body = new byte[2]; await client.GetStream().ReadExactlyAsync(body, stop);
                Check(Encoding.ASCII.GetString(body) == "OK", "ordinary HTTP body is preserved");
            }
            await http;
            // Leave an incomplete request open while disconnecting.
            using var stalled = new TcpClient();
            await stalled.ConnectAsync(IPAddress.Loopback, proxyPort, stop);
            await stalled.GetStream().WriteAsync("CON"u8.ToArray(), stop);
        }
        finally { await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        using (var reuse = new TcpListener(IPAddress.Loopback, proxyPort)) { reuse.Start(); Check(true, "disconnect releases the local port and pending requests"); }
        await using (var reconnect = Start(proxyPort))
        using (var client = new TcpClient()) { await client.ConnectAsync(IPAddress.Loopback, proxyPort, stop); Check(true, "local proxy starts again on reconnect"); }

        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Server.ExclusiveAddressUse = true; occupied.Start();
        int occupiedPort = Port(occupied);
        await using var waiting = Start(occupiedPort);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, occupiedPort, stop);
            using var accepted = await occupied.AcceptTcpClientAsync(stop);
            Check(true, "an existing proxy listener is left untouched");
        }
        occupied.Stop();
        await Task.Delay(2500, stop);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, occupiedPort, stop);
            await client.GetStream().WriteAsync("bad\r\n\r\n"u8.ToArray(), stop);
            Check((await Header(client.GetStream(), stop)).StartsWith("HTTP/1.1 502"), "Jammer takes the free local port after the previous proxy stops");
        }
    }
}
