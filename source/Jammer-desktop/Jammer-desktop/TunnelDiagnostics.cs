using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using Serilog;

namespace Jammer_desktop;

// Packet metadata only. Sample limits keep diagnostics inexpensive under load.
internal sealed class TunnelDiagnostics(string tunnelIp)
{
    private readonly uint _tunnelAddress = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(tunnelIp).GetAddressBytes());
    private long _tunRead, _tcpSent, _tcpReceived, _tunWritten;
    private long _outBytes, _inBytes, _httpsOut, _httpsIn, _foreignSource;
    private int _outSamples, _inSamples, _sourceSamples;

    public void FromTun(byte[] packet)
    {
        if (!AppLogging.Detailed) return;
        Interlocked.Increment(ref _tunRead);
        Interlocked.Add(ref _outBytes, packet.Length);
        Inspect(packet, true);
    }

    public void FrameSent() { if (AppLogging.Detailed) Interlocked.Increment(ref _tcpSent); }
    public void FrameReceived() { if (AppLogging.Detailed) Interlocked.Increment(ref _tcpReceived); }

    public void ToTun(byte[] packet)
    {
        if (!AppLogging.Detailed) return;
        Interlocked.Increment(ref _tunWritten);
        Interlocked.Add(ref _inBytes, packet.Length);
        Inspect(packet, false);
    }

    private void Inspect(byte[] packet, bool outbound)
    {
        if (packet.Length < 20 || packet[0] >> 4 != 4) return;
        if (outbound && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12, 4)) != _tunnelAddress)
        {
            Interlocked.Increment(ref _foreignSource);
            if (Interlocked.Increment(ref _sourceSamples) <= 8)
                Log.Warning("VPN TUN source differs from assigned address: {Source}, expected {TunnelIp}, destination {Destination}",
                    new IPAddress(packet.AsSpan(12, 4)), tunnelIp, new IPAddress(packet.AsSpan(16, 4)));
        }
        int header = (packet[0] & 15) * 4;
        if (packet[9] != 6 || header < 20 || packet.Length < header + 20 ||
            (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6)) & 0x1fff) != 0) return;
        int sourcePort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(header));
        int destinationPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(header + 2));
        if (sourcePort != 443 && destinationPort != 443) return;
        int flags = packet[header + 13];
        long count = outbound ? Interlocked.Increment(ref _httpsOut) : Interlocked.Increment(ref _httpsIn);
        // Prefer connection setup/termination over ordinary data, with a hard session limit.
        if ((flags & 0x07) == 0 && count > 4) return;
        int sample = outbound ? Interlocked.Increment(ref _outSamples) : Interlocked.Increment(ref _inSamples);
        if (sample > 48) return;
        Log.Debug("VPN TCP443 {Direction}: {Source}:{SourcePort} -> {Destination}:{DestinationPort}, flags={Flags}, length={Length}",
            outbound ? "TUN->server" : "server->TUN", new IPAddress(packet.AsSpan(12, 4)), sourcePort,
            new IPAddress(packet.AsSpan(16, 4)), destinationPort, flags, packet.Length);
    }

    public void Report(string reason) => Log.Debug(
        "VPN counters for detailed intervals ({Reason}): TUNread={TunRead}, TCPsent={TcpSent}, TCPreceived={TcpReceived}, TUNwritten={TunWritten}, outBytes={OutBytes}, inBytes={InBytes}, TCP443out={HttpsOut}, TCP443in={HttpsIn}, foreignSource={ForeignSource}",
        reason, Interlocked.Read(ref _tunRead), Interlocked.Read(ref _tcpSent),
        Interlocked.Read(ref _tcpReceived), Interlocked.Read(ref _tunWritten),
        Interlocked.Read(ref _outBytes), Interlocked.Read(ref _inBytes),
        Interlocked.Read(ref _httpsOut), Interlocked.Read(ref _httpsIn), Interlocked.Read(ref _foreignSource));
}
