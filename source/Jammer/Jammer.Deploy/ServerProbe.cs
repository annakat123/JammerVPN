using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Jammer.Core;

namespace Jammer.Deploy;

// A real encrypted DNS round trip without creating an adapter or changing PC routes.
public static class ServerProbe
{
    public static async Task CheckAsync(IPAddress address)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await Check(socket, address).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task Check(Socket socket, IPAddress address)
    {
        await socket.ConnectAsync(new IPEndPoint(address, 7777));
        var key = await new Crypto.ECDH().CreateAesKey(socket);
        var idBytes = Crypto.AES.Decrypt(await Frame.ReadFrameAsync(socket), key);
        if (idBytes.Length != 4 || BitConverter.ToInt32(idBytes) is < 2 or > 254)
            throw new IOException("Сервер вернул неверный адрес туннеля.");
        byte id = (byte)BitConverter.ToInt32(idBytes);
        byte[] dns = Convert.FromHexString("4a4d01000001000000000000076578616d706c6503636f6d0000010001");
        byte[] packet = new byte[28 + dns.Length];
        packet[0] = 0x45; packet[8] = 64; packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        new byte[] { 172, 16, 0, id }.CopyTo(packet, 12);
        new byte[] { 1, 1, 1, 1 }.CopyTo(packet, 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 54321);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 53);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(dns.Length + 8));
        dns.CopyTo(packet, 28);
        uint checksum = 0;
        for (int i = 0; i < 20; i += 2) checksum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i));
        while ((checksum >> 16) != 0) checksum = (checksum & 65535) + (checksum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), (ushort)~checksum);
        await Frame.WriteFrameAsync(socket, Crypto.AES.Encrypt(packet, key));
        var reply = Crypto.AES.Decrypt(await Frame.ReadFrameAsync(socket), key);
        int header = reply.Length == 0 ? 0 : (reply[0] & 15) * 4;
        if (header < 20 || reply.Length < header + 20 || reply[0] >> 4 != 4 || reply[9] != 17 ||
            !reply.AsSpan(12, 4).SequenceEqual(new byte[] { 1, 1, 1, 1 }) ||
            !reply.AsSpan(16, 4).SequenceEqual(new byte[] { 172, 16, 0, id }) ||
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(header)) != 53 ||
            BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(header + 2)) != 54321 ||
            reply[header + 8] != 0x4a || reply[header + 9] != 0x4d ||
            (reply[header + 10] & 0x80) == 0 || (reply[header + 11] & 15) != 0)
            throw new IOException("Проверка трафика через туннель не получила корректный DNS-ответ.");
    }
}
