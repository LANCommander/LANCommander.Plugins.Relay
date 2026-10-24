using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LANCommander.Interposer.Relay;
using LANCommander.Plugins.Relay.Storage;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Relay.Hosting;

/// <summary>Whether the relay is listening.</summary>
public enum RelayHostState
{
    /// <summary>Turned off in the settings.</summary>
    Stopped,

    /// <summary>Listening for clients.</summary>
    Running,

    /// <summary>Turned on, but could not start; <see cref="RelayHost.Error"/> says why.</summary>
    Faulted,
}

/// <summary>Totals since the plugin was loaded, across every restart of the relay.</summary>
/// <param name="Sessions">Clients that have joined a network.</param>
/// <param name="Refused">Clients the authenticator turned away.</param>
/// <param name="PeakClients">The most clients connected at once.</param>
/// <param name="BytesReceived">Bytes received from clients, TCP and UDP.</param>
/// <param name="BytesSent">Bytes sent to clients, TCP and UDP.</param>
public sealed record RelayStatistics(long Sessions, long Refused, int PeakClients, long BytesReceived, long BytesSent);

/// <summary>
/// Owns the relay server: starts it with the saved settings, restarts it when they change, and
/// keeps the figures the settings page shows.
/// </summary>
public sealed class RelayHost : IAsyncDisposable, IDisposable
{
    private readonly RelaySettingsStore _settings;
    private readonly IRelayAuthenticator _authenticator;
    private readonly RelayActivityLog _activity;
    private readonly ILogger<RelayServer> _serverLogger;
    private readonly ILogger<RelayHost> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _statsLock = new();
    private readonly ConcurrentDictionary<string, string> _networkLabels = new(StringComparer.OrdinalIgnoreCase);

    private RelayServer? _server;
    private long _sessions;
    private long _refused;
    private int _peakClients;
    private long _departedBytesReceived;
    private long _departedBytesSent;
    private int _disposed;

    public RelayHost(
        RelaySettingsStore settings,
        IRelayAuthenticator authenticator,
        RelayActivityLog activity,
        ILogger<RelayServer> serverLogger,
        ILogger<RelayHost> logger,
        TimeProvider time)
    {
        _settings = settings;
        _authenticator = new CountingAuthenticator(authenticator, this);
        _activity = activity;
        _serverLogger = activity.Tee(serverLogger);
        _logger = logger;
        _time = time;
    }

    /// <summary>Raised when the relay starts or stops, or a client joins or leaves.</summary>
    public event Action? Changed;

    public RelayHostState State { get; private set; } = RelayHostState.Stopped;

    /// <summary>Why the relay could not start, when <see cref="State"/> is <see cref="RelayHostState.Faulted"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>When the relay last started listening.</summary>
    public DateTimeOffset? StartedOn { get; private set; }

    public IPEndPoint? TcpEndPoint => _server?.TcpEndPoint;

    /// <summary>Null while stopped, or when UDP is turned off.</summary>
    public IPEndPoint? UdpEndPoint => _server?.UdpEndPoint;

    public IReadOnlyList<RelayClientInfo> GetClients() => _server?.GetClients() ?? [];

    public IReadOnlyList<RelayNetworkInfo> GetNetworks() => _server?.GetNetworks() ?? [];

    /// <summary>
    /// Names a network for the settings page, such as the title of the game it was issued for.
    /// Display only: the name a launcher sends is not checked against anything.
    /// </summary>
    public void LabelNetwork(string network, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
            _networkLabels[network] = label.Trim().Length > 100 ? label.Trim()[..100] : label.Trim();
    }

    /// <summary>The name given to a network with <see cref="LabelNetwork"/>, if any.</summary>
    public string? GetNetworkLabel(string network) => _networkLabels.GetValueOrDefault(network);

    public RelayStatistics GetStatistics()
    {
        var clients = GetClients();

        lock (_statsLock)
        {
            return new RelayStatistics(
                _sessions,
                _refused,
                _peakClients,
                _departedBytesReceived + clients.Sum(client => client.BytesReceived),
                _departedBytesSent + clients.Sum(client => client.BytesSent));
        }
    }

    /// <summary>Drops a client. Its game reconnects on its own unless the player has quit.</summary>
    public bool Disconnect(uint clientId) => _server?.Disconnect(clientId) ?? false;

    /// <summary>Starts the relay if the settings turn it on.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => RestartAsync(cancellationToken);

    /// <summary>
    /// Stops the relay and starts it again with the current settings. Connected clients are
    /// dropped and reconnect by themselves, keeping their addresses where they can.
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await StopServerAsync(cancellationToken);

            var settings = _settings.Current;

            if (!settings.Enabled)
            {
                SetState(RelayHostState.Stopped, null);
                return;
            }

            var server = new RelayServer(
                new RelayServerOptions
                {
                    Port = settings.Port,
                    DisableUdp = !settings.Udp,
                    MaxClientsPerNetwork = settings.MaxClientsPerNetwork,
                },
                _authenticator,
                _serverLogger);

            server.ClientJoined += OnClientJoined;
            server.ClientLeft += OnClientLeft;

            try
            {
                await server.StartAsync(cancellationToken);
            }
            catch (SocketException exception)
            {
                server.ClientJoined -= OnClientJoined;
                server.ClientLeft -= OnClientLeft;

                // StartAsync may have bound TCP before UDP failed; release whatever it holds.
                await server.StopAsync(CancellationToken.None);

                var error = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {settings.Port} is already in use by another program."
                    : $"Could not listen on port {settings.Port}: {exception.Message}";

                _logger.LogError(exception, "The relay could not start on port {Port}", settings.Port);
                _activity.Add(LogLevel.Error, error);

                SetState(RelayHostState.Faulted, error);
                return;
            }

            _server = server;
            StartedOn = _time.GetUtcNow();

            SetState(RelayHostState.Running, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await StopServerAsync(cancellationToken);

            SetState(RelayHostState.Stopped, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await StopAsync();

        _gate.Dispose();
    }

    /// <summary>For containers disposed synchronously, which refuse a service that is only async-disposable.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task StopServerAsync(CancellationToken cancellationToken)
    {
        if (_server is not { } server)
            return;

        _server = null;
        StartedOn = null;

        await server.StopAsync(cancellationToken);

        server.ClientJoined -= OnClientJoined;
        server.ClientLeft -= OnClientLeft;

        _activity.Add(LogLevel.Information, "Relay stopped");
    }

    private void SetState(RelayHostState state, string? error)
    {
        State = state;
        Error = error;

        Changed?.Invoke();
    }

    private void OnClientJoined(object? sender, RelayClientEventArgs e)
    {
        var connected = (sender as RelayServer)?.GetClients().Count ?? 0;

        lock (_statsLock)
        {
            _sessions++;
            _peakClients = Math.Max(_peakClients, connected);
        }

        Changed?.Invoke();
    }

    private void OnClientLeft(object? sender, RelayClientEventArgs e)
    {
        lock (_statsLock)
        {
            _departedBytesReceived += e.Client.BytesReceived;
            _departedBytesSent += e.Client.BytesSent;
        }

        Changed?.Invoke();
    }

    /// <summary>Counts refusals, which the relay server reports only to its log.</summary>
    private sealed class CountingAuthenticator(IRelayAuthenticator inner, RelayHost host) : IRelayAuthenticator
    {
        public async ValueTask<RelayAuthenticationResult> AuthenticateAsync(
            RelayAuthenticationRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.AuthenticateAsync(request, cancellationToken);

            if (!result.Allowed)
            {
                lock (host._statsLock)
                    host._refused++;

                host.Changed?.Invoke();
            }

            return result;
        }
    }
}
