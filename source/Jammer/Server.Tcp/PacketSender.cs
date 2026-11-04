using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using Jammer.Core;

static class PacketSender
{
    public static async Task SendAsync(Socket socket, byte[] key, ChannelReader<byte[]> queue)
    {
        byte[] buffer = new byte[16 * 1532];
        while (await queue.WaitToReadAsync())
        {
            int length = 0, count = 0;
            // Drain only packets already available; a lone packet is sent immediately.
            while (count < 16 && queue.TryRead(out var packet))
            {
                byte[] encrypted = Crypto.AES.Encrypt(packet, key);
                if (encrypted.Length > 1528) throw new OverflowException("Invalid frame length");
                BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(length, 4), encrypted.Length);
                encrypted.CopyTo(buffer, length + 4);
                length += 4 + encrypted.Length; count++;
            }
            int sent = 0;
            while (sent < length)
            {
                int written = await socket.SendAsync(buffer.AsMemory(sent, length - sent));
                if (written == 0) throw new IOException("Socket closed during send");
                sent += written;
            }
        }
    }
}
