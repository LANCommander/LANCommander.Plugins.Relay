using System.Reflection;
using System.Runtime.Loader;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Plugins.Relay.Tests;

/// <summary>
/// Loads the built drop-in folder the way the server does, to prove it carries what it needs and
/// nothing that would load a second copy of a host type.
/// </summary>
public sealed class PluginLoadingTests : IDisposable
{
    private static readonly string[] PrivateAssemblies = ["LANCommander.Plugins.Relay", "LANCommander.Interposer.Relay"];

    private readonly ScratchDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void TheDropInFolderHoldsOnlyThePluginsOwnAssemblies()
    {
        var assemblies = Directory.GetFiles(PluginOutputDirectory(), "*.dll").Select(Path.GetFileNameWithoutExtension).Order();

        Assert.Equal(PrivateAssemblies.Order(), assemblies);
        Assert.Empty(Directory.GetDirectories(PluginOutputDirectory()));
    }

    [Fact]
    public void TheServerLoadsThePluginAndItSharesTheHostsTypes()
    {
        var services = new ServiceCollection();
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(services, PluginHost.Server, Deploy(), "2.2.0", NullLogger.Instance);

        var manifest = Assert.Single(loader.LoadedPlugins);

        Assert.Equal(RelayPlugin.PluginId, manifest.Id);

        // The contributions are the host's interface types, so the server can find them.
        var navigation = Assert.IsAssignableFrom<IServerNavigationExtension>(
            services.Single(service => service.ServiceType == typeof(IServerNavigationExtension)).ImplementationType is { } type
                ? Activator.CreateInstance(type, nonPublic: true)
                : null);

        Assert.Equal(RelayRoutes.Settings, navigation.Href);
        Assert.Equal(ServerNavigationSection.Settings, navigation.Section);

        Assert.Contains(services, service => service.ServiceType == typeof(IHostedService));

        var routes = (IServerRouteAssemblyExtension)services
            .Single(service => service.ServiceType == typeof(IServerRouteAssemblyExtension)).ImplementationInstance!;

        var pluginContext = AssemblyLoadContext.GetLoadContext(routes.Assembly);

        Assert.IsType<PluginLoadContext>(pluginContext);

        // Every assembly the plugin references resolves to the host's copy, except its own.
        foreach (var reference in AllReferences(routes.Assembly, pluginContext!))
        {
            var loaded = pluginContext!.LoadFromAssemblyName(reference);
            var expected = PrivateAssemblies.Contains(reference.Name) ? pluginContext : AssemblyLoadContext.Default;

            Assert.True(
                AssemblyLoadContext.GetLoadContext(loaded) == expected,
                $"{reference.Name} loaded into {AssemblyLoadContext.GetLoadContext(loaded)?.Name}");
        }
    }

    [Theory]
    [InlineData("2.1.16", false)]
    // IServerEndpointExtension arrived in 2.2.0-alpha.2. (The unpublished 2.2.0-dev builds sort after
    // alpha, as SemVer compares the labels as text, so they aren't kept out.)
    [InlineData("2.2.0-alpha", false)]
    [InlineData("2.2.0-alpha.1", false)]
    [InlineData("2.2.0-alpha.2", true)]
    [InlineData("2.2.0-beta", true)]
    [InlineData("2.2.0", true)]
    [InlineData("2.3.0", true)]
    public void TheServerSkipsThePluginWhenItPredatesPluginEndpoints(string hostVersion, bool loads)
    {
        var loader = new PluginLoaderService();

        loader.DiscoverAndConfigure(new ServiceCollection(), PluginHost.Server, Deploy(), hostVersion, NullLogger.Instance);

        Assert.Equal(loads, loader.LoadedPlugins.Count == 1);
    }

    private string Deploy()
    {
        var root = Path.Combine(_directory.Path, "Plugins");
        var folder = Path.Combine(root, "LANCommander.Plugins.Relay");

        Directory.CreateDirectory(folder);

        foreach (var file in Directory.GetFiles(PluginOutputDirectory()))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));

        return root;
    }

    private static string PluginOutputDirectory()
        => typeof(PluginLoadingTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "PluginOutputDirectory").Value!;

    /// <summary>
    /// The LANCommander assemblies reachable from the plugin, and the logging abstractions the
    /// relay library takes from the host.
    /// </summary>
    private static IEnumerable<AssemblyName> AllReferences(Assembly root, AssemblyLoadContext context)
    {
        var seen = new HashSet<string>();
        var pending = new Queue<Assembly>([root]);

        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is not { } name
                    || !(name.StartsWith("LANCommander.", StringComparison.Ordinal) || name == "Microsoft.Extensions.Logging.Abstractions")
                    || !seen.Add(name))
                    continue;

                yield return reference;

                var loaded = context.LoadFromAssemblyName(reference);

                if (PrivateAssemblies.Contains(name))
                    pending.Enqueue(loaded);
            }
        }
    }
}
