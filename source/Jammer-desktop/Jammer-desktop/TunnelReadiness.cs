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
        try
        {
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Windows may still be validating the newly assigned address after netsh returns.
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { udp.Bind(new IPEndPoint(IPAddress.Parse(tunnelIp), 0)); break; }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
                { await Task.Delay(100, timeout.Token); }
            }
            await udp.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53), timeout.Token);
            byte[] query = Convert.FromHexString("4a4d01000001000000000000076578616d706c6503636f6d0000010001");
            await udp.SendAsync(query, SocketFlags.None, timeout.Token);
            byte[] reply = new byte[1500];
            int length = await udp.ReceiveAsync(reply, SocketFlags.None, timeout.Token);
            if (length < 12 || reply[0] != 0x4a || reply[1] != 0x4d || (reply[2] & 0x80) == 0 || (reply[3] & 15) != 0)
                throw new IOException("Туннель вернул неверный DNS-ответ.");
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { throw new TimeoutException("Туннель не готов: адрес недоступен или нет DNS-ответа через JammerTun за 8 секунд."); }
    }
}
