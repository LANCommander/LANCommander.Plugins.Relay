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
    public const byte Leave = 0x30;

    private readonly TcpClient _tcp = new();

    public static async Task<RelayTestClient> ConnectAsync(IPEndPoint server)
    {
        var client = new RelayTestClient();

        await client._tcp.ConnectAsync(IPAddress.Loopback, server.Port);

        return client;
    }

    /// <summary>Sends Hello and returns the type of the reply and its body.</summary>
    public async Task<(byte Type, byte[] Body)> HelloAsync(string network, string? ticket, string player)
    {
        var body = new MemoryStream();

        body.WriteByte(1); // protocol version
        WriteString(body, network);
        WriteString(body, ticket ?? "");
        WriteString(body, player);
        body.Write(new byte[4]); // no particular address

        await SendAsync(Hello, body.ToArray());

        return await ReceiveAsync();
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
        var stream = _tcp.GetStream();

        var header = new byte[2];
        await stream.ReadExactlyAsync(header, timeout.Token);

        var frame = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
        await stream.ReadExactlyAsync(frame, timeout.Token);

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

        return ValueTask.CompletedTask;
    }
}
