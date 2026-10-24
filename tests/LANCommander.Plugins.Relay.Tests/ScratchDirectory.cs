using LANCommander.Plugins.Relay.Storage;

namespace LANCommander.Plugins.Relay.Tests;

/// <summary>A temporary plugin data directory, deleted afterwards.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "relay-plugin-tests", Guid.NewGuid().ToString("N"));
        Paths = new RelayPluginPaths(Path);
    }

    public string Path { get; }

    public RelayPluginPaths Paths { get; }

    /// <summary>
    /// Writes settings as they are, bypassing <see cref="RelaySettingsStore.Save"/>, so a test can
    /// use port 0 to have the relay pick free ports.
    /// </summary>
    public void WriteSettings(RelayPluginSettings settings)
        => JsonFile.Write(Paths.Settings, settings, RelayJsonContext.Default.RelayPluginSettings);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // A plugin assembly loaded from here stays locked until the process exits.
        }
    }
}
