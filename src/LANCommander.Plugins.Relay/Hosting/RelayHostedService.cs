using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Relay.Hosting;

/// <summary>Starts the relay with the server and stops it on shutdown.</summary>
public sealed class RelayHostedService(RelayHost host, ILogger<RelayHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await host.StartAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A relay that cannot start must not take the server down with it; the settings page
            // shows the error and offers a retry.
            logger.LogError(exception, "The relay failed to start");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync(cancellationToken);
}
