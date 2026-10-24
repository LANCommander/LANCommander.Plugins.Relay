using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Endpoints;
using LANCommander.Plugins.Relay.Hosting;
using LANCommander.Plugins.Relay.Storage;
using LANCommander.SDK.Models;
using LANCommander.SDK.Services;
using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LANCommander.Plugins.Relay.Tests;

/// <summary>
/// The plugin's API on a test server, mounted the way LANCommander's server mounts a plugin's
/// endpoints: under <c>/api/Plugins/{RoutePrefix}</c>, behind the extension's declared access.
/// </summary>
public sealed class RelayEndpointsTests : IAsyncLifetime
{
    private static readonly Guid AliceId = Guid.NewGuid();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ScratchDirectory _scratch = new();
    private WebApplication _app = null!;

    private RelayHost Host => _app.Services.GetRequiredService<RelayHost>();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddAuthorization();
        builder.Services
            .AddAuthentication(TestAuthentication.Scheme)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.Scheme, null);

        RelayPlugin.AddRelayServices(builder.Services, _ => _scratch.Paths);

        _app = builder.Build();

        _app.UseRouting();
        _app.UseAuthentication();
        _app.UseAuthorization();

        IServerEndpointExtension extension = new RelayEndpoints();

        // As LANCommander.Server's PluginEndpoints does
        extension.MapEndpoints(_app
            .MapGroup($"/api/Plugins/{extension.RoutePrefix}")
            .RequireAuthorization(policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => extension.Access.IsSatisfiedBy(context.User))));

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public void TheRouteIsTheOneTheLauncherCalls() =>
        Assert.Equal(RelayClient.SessionsRoute, $"/api/Plugins/{RelayEndpoints.Prefix}/Sessions");

    [Fact]
    public async Task ASignedInPlayerGetsATicketIntoTheGamesNetwork()
    {
        await StartRelayAsync();

        var gameId = Guid.NewGuid();
        var response = await PostAsync(new RelaySessionRequest { GameId = gameId, GameTitle = "Quake III Arena" }, "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = (await response.Content.ReadFromJsonAsync<RelaySession>(Json))!;

        Assert.Equal(Host.TcpEndPoint!.Port, session.Port);
        Assert.Equal(gameId.ToString("N"), session.Network);
        Assert.Null(session.Host);

        var ticket = _app.Services.GetRequiredService<RelayTicketService>().Validate(session.Ticket);

        Assert.Equal(new RelayTicket(AliceId, "alice", session.Network, session.ExpiresOn), ticket);
        Assert.Equal("Quake III Arena", Host.GetNetworkLabel(session.Network));
    }

    [Fact]
    public async Task EveryPlayerOfAGameGetsTheSameNetwork()
    {
        await StartRelayAsync();

        var gameId = Guid.NewGuid();

        var alice = await (await PostAsync(new RelaySessionRequest { GameId = gameId }, "alice")).Content.ReadFromJsonAsync<RelaySession>(Json);
        var bob = await (await PostAsync(new RelaySessionRequest { GameId = gameId }, "bob")).Content.ReadFromJsonAsync<RelaySession>(Json);
        var other = await (await PostAsync(new RelaySessionRequest { GameId = Guid.NewGuid() }, "bob")).Content.ReadFromJsonAsync<RelaySession>(Json);

        Assert.Equal(alice!.Network, bob!.Network);
        Assert.NotEqual(alice.Network, other!.Network);
        Assert.NotEqual(alice.Ticket, bob.Ticket);
    }

    [Fact]
    public async Task TheSessionNamesThePublicAddressWhenOneIsSet()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, PublicHost = "relay.example.com" });
        await Host.StartAsync();

        var session = await (await PostAsync(new RelaySessionRequest { GameId = Guid.NewGuid() }, "alice")).Content.ReadFromJsonAsync<RelaySession>(Json);

        Assert.Equal("relay.example.com", session!.Host);
    }

    [Fact]
    public async Task AnonymousCallersGetNothing()
    {
        await StartRelayAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(new RelaySessionRequest { GameId = Guid.NewGuid() }, user: null)).StatusCode);
    }

    [Fact]
    public async Task ARelayThatIsNotRunningIsUnavailable()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = false });
        await Host.StartAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(new RelaySessionRequest { GameId = Guid.NewGuid() }, "alice")).StatusCode);
    }

    [Fact]
    public async Task AGameIsRequired()
    {
        await StartRelayAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(new RelaySessionRequest(), "alice")).StatusCode);
    }

    [Fact]
    public async Task TheTicketGetsAGameIntoTheRelay()
    {
        await StartRelayAsync();

        var session = await (await PostAsync(new RelaySessionRequest { GameId = Guid.NewGuid() }, "alice")).Content.ReadFromJsonAsync<RelaySession>(Json);

        await using var client = await RelayTestClient.ConnectAsync(Host.TcpEndPoint!);

        // Whatever network the game's own config names, the ticket's wins
        var (type, _) = await client.HelloAsync("lan-party", session!.Ticket, "Alice");

        Assert.Equal(RelayTestClient.Welcome, type);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Host.GetClients().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var joined = Assert.Single(Host.GetClients());
        Assert.Equal(session.Network, joined.Network);
        Assert.Equal(new RelayUser(AliceId, "alice"), joined.State);
    }

    private async Task StartRelayAsync()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = true });

        await Host.StartAsync();

        Assert.Equal(RelayHostState.Running, Host.State);
    }

    private Task<HttpResponseMessage> PostAsync(RelaySessionRequest request, string? user)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, RelayClient.SessionsRoute)
        {
            Content = JsonContent.Create(request),
        };

        if (user is not null)
            message.Headers.Add(TestAuthentication.UserHeader, user);

        return _app.GetTestClient().SendAsync(message);
    }

    /// <summary>Signs in whoever the request's header names, as the server's bearer handler would a launcher.</summary>
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Scheme = "Test";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            // The claims LANCommander's AuthenticationService puts in an access token
            var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };

            if (user == "alice")
                claims.Add(new Claim(ClaimTypes.NameIdentifier, AliceId.ToString()));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
        }
    }
}
