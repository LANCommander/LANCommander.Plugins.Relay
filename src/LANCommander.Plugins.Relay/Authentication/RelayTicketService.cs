using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LANCommander.Plugins.Relay.Storage;

namespace LANCommander.Plugins.Relay.Authentication;

/// <summary>What a relay ticket vouches for.</summary>
/// <param name="UserId">The account's id, when the server knows one.</param>
/// <param name="UserName">The account's user name.</param>
/// <param name="Network">The one relay network the ticket admits its holder to.</param>
/// <param name="ExpiresOn">When the ticket stops being accepted.</param>
public sealed record RelayTicket(Guid? UserId, string UserName, string Network, DateTimeOffset ExpiresOn);

/// <summary>
/// Issues and checks relay tickets: proof that the server let a signed-in user into a relay
/// network. A launcher asks for one through the plugin's API and hands it to the game's Interposer,
/// which presents it when it connects, and again on every reconnect.
/// </summary>
/// <remarks>
/// <para>
/// A ticket is <c>v1.{payload}.{signature}</c>, both base64url, the signature an HMAC-SHA256 of the
/// payload under a key only this server has. It is good for the relay and nothing else, so unlike
/// the access token it replaces, one that is seen on the wire cannot be used against the server's
/// API; and it only admits its holder to the network it names, for <see cref="Lifetime"/>.
/// </para>
/// <para>
/// The key is made on first use and kept in the plugin's data directory. Deleting it and restarting
/// the server invalidates every ticket issued so far.
/// </para>
/// </remarks>
public sealed class RelayTicketService(RelayPluginPaths paths, TimeProvider time)
{
    private const string Version = "v1";

    /// <summary>
    /// How long a ticket is accepted. Long enough for a session, reconnects included, since the
    /// Interposer reconnects with the ticket it started with.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly Lock _lock = new();
    private byte[]? _key;

    /// <summary>Issues a ticket for <paramref name="userName"/> to join <paramref name="network"/>.</summary>
    public RelayTicket Issue(Guid? userId, string userName, string network, out string ticket)
    {
        // Whole seconds, as the ticket carries it, so what is reported matches what is enforced
        var expiresOn = DateTimeOffset.FromUnixTimeSeconds((time.GetUtcNow() + Lifetime).ToUnixTimeSeconds());
        var issued = new RelayTicket(userId, userName, network, expiresOn);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(issued.UserId, issued.UserName, issued.Network, issued.ExpiresOn.ToUnixTimeSeconds()),
            RelayTicketJsonContext.Default.Payload);

        ticket = $"{Version}.{Base64Url.EncodeToString(payload)}.{Base64Url.EncodeToString(Sign(payload))}";

        return issued;
    }

    /// <summary>What the ticket vouches for, or null when it is not one of ours, has been altered, or has expired.</summary>
    public RelayTicket? Validate(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        var parts = ticket.Trim().Split('.');

        if (parts.Length != 3 || parts[0] != Version)
            return null;

        byte[] payload, signature;

        try
        {
            payload = Base64Url.DecodeFromChars(parts[1]);
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payload)))
            return null;

        Payload? claims;

        try
        {
            claims = JsonSerializer.Deserialize(payload, RelayTicketJsonContext.Default.Payload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (claims is null || string.IsNullOrWhiteSpace(claims.UserName) || claims.Network is null)
            return null;

        var expiresOn = DateTimeOffset.FromUnixTimeSeconds(claims.ExpiresOn);

        if (expiresOn <= time.GetUtcNow())
            return null;

        return new RelayTicket(claims.UserId, claims.UserName, claims.Network, expiresOn);
    }

    private byte[] Sign(byte[] payload) => HMACSHA256.HashData(Key, payload);

    private byte[] Key
    {
        get
        {
            lock (_lock)
                return _key ??= LoadOrCreateKey();
        }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(paths.TicketKey))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(paths.TicketKey, Encoding.ASCII).Trim());

            if (existing.Length >= 32)
                return existing;
        }

        var key = RandomNumberGenerator.GetBytes(32);

        Directory.CreateDirectory(Path.GetDirectoryName(paths.TicketKey)!);

        // Written to a temporary file first, so a crash part-way leaves no truncated key behind
        var temporary = $"{paths.TicketKey}.{Guid.NewGuid():N}.tmp";

        File.WriteAllText(temporary, Convert.ToBase64String(key), Encoding.ASCII);
        File.Move(temporary, paths.TicketKey, overwrite: true);

        return key;
    }

    /// <summary>The signed part of a ticket. Short names, since the ticket travels in every Hello.</summary>
    internal sealed record Payload(
        [property: JsonPropertyName("u")] Guid? UserId,
        [property: JsonPropertyName("n")] string UserName,
        [property: JsonPropertyName("w")] string Network,
        [property: JsonPropertyName("e")] long ExpiresOn);
}
