using System.Reflection;
using LANCommander.Interposer.Relay;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Endpoints;
using LANCommander.Plugins.Relay.Hosting;
using LANCommander.Plugins.Relay.Storage;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(
    typeof(LANCommander.Plugins.Relay.RelayPlugin),
    Id = LANCommander.Plugins.Relay.RelayPlugin.PluginId,
    // IServerEndpointExtension arrives in 2.2.0-alpha.2; 2.2.0-alpha lacks it
    MinHostVersion = "2.2.0-alpha.2",
    Hosts = PluginHost.Server)]

namespace LANCommander.Plugins.Relay;

/// <summary>
/// Hosts the Interposer's network relay, so players on different networks can see each other's
/// LAN games through the server.
/// </summary>
public sealed class RelayPlugin : IPlugin
{
    public const string PluginId = "lancommander.relay";

    public string Id => PluginId;
    public string Name => "Relay";
    public string Version => typeof(RelayPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string Author => "LANCommander";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServerRouteAssemblyExtension>(new RelayRouteAssembly());
        services.AddSingleton<IServerNavigationExtension, RelayNavigation>();
        services.AddSingleton<IServerEndpointExtension, RelayEndpoints>();

        AddRelayServices(services, _ => RelayPluginPaths.ForHost());

        services.AddHostedService<RelayHostedService>();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        var settings = context.Services.GetRequiredService<RelaySettingsStore>().Current;

        context.Logger.LogInformation(
            "Relay plugin keeps its settings in {Root}; the relay is {State}",
            context.Services.GetRequiredService<RelayPluginPaths>().Root,
            settings.Enabled ? $"enabled on port {settings.Port}" : "disabled");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers everything the plugin needs that does not come from the host, so the tests can
    /// build the same container around a scratch directory.
    /// </summary>
    internal static void AddRelayServices(IServiceCollection services, Func<IServiceProvider, RelayPluginPaths> paths)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(paths);
        services.AddSingleton<RelaySettingsStore>();
        services.AddSingleton<RelayActivityLog>();
        services.AddSingleton<RelayTicketService>();
        services.AddSingleton<IRelayAuthenticator, LANCommanderRelayAuthenticator>();
        services.AddSingleton<RelayHost>();
    }

    private sealed class RelayRouteAssembly : IServerRouteAssemblyExtension
    {
        public Assembly Assembly => typeof(RelayPlugin).Assembly;
    }

    private sealed class RelayNavigation : IServerNavigationExtension
    {
        public string Id => $"{PluginId}.settings";
        public string Label => "Relay";
        public string Href => RelayRoutes.Settings;
        public string? Icon => "ShareNetwork";
        public string? Section => ServerNavigationSection.Settings;
    }
}

/// <summary>The plugin's pages.</summary>
public static class RelayRoutes
{
    public const string Settings = "/Plugins/Relay";
}
