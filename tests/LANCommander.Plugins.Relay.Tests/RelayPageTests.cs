using Bunit;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Hosting;
using LANCommander.Plugins.Relay.Pages;
using LANCommander.Plugins.Relay.Storage;
using LANCommander.Server.UI.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Plugins.Relay.Tests;

public sealed class RelayPageTests : BunitContext
{
    private readonly ScratchDirectory _scratch = new();

    public RelayPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLANCommanderServerUI();
        Services.AddLogging();

        RelayPlugin.AddRelayServices(Services, _ => _scratch.Paths);
    }

    protected override void Dispose(bool disposing)
    {
        // Disposing the container stops the relay.
        base.Dispose(disposing);

        if (disposing)
            _scratch.Dispose();
    }

    [Fact]
    public void AStoppedRelaySaysSo()
    {
        var page = Render<RelayPage>();

        Assert.Contains("The relay is turned off", page.Markup);
        Assert.Contains("Nobody is connected", page.Markup);
    }

    [Fact]
    public async Task ThePageFollowsPlayersAsTheyComeAndGo()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = false });

        var host = Services.GetRequiredService<RelayHost>();
        await host.StartAsync();

        var page = Render<RelayPage>();

        page.WaitForAssertion(() => Assert.Contains($"Listening on {host.TcpEndPoint!.Port}", page.Markup));
        Assert.Contains("Relay listening on TCP", page.Markup);

        await using (var client = await RelayTestClient.ConnectAsync(host.TcpEndPoint!))
        {
            await client.HelloAsync("friday-night", null, "Alice");

            page.WaitForAssertion(() =>
            {
                Assert.Contains("Alice", page.Markup);
                Assert.Contains("friday-night", page.Markup);
                Assert.Contains("Not signed in", page.Markup);
            });

            await client.LeaveAsync();
        }

        page.WaitForAssertion(() => Assert.Contains("Nobody is connected", page.Markup));
        Assert.Contains("left network friday-night", page.Markup);
    }

    [Fact]
    public async Task SavingTheSettingsRestartsTheRelay()
    {
        var page = Render<RelayPage>();

        // The Settings tab.
        page.FindAll("[role=tab]")[1].Click();

        page.WaitForAssertion(() => Assert.Contains("Require sign-in", page.Markup));
        Assert.Contains("NetworkRelay:", page.Markup);

        // Flip Enabled and submit; the relay tries the default port, which may or may not be free
        // on the machine running the tests, so only the saved settings are checked.
        page.FindAll("input[type=checkbox], [role=switch]")[0].Click();
        await page.Find("form").SubmitAsync();

        page.WaitForAssertion(() => Assert.True(Services.GetRequiredService<RelaySettingsStore>().Current.Enabled));
        Assert.NotEqual(RelayHostState.Stopped, Services.GetRequiredService<RelayHost>().State);
    }

    [Fact]
    public async Task ASignedInPlayerIsShownWithTheirAccountAndGame()
    {
        _scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = true });

        var host = Services.GetRequiredService<RelayHost>();
        await host.StartAsync();

        // What the session endpoint does when a launcher asks for a ticket
        Services.GetRequiredService<RelayTicketService>().Issue(null, "alice", "3f2a", out var ticket);
        host.LabelNetwork("3f2a", "Quake III Arena");

        var page = Render<RelayPage>();

        await using var client = await RelayTestClient.ConnectAsync(host.TcpEndPoint!);
        await client.HelloAsync("lan-party", ticket, "Alice");

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Signed in as alice", page.Markup);
            Assert.Contains("Quake III Arena", page.Markup);
        });
    }
}
