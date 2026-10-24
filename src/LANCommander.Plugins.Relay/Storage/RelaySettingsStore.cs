namespace LANCommander.Plugins.Relay.Storage;

/// <summary>Holds the plugin's settings and persists them.</summary>
public sealed class RelaySettingsStore(RelayPluginPaths paths)
{
    private readonly Lock _lock = new();
    private RelayPluginSettings? _settings;

    public RelayPluginSettings Current
    {
        get
        {
            lock (_lock)
                return _settings ??= JsonFile.Read(paths.Settings, RelayJsonContext.Default.RelayPluginSettings)
                                     ?? new RelayPluginSettings();
        }
    }

    /// <summary>Saves the settings, clamped to values the relay accepts, and returns what was saved.</summary>
    public RelayPluginSettings Save(RelayPluginSettings settings)
    {
        var normalized = settings with
        {
            Port = Math.Clamp(settings.Port, 1, 65535),
            MaxClientsPerNetwork = Math.Clamp(settings.MaxClientsPerNetwork, 2, 254),
            PublicHost = string.IsNullOrWhiteSpace(settings.PublicHost) ? null : settings.PublicHost.Trim(),
        };

        lock (_lock)
        {
            JsonFile.Write(paths.Settings, normalized, RelayJsonContext.Default.RelayPluginSettings);

            _settings = normalized;
        }

        return normalized;
    }
}
