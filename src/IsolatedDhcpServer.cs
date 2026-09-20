using System.Net;
using System.Net.Sockets;

namespace iPhoneUsbShare;

// iOS brings the USB NCM interface up as a DHCP client. This server is deliberately
// isolated to the NCM host address and only offers the fixed peer address 192.168.99.2.
// It does not advertise a router or DNS server and performs no NAT/ICS.
internal sealed class IsolatedDhcpServer : IDisposable
{
    private readonly string _hostAddress;
    private readonly string _peerAddress;
    private const int ServerPort = 67;
    private const int ClientPort = 68;
    private const uint LeaseSeconds = 3600;
    private readonly Action<string> _log;
    private UdpClient? _socket;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IsolatedDhcpServer(Action<string> log, string hostAddress = "192.168.99.1", string peerAddress = "192.168.99.2") { _log = log; _hostAddress = hostAddress; _peerAddress = peerAddress; }

    public void Start()
    {
        if (_loop is not null) return;

        _socket = new UdpClient(AddressFamily.InterNetwork);
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.EnableBroadcast = true;

        // DHCPDISCOVER is normally sent from 0.0.0.0:68 to 255.255.255.255:67.
        // Bind UDP/67 to all local IPv4 addresses so Windows can deliver that
        // initial broadcast to us. The Windows Firewall rule below restricts
        // inbound UDP/67 to the isolated NCM host address.
        _socket.Client.Bind(new IPEndPoint(IPAddress.Parse(_hostAddress), ServerPort));
        RunFirewall("add");

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _log($"Isolated DHCP server listening on {_hostAddress}:{ServerPort}; fixed lease {_peerAddress}/24, no gateway, no DNS.");
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Close(); } catch { }
        try { RunFirewall("delete"); } catch { }
        try { _loop?.Wait(1000); } catch { }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        _socket?.Dispose();
        _socket = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(cancellationToken);
                if (result.Buffer.Length < 240) continue;
                var packet = result.Buffer;

                if (packet[0] != 1 || packet[1] != 1 || packet[2] < 6) continue;
                if (packet[236] != 99 || packet[237] != 130 || packet[238] != 83 || packet[239] != 99) continue;

                var xid = ReadUInt32(packet, 4);
                var flags = ReadUInt16(packet, 10);
                var messageType = GetOptionByte(packet, 53);
                var serverId = GetOptionAddress(packet, 54);
                var requested = GetOptionAddress(packet, 50);
                if (messageType is null) continue;

                if (messageType == 1) // DHCPDISCOVER
                {
                    await SendReplyAsync(packet, xid, flags, 2, cancellationToken);
                    _log($"DHCP DISCOVER received; offered fixed peer address {_peerAddress}.");
                }
                else if (messageType == 3) // DHCPREQUEST
                {
                    if (serverId is not null && !serverId.Equals(IPAddress.Parse(_hostAddress))) continue;
                    await SendReplyAsync(packet, xid, flags, 5, cancellationToken);
                    _log($"DHCP REQUEST received for {requested?.ToString() ?? PeerAddress}; acknowledged fixed peer address {_peerAddress}.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log($"Isolated DHCP receive error: {ex.GetType().Name}: {ex.Message}");
                if (!cancellationToken.IsCancellationRequested) await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task SendReplyAsync(byte[] request, uint xid, ushort flags, byte messageType, CancellationToken cancellationToken)
    {
        var reply = new byte[300];
        reply[0] = 2; // BOOTREPLY
        reply[1] = 1; // Ethernet
        reply[2] = 6;
        reply[3] = 0;
        WriteUInt32(reply, 4, xid);
        WriteUInt16(reply, 10, flags);
        CopyAddress(reply, 16, IPAddress.Parse(_peerAddress)); // yiaddr
        CopyAddress(reply, 20, IPAddress.Parse(_hostAddress)); // siaddr
        Buffer.BlockCopy(request, 28, reply, 28, 16); // chaddr

        reply[236] = 99;
        reply[237] = 130;
        reply[238] = 83;
        reply[239] = 99;

        var pos = 240;
        pos = AddOptionByte(reply, pos, 53, messageType);
        pos = AddOptionAddress(reply, pos, 54, IPAddress.Parse(_hostAddress));
        pos = AddOption(reply, pos, 1, new byte[] { 255, 255, 255, 0 });
        pos = AddOptionUInt32(reply, pos, 51, LeaseSeconds);
        reply[pos++] = 255;

        // Use the isolated subnet broadcast rather than 255.255.255.255.
        // A global limited broadcast can be routed through another active interface
        // (for example Wi-Fi) on Windows. 192.168.99.255 is unambiguously on the
        // NCM interface because Windows has 192.168.99.1/24 there.
        var destination = new IPEndPoint(GetBroadcastAddress(), ClientPort);

        await _socket!.SendAsync(reply.AsMemory(0, pos), destination, cancellationToken);
        _log($"DHCP {(messageType == 2 ? "OFFER" : "ACK")} sent to {GetBroadcastAddress()}:68 for {_peerAddress}.");
    }

    private static byte? GetOptionByte(byte[] packet, byte wanted)
    {
        var data = FindOption(packet, wanted);
        return data is { Length: > 0 } ? data[0] : null;
    }

    private static IPAddress? GetOptionAddress(byte[] packet, byte wanted)
    {
        var data = FindOption(packet, wanted);
        return data is { Length: 4 } ? new IPAddress(data) : null;
    }

    private static byte[]? FindOption(byte[] packet, byte wanted)
    {
        var pos = 240;
        while (pos < packet.Length)
        {
            var type = packet[pos++];
            if (type == 0) continue;
            if (type == 255) break;
            if (pos >= packet.Length) break;
            var length = packet[pos++];
            if (pos + length > packet.Length) break;
            if (type == wanted) return packet[pos..(pos + length)];
            pos += length;
        }
        return null;
    }

    private static int AddOptionByte(byte[] packet, int pos, byte type, byte value)
        => AddOption(packet, pos, type, new[] { value });

    private static int AddOptionAddress(byte[] packet, int pos, byte type, IPAddress address)
        => AddOption(packet, pos, type, address.GetAddressBytes());

    private static int AddOptionUInt32(byte[] packet, int pos, byte type, uint value)
        => AddOption(packet, pos, type, new[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        });

    private static int AddOption(byte[] packet, int pos, byte type, byte[] value)
    {
        packet[pos++] = type;
        packet[pos++] = (byte)value.Length;
        Buffer.BlockCopy(value, 0, packet, pos, value.Length);
        return pos + value.Length;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private void RunFirewall(string action)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("netsh.exe", $"advfirewall firewall {action} rule name=\\\"iPhoneUsbShare Isolated DHCP {_hostAddress}\\\" dir=in action=allow protocol=UDP localport={ServerPort} localip={_hostAddress} profile=any") { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(2000);
    }

    private IPAddress GetBroadcastAddress()
    {
        var bytes = IPAddress.Parse(_hostAddress).GetAddressBytes();
        bytes[3] = 255;
        return new IPAddress(bytes);
    }

    private static void CopyAddress(byte[] data, int offset, IPAddress address)
        => Buffer.BlockCopy(address.GetAddressBytes(), 0, data, offset, 4);
}
