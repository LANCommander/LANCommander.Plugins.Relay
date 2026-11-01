using LANCommander.SDK.Plugins;

namespace LANCommander.Plugins.Relay.Storage;

/// <summary>
/// Where the plugin keeps its own state.
/// </summary>
/// <remarks>
/// Everything lives in the <c>Data</c> folder inside the plugin's own folder, which the server keeps
/// when it installs an update over the plugin and deletes when the plugin is uninstalled.
/// </remarks>
public sealed class RelayPluginPaths(string root)
{
    /// <summary>Paths in the plugin's <c>Data</c> folder.</summary>
    public static RelayPluginPaths ForHost() => new(PluginPaths.GetDataDirectory<RelayPluginPaths>());

    public string Root { get; } = Path.GetFullPath(root);

    public string Settings => Path.Combine(Root, "settings.json");

    /// <summary>The key relay tickets are signed with.</summary>
    public string TicketKey => Path.Combine(Root, "ticket.key");
}
