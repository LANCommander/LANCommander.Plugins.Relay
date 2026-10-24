using System.Net;
using LANCommander.Interposer.Relay;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Storage;

namespace LANCommander.Plugins.Relay.Tests;

public sealed class LANCommanderRelayAuthenticatorTests : IDisposable
{
    private static readonly Guid AliceId = Guid.NewGuid();

    private readonly ScratchDirectory _scratch = new();
    private readonly RelaySettingsStore _store;
    private readonly RelayTicketService _tickets;
    private readonly LANCommanderRelayAuthenticator _authenticator;
    private readonly string _validTicket;

    public LANCommanderRelayAuthenticatorTests()
    {
        _store = new RelaySettingsStore(_scratch.Paths);
        _tickets = new RelayTicketService(_scratch.Paths, TimeProvider.System);
        _authenticator = new LANCommanderRelayAuthenticator(_store, _tickets);

        _tickets.Issue(AliceId, "alice", "game-network", out _validTicket);
    }

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequiredSignInRefusesAMissingTicket(string? ticket)
    {
        var result = await AuthenticateAsync(requireAuthentication: true, ticket);

        Assert.False(result.Allowed);
        Assert.Contains("requires", result.Message);
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("v1.e30.e30")]
    public async Task RequiredSignInRefusesAnInvalidTicket(string ticket)
    {
        var result = await AuthenticateAsync(requireAuthentication: true, ticket);

        Assert.False(result.Allowed);
        Assert.Contains("not valid", result.Message);
    }

    [Fact]
    public async Task AValidTicketAdmitsItsHolderToItsOwnNetwork()
    {
        // The client asks for another network; the ticket decides
        var result = await AuthenticateAsync(requireAuthentication: true, _validTicket, network: "somebody-elses-game");

        Assert.True(result.Allowed);
        Assert.Equal("game-network", result.Network);
        Assert.Equal(new RelayUser(AliceId, "alice"), result.State);
        Assert.Null(result.PlayerName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("forged")]
    public async Task OptionalSignInAdmitsEveryoneToTheNetworkTheyAskFor(string? ticket)
    {
        var result = await AuthenticateAsync(requireAuthentication: false, ticket);

        Assert.True(result.Allowed);
        Assert.Null(result.Network);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task OptionalSignInStillHonoursAValidTicket()
    {
        var result = await AuthenticateAsync(requireAuthentication: false, _validTicket);

        Assert.True(result.Allowed);
        Assert.Equal("game-network", result.Network);
        Assert.Equal(new RelayUser(AliceId, "alice"), result.State);
    }

    [Fact]
    public async Task TheSettingIsReadOnEveryHandshake()
    {
        _store.Save(new RelayPluginSettings { RequireAuthentication = false });
        Assert.True((await _authenticator.AuthenticateAsync(Request(null), CancellationToken.None)).Allowed);

        _store.Save(new RelayPluginSettings { RequireAuthentication = true });
        Assert.False((await _authenticator.AuthenticateAsync(Request(null), CancellationToken.None)).Allowed);
    }

    private async Task<RelayAuthenticationResult> AuthenticateAsync(bool requireAuthentication, string? ticket, string network = "lan-party")
    {
        _store.Save(new RelayPluginSettings { RequireAuthentication = requireAuthentication });

        return await _authenticator.AuthenticateAsync(Request(ticket, network), CancellationToken.None);
    }

    private static RelayAuthenticationRequest Request(string? ticket, string network = "lan-party")
        => new(network, ticket!, "Player", new IPEndPoint(IPAddress.Loopback, 50000));
}
