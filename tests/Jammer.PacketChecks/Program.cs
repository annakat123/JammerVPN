using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Jammer.Core;

await Immediate();
Console.WriteLine("PASS: lone packet sent while writer remains open");
await Burst().WaitAsync(TimeSpan.FromSeconds(25));
Console.WriteLine("PASS: legacy frame/AES reader, immediate lone packet, mixed sizes, long burst, slow reader, full ordered delivery and EOF");

static async Task Immediate()
{
    var pair = await Pair(); using var server = pair.Item1; using var client = pair.Item2;
    var queue = Channel.CreateBounded<byte[]>(64); byte[] key = RandomNumberGenerator.GetBytes(32), data = new byte[] {1,2,3};
    var send = PacketSender.SendAsync(server, key, queue.Reader);
    await queue.Writer.WriteAsync(data);
    byte[] received = Crypto.AES.Decrypt(await Frame.ReadFrameAsync(client).WaitAsync(TimeSpan.FromSeconds(2)), key);
    if (!received.SequenceEqual(data)) throw new Exception("Lone packet changed");
    // The writer stays open until after receipt: sending cannot wait for another packet.
    queue.Writer.Complete(); await send.WaitAsync(TimeSpan.FromSeconds(2));
}
static async Task Burst()
{
    var pair = await Pair(); using var server = pair.Item1; using var client = pair.Item2;
    server.SendBufferSize = 4096; client.ReceiveBufferSize = 16384;
    var queue = Channel.CreateBounded<byte[]>(64); byte[] key = RandomNumberGenerator.GetBytes(32);
    var random = new Random(1234);
    var packets = Enumerable.Range(0, 1024).Select(i => { var p = new byte[new[] {0,1,4,64,1400,1500}[i % 6]]; random.NextBytes(p); return p; }).ToArray();
    var send = PacketSender.SendAsync(server, key, queue.Reader);
    var produce = Task.Run(async () => { foreach (var p in packets) await queue.Writer.WriteAsync(p); queue.Writer.Complete(); });
    await Task.Delay(200);
    foreach (var expected in packets)
    {
        byte[] received = Crypto.AES.Decrypt(await Frame.ReadFrameAsync(client).WaitAsync(TimeSpan.FromSeconds(5)), key);
        if (!received.SequenceEqual(expected)) throw new Exception("Framing, order or payload mismatch");
    }
    await produce; await send.WaitAsync(TimeSpan.FromSeconds(5));
    server.Shutdown(SocketShutdown.Send);
    try { await Frame.ReadFrameAsync(client); throw new Exception("Expected clean EOF"); } catch (IOException) { }
}
static async Task<(Socket, Socket)> Pair()
{
    using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen();
    var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    await client.ConnectAsync(listener.LocalEndPoint!); var server = await listener.AcceptAsync(); server.NoDelay = true;
    return (server, client);
}
