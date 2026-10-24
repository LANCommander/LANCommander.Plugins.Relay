using LANCommander.Plugins.Relay.Storage;

namespace LANCommander.Plugins.Relay.Tests;

public sealed class RelaySettingsStoreTests
{
    [Fact]
    public void DefaultsWhenNothingIsSaved()
    {
        using var scratch = new ScratchDirectory();

        var settings = new RelaySettingsStore(scratch.Paths).Current;

        Assert.False(settings.Enabled);
        Assert.Equal(RelayPluginSettings.DefaultPort, settings.Port);
        Assert.True(settings.Udp);
        Assert.True(settings.RequireAuthentication);
    }

    [Fact]
    public void SavedSettingsSurviveARestart()
    {
        using var scratch = new ScratchDirectory();

        new RelaySettingsStore(scratch.Paths).Save(new RelayPluginSettings
        {
            Enabled = true,
            Port = 50000,
            Udp = false,
            RequireAuthentication = false,
            MaxClientsPerNetwork = 8,
        });

        var reloaded = new RelaySettingsStore(scratch.Paths).Current;

        Assert.Equal(new RelayPluginSettings
        {
            Enabled = true,
            Port = 50000,
            Udp = false,
            RequireAuthentication = false,
            MaxClientsPerNetwork = 8,
        }, reloaded);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(70000, 65535)]
    [InlineData(41300, 41300)]
    public void PortIsClampedToAValidPort(int requested, int expected)
    {
        using var scratch = new ScratchDirectory();

        var saved = new RelaySettingsStore(scratch.Paths).Save(new RelayPluginSettings { Port = requested });

        Assert.Equal(expected, saved.Port);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1000, 254)]
    public void PlayersPerNetworkIsClampedToWhatTheSubnetHolds(int requested, int expected)
    {
        using var scratch = new ScratchDirectory();

        var saved = new RelaySettingsStore(scratch.Paths).Save(new RelayPluginSettings { MaxClientsPerNetwork = requested });

        Assert.Equal(expected, saved.MaxClientsPerNetwork);
    }
}
