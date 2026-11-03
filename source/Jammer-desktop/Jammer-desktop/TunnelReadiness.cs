using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jammer_desktop;

internal static class TunnelReadiness
{
    public static async Task CheckAsync(string tunnelIp, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Parse(tunnelIp), 0));
        await udp.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), timeout.Token);
        byte[] query = Convert.FromHexString("4a4d01000001000000000000076578616d706c6503636f6d0000010001");
        await udp.SendAsync(query, SocketFlags.None, timeout.Token);
        byte[] reply = new byte[1500];
        int length;
        try { length = await udp.ReceiveAsync(reply, SocketFlags.None, timeout.Token); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { throw new TimeoutException("Туннель не прошёл проверку: нет DNS-ответа через JammerTun за 8 секунд."); }
        if (length < 12 || reply[0] != 0x4a || reply[1] != 0x4d || (reply[2] & 0x80) == 0 || (reply[3] & 15) != 0)
            throw new IOException("Туннель вернул неверный DNS-ответ.");
    }
}
