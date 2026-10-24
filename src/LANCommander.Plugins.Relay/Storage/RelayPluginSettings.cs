namespace LANCommander.Plugins.Relay.Storage;

/// <summary>Settings an administrator can change from the plugin's settings page.</summary>
public sealed record RelayPluginSettings
{
    /// <summary>The port the Interposer relay uses by default.</summary>
    public const int DefaultPort = 41300;

    /// <summary>Run the relay. Off until an administrator turns it on.</summary>
    public bool Enabled { get; init; }

    /// <summary>The TCP port clients connect to, and the UDP port datagrams use.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Relay datagrams over UDP once a client has shown UDP reaches it. Off carries everything
    /// over each client's TCP connection, for hosts that cannot open a UDP port.
    /// </summary>
    public bool Udp { get; init; } = true;

    /// <summary>
    /// Only let in clients whose ticket is a valid LANCommander access token for this server.
    /// </summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>The most players one relay network may hold.</summary>
    public int MaxClientsPerNetwork { get; init; } = 64;

    /// <summary>
    /// The host name or address games connect to the relay at, when it is not the one the launcher
    /// reaches the server at: a reverse proxy that only carries HTTP, for example. Null uses the
    /// launcher's.
    /// </summary>
    public string? PublicHost { get; init; }
}
