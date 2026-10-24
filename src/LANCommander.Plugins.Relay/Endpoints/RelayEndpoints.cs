using System.Security.Claims;
using LANCommander.Plugins.Relay.Authentication;
using LANCommander.Plugins.Relay.Hosting;
using LANCommander.Plugins.Relay.Storage;
using LANCommander.SDK.Models;
using LANCommander.SDK.Services;
using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LANCommander.Plugins.Relay.Endpoints;

/// <summary>
/// The plugin's API, at <c>/api/Plugins/Relay</c>: where a signed-in launcher gets a ticket into a
/// game's relay network. See <see cref="RelayClient"/> for the launcher's side.
/// </summary>
public sealed class RelayEndpoints : IServerEndpointExtension
{
    public const string Prefix = "Relay";

    public string RoutePrefix => Prefix;

    // Any player may join the relay, so any signed-in user may ask for a ticket
    public PluginAccessPolicy Access => PluginAccessPolicy.AuthenticatedUser;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // RelayClient.SessionsRoute
        endpoints.MapPost("/Sessions", CreateSession);
    }

    /// <summary>
    /// Issues the caller a ticket into the game's relay network: one network per game, so everyone
    /// playing it sees each other's sessions and nobody else's.
    /// </summary>
    internal static IResult CreateSession(
        RelaySessionRequest request,
        ClaimsPrincipal user,
        RelayHost host,
        RelaySettingsStore settings,
        RelayTicketService tickets,
        ILogger<RelayEndpoints> logger)
    {
        if (request is null || request.GameId == Guid.Empty)
            return TypedResults.Problem("A game id is required.", statusCode: StatusCodes.Status400BadRequest);

        // 503 rather than 404: the launcher reads both as "no relay", but the log can tell them apart
        if (host.State != RelayHostState.Running || host.TcpEndPoint is not { } endPoint)
            return TypedResults.Problem("The relay is not running.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var userName = user.FindFirstValue(ClaimTypes.Name) ?? user.Identity?.Name;

        // A Problem, not Forbid, which the server's cookie scheme would turn into a sign-in redirect
        if (string.IsNullOrWhiteSpace(userName))
            return TypedResults.Problem("The signed-in account has no user name.", statusCode: StatusCodes.Status403Forbidden);

        var userId = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        var network = NetworkFor(request.GameId);

        var issued = tickets.Issue(userId, userName, network, out var ticket);

        host.LabelNetwork(network, request.GameTitle);

        logger.LogDebug("Issued {User} a relay ticket for network {Network}", userName, network);

        return TypedResults.Ok(new RelaySession
        {
            Host = settings.Current.PublicHost,
            Port = endPoint.Port,
            Network = network,
            Ticket = ticket,
            ExpiresOn = issued.ExpiresOn,
        });
    }

    /// <summary>The relay network a game's players share.</summary>
    internal static string NetworkFor(Guid gameId) => gameId.ToString("N");
}
