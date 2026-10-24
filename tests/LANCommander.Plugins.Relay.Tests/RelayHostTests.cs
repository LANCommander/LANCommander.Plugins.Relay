using System.Net;
using System.Net.Sockets;
using LANCommander.Interposer.Relay;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Hosting;
using LANCommander.Plugins.Relay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Relay.Tests;

/// <summary>Runs the relay for real, on free loopback ports, and talks to it over the wire.</summary>
public sealed class RelayHostTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = new();
    private ServiceProvider _services = null!;

    private RelayHost Host => _services.GetRequiredService<RelayHost>();
    private RelayActivityLog Activity => _services.GetRequiredService<RelayActivityLog>();

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();

        services.AddLogging();

        RelayPlugin.AddRelayServices(services, _ => _scratch.Paths);

        _services = services.BuildServiceProvider();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task ARelayTurnedOffDoesNotListen()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = false, Port = 0 });

        await Host.StartAsync();

        Assert.Equal(RelayHostState.Stopped, Host.State);
        Assert.Null(Host.TcpEndPoint);
    }

    [Fact]
    public async Task APlayerJoinsAndIsCounted()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = false });

        await Host.StartAsync();

        Assert.Equal(RelayHostState.Running, Host.State);
        Assert.NotNull(Host.UdpEndPoint);

        await using var client = await RelayTestClient.ConnectAsync(Host.TcpEndPoint!);
        var (type, body) = await client.HelloAsync("lan-party", null, "Alice");

        Assert.Equal(RelayTestClient.Welcome, type);
        Assert.StartsWith("10.13.37.", RelayTestClient.ParseWelcomeAddress(body).ToString());

        await Eventually(() => Host.GetStatistics().Sessions == 1);

        var connected = Assert.Single(Host.GetClients());
        Assert.Equal("Alice", connected.PlayerName);
        Assert.Equal("lan-party", connected.Network);
        Assert.Null(connected.State);
        Assert.Equal(1, Host.GetStatistics().PeakClients);

        Assert.Contains(Activity.Entries, entry => entry.Message.Contains("Alice") && entry.Message.Contains("joined"));
    }

    [Fact]
    public async Task APlayerWithoutASignInIsRefusedWhenSignInIsRequired()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = true });

        await Host.StartAsync();

        await using var client = await RelayTestClient.ConnectAsync(Host.TcpEndPoint!);
        var (type, body) = await client.HelloAsync("lan-party", null, "Mallory");

        Assert.Equal(RelayTestClient.Reject, type);

        var (code, message) = RelayTestClient.ParseReject(body);
        Assert.Equal(2, code); // unauthorized
        Assert.Contains("signed in", message);

        Assert.Empty(Host.GetClients());
        Assert.Equal(1, Host.GetStatistics().Refused);
        Assert.Contains(Activity.Entries, entry => entry.Message.StartsWith("Refused"));
    }

    [Fact]
    public async Task ASignedInPlayerIsShownUnderTheirAccountInTheirTicketsNetwork()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = true });

        await Host.StartAsync();

        _services.GetRequiredService<RelayTicketService>().Issue(null, "alice", "game-network", out var ticket);

        await using var client = await RelayTestClient.ConnectAsync(Host.TcpEndPoint!);
        var (type, _) = await client.HelloAsync("lan-party", ticket, "Alice");

        Assert.Equal(RelayTestClient.Welcome, type);

        await Eventually(() => Host.GetClients().Count == 1);

        Assert.Equal(new RelayUser(null, "alice"), Host.GetClients()[0].State);
        Assert.Equal("game-network", Host.GetClients()[0].Network);
    }

    [Fact]
    public async Task AnAdministratorCanDisconnectAPlayer()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = false });

        await Host.StartAsync();

        await using var client = await RelayTestClient.ConnectAsync(Host.TcpEndPoint!);
        await client.HelloAsync("lan-party", null, "Alice");
        await Eventually(() => Host.GetClients().Count == 1);

        Assert.True(Host.Disconnect(Host.GetClients()[0].ClientId));
        Assert.True(await client.IsClosedAsync(TimeSpan.FromSeconds(10)));

        await Eventually(() => Host.GetClients().Count == 0);
        Assert.False(Host.Disconnect(12345));
    }

    [Fact]
    public async Task TrafficOfPlayersWhoLeftIsStillCounted()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = false });

        await Host.StartAsync();

        await using (var client = await RelayTestClient.ConnectAsync(Host.TcpEndPoint!))
        {
            await client.HelloAsync("lan-party", null, "Alice");
            await Eventually(() => Host.GetClients().Count == 1);
            await client.LeaveAsync();
        }

        await Eventually(() => Host.GetClients().Count == 0);

        var statistics = Host.GetStatistics();
        Assert.True(statistics.BytesReceived > 0);
        Assert.True(statistics.BytesSent > 0);
        Assert.Contains(Activity.Entries, entry => entry.Message.Contains("Alice") && entry.Message.Contains("left"));
    }

    [Fact]
    public async Task APortInUseFaultsTheRelayWithAReason()
    {
        using var squatter = new TcpListener(IPAddress.Any, 0);
        squatter.Start();
        var port = ((IPEndPoint)squatter.LocalEndpoint).Port;

        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = port });

        await Host.StartAsync();

        Assert.Equal(RelayHostState.Faulted, Host.State);
        Assert.Contains(port.ToString(), Host.Error);
        Assert.Contains(Activity.Entries, entry => entry.Level == LogLevel.Error);

        // Once the port is free, a restart recovers.
        squatter.Stop();
        await Host.RestartAsync();

        Assert.Equal(RelayHostState.Running, Host.State);
        Assert.Null(Host.Error);
    }

    [Fact]
    public async Task RestartingAppliesNewSettings()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, Udp = true });
        await Host.StartAsync();
        Assert.NotNull(Host.UdpEndPoint);

        _services.GetRequiredService<RelaySettingsStore>().Save(new RelayPluginSettings { Enabled = false });
        await Host.RestartAsync();
        Assert.Equal(RelayHostState.Stopped, Host.State);
        Assert.Null(Host.TcpEndPoint);
        Assert.Contains(Activity.Entries, entry => entry.Message == "Relay stopped");
    }

    [Fact]
    public async Task TheHostedServiceStartsAndStopsTheRelay()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0 });

        var service = new RelayHostedService(Host, _services.GetRequiredService<ILogger<RelayHostedService>>());

        await service.StartAsync(CancellationToken.None);
        Assert.Equal(RelayHostState.Running, Host.State);

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(RelayHostState.Stopped, Host.State);
    }

    [Fact]
    public void ThePluginsRegistrationsResolve()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        new RelayPlugin().ConfigureServices(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<LANCommanderRelayAuthenticator>(provider.GetRequiredService<IRelayAuthenticator>());
        Assert.NotNull(provider.GetRequiredService<RelayTicketService>());
        Assert.NotNull(provider.GetRequiredService<RelayHost>());
        Assert.Contains(provider.GetServices<LANCommander.Server.Plugins.IServerEndpointExtension>(), extension => extension is Endpoints.RelayEndpoints);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition did not become true in time");

            await Task.Delay(20);
        }
    }
}
