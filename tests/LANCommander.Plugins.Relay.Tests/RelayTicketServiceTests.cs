using System.Buffers.Text;
using System.Text;
using LANCommander.Plugins.Relay.Authentication;

namespace LANCommander.Plugins.Relay.Tests;

public sealed class RelayTicketServiceTests : IDisposable
{
    private static readonly Guid AliceId = Guid.NewGuid();

    private readonly ScratchDirectory _scratch = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, 250, TimeSpan.Zero));

    public void Dispose() => _scratch.Dispose();

    private RelayTicketService Service() => new(_scratch.Paths, _time);

    [Fact]
    public void ATicketVouchesForWhatItWasIssuedFor()
    {
        var service = Service();

        var issued = service.Issue(AliceId, "alice", "network-1", out var ticket);

        // To the second, as the ticket carries it
        var expected = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new RelayTicket(AliceId, "alice", "network-1", expected), issued);
        Assert.Equal(issued, service.Validate(ticket));
    }

    [Fact]
    public void ATicketOutlivesARestartOfTheServer()
    {
        Service().Issue(null, "alice", "network-1", out var ticket);

        Assert.Equal("alice", Service().Validate(ticket)?.UserName);
    }

    [Fact]
    public void ATicketIsRefusedOnceItExpires()
    {
        var service = Service();
        var issued = service.Issue(AliceId, "alice", "network-1", out var ticket);

        _time.Now = issued.ExpiresOn - TimeSpan.FromMilliseconds(1);
        Assert.NotNull(service.Validate(ticket));

        _time.Now = issued.ExpiresOn;
        Assert.Null(service.Validate(ticket));
    }

    [Fact]
    public void AnotherServersTicketIsRefused()
    {
        using var other = new ScratchDirectory();
        new RelayTicketService(other.Paths, _time).Issue(AliceId, "alice", "network-1", out var ticket);

        Assert.Null(Service().Validate(ticket));
    }

    [Fact]
    public void ReplacingTheKeyRevokesEveryTicket()
    {
        Service().Issue(AliceId, "alice", "network-1", out var ticket);

        File.Delete(_scratch.Paths.TicketKey);

        Assert.Null(Service().Validate(ticket));
    }

    [Fact]
    public void AnAlteredTicketIsRefused()
    {
        var service = Service();
        service.Issue(AliceId, "alice", "network-1", out var ticket);

        var parts = ticket.Split('.');

        // Someone rewrites the payload to get into another game's network, keeping the signature
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1])).Replace("network-1", "network-2");
        var forged = $"{parts[0]}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";

        Assert.Null(service.Validate(forged));

        // Or flips a byte of the signature
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature[0] ^= 1;

        Assert.Null(service.Validate($"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("letmein")]
    [InlineData("v1.only-two")]
    [InlineData("v1.!!!.!!!")]
    [InlineData("v2.e30.e30")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJuYW1lIjoiYWxpY2UifQ.c2lnbmF0dXJl")]
    public void SomethingThatIsNotATicketIsRefused(string? ticket) =>
        Assert.Null(Service().Validate(ticket));

    [Fact]
    public void SurroundingWhitespaceIsIgnored()
    {
        var service = Service();
        service.Issue(AliceId, "alice", "network-1", out var ticket);

        Assert.NotNull(service.Validate($"  {ticket}\n"));
    }

    [Fact]
    public void ATicketIsCompact()
    {
        // It travels in every Hello the game sends, and must fit the Interposer's u16 strings easily
        Service().Issue(AliceId, "a-rather-long-user-name-for-a-lan-party", Guid.NewGuid().ToString("N"), out var ticket);

        Assert.True(ticket.Length < 300, $"{ticket.Length} characters");
        Assert.DoesNotContain('\n', ticket);
    }
}
