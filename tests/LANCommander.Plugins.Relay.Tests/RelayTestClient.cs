using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LANCommander.Plugins.Relay.Tests;

/// <summary>
/// Just enough of a relay client to get through the handshake: it sends Hello and reads the reply.
/// Frames are <c>u16 length, u8 type, body</c>, big-endian, with strings as a u16 byte count and
/// UTF-8 (NetworkRelay.md in the Interposer documentation).
/// </summary>
internal sealed class RelayTestClient : IAsyncDisposable
{
    public const byte Hello = 0x01;
    public const byte Welcome = 0x02;
    public const byte Reject = 0x03;
    public const byte PeerPath = 0x08;
    public const byte Datagram = 0x10;
    public const byte Leave = 0x30;
    public const byte UdpHello = 0x41;
    public const byte UdpHelloAck = 0x42;

    private readonly TcpClient _tcp = new();
    private Socket? _udp;
    private IPEndPoint? _udpServer;
    private uint _clientId;
    private uint _token;

    public static async Task<RelayTestClient> ConnectAsync(IPEndPoint server)
    {
        var client = new RelayTestClient();

        await client._tcp.ConnectAsync(IPAddress.Loopback, server.Port);

        return client;
    }

    /// <summary>
    /// Sends Hello and returns the type of the reply and its body. A Welcome is remembered, so
    /// the client can then go on to UDP. <paramref name="direct"/> asks to be introduced to the
    /// other members for direct paths.
    /// </summary>
    public async Task<(byte Type, byte[] Body)> HelloAsync(string network, string? ticket, string player, bool direct = false)
    {
        var body = new MemoryStream();

        body.WriteByte(1); // protocol version
        WriteString(body, network);
        WriteString(body, ticket ?? "");
        WriteString(body, player);
        body.Write(new byte[4]); // no particular address

        if (direct)
        {
            body.WriteByte(0x01); // HelloFlagDirect
            body.Write(new byte[6]); // no local candidate
        }

        await SendAsync(Hello, body.ToArray());

        var reply = await ReceiveAsync();

        if (reply.Type == Welcome)
        {
            _clientId = BinaryPrimitives.ReadUInt32BigEndian(reply.Body);
            _token = BinaryPrimitives.ReadUInt32BigEndian(reply.Body.AsSpan(4));
            Address = ParseWelcomeAddress(reply.Body);
        }

        return reply;
    }

    /// <summary>The virtual address the relay handed out, once welcomed.</summary>
    public IPAddress? Address { get; private set; }

    /// <summary>
    /// Registers a UDP endpoint with the relay and waits for its acknowledgement. The socket uses
    /// blocking calls only, so it works while the thread pool is starved.
    /// </summary>
    public void OpenUdp(IPEndPoint server)
    {
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveTimeout = 10_000 };
        _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _udpServer = new IPEndPoint(IPAddress.Loopback, server.Port);

        SendUdp(UdpHello, []);

        if (ReceiveUdp().Type != UdpHelloAck)
            throw new InvalidOperationException("The relay did not acknowledge UdpHello");
    }

    /// <summary>Sends a game datagram over UDP to <paramref name="to"/>, port 7777 to port 7777.</summary>
    public void SendDatagramUdp(IPAddress to, byte[] payload)
    {
        var body = new byte[10 + payload.Length];

        body[0] = 0; // UDP
        to.TryWriteBytes(body.AsSpan(1, 4), out _);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(5), 7777);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(7), 7777);
        payload.CopyTo(body, 10);

        SendUdp(Datagram, body);
    }

    /// <summary>Waits for a UDP packet from the relay and returns its type and body.</summary>
    public (byte Type, byte[] Body) ReceiveUdp()
    {
        var buffer = new byte[65536];
        var received = _udp!.Receive(buffer);

        return (buffer[8], buffer[9..received]);
    }

    /// <summary>Reads frames until one of <paramref name="type"/> arrives, and returns its body.</summary>
    public async Task<byte[]?> WaitForFrameAsync(byte type, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);

        try
        {
            for (;;)
            {
                var frame = await ReceiveAsync(cancellation.Token);

                if (frame.Type == type)
                    return frame.Body;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void SendUdp(byte type, byte[] body)
    {
        var packet = new byte[9 + body.Length];

        BinaryPrimitives.WriteUInt32BigEndian(packet, _clientId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _token);
        packet[8] = type;
        body.CopyTo(packet, 9);

        _udp!.SendTo(packet, _udpServer!);
    }

    /// <summary>The message of a Reject body: u8 code, str message.</summary>
    public static (byte Code, string Message) ParseReject(byte[] body)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(1));

        return (body[0], Encoding.UTF8.GetString(body, 3, length));
    }

    /// <summary>The address in a Welcome body: u32 client id, u32 token, u32 address, ...</summary>
    public static IPAddress ParseWelcomeAddress(byte[] body)
        => new(body.AsSpan(8, 4));

    public Task LeaveAsync() => SendAsync(Leave, []);

    /// <summary>True once the server has closed the connection.</summary>
    public async Task<bool> IsClosedAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[1024];

        try
        {
            // Pings and peer notices may arrive first; read past them to the end of the stream.
            while (await _tcp.GetStream().ReadAsync(buffer, cancellation.Token) > 0)
            {
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private async Task SendAsync(byte type, byte[] body)
    {
        var frame = new byte[3 + body.Length];

        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(1 + body.Length));
        frame[2] = type;
        body.CopyTo(frame, 3);

        await _tcp.GetStream().WriteAsync(frame);
    }

    private async Task<(byte Type, byte[] Body)> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        return await ReceiveAsync(timeout.Token);
    }

    private async Task<(byte Type, byte[] Body)> ReceiveAsync(CancellationToken cancellationToken)
    {
        var stream = _tcp.GetStream();

        var header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken);

        var frame = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
        await stream.ReadExactlyAsync(frame, cancellationToken);

        return (frame[0], frame[1..]);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = new byte[2];

        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length);

        stream.Write(length);
        stream.Write(bytes);
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        _udp?.Dispose();

        return ValueTask.CompletedTask;
    }
}
