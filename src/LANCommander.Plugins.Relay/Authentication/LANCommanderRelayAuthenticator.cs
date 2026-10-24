using LANCommander.Interposer.Relay;
using LANCommander.Plugins.Relay.Storage;

namespace LANCommander.Plugins.Relay.Authentication;

/// <summary>
/// Admits relay clients according to the plugin's settings. When sign-in is required, a client must
/// present a relay ticket this server issued, and is placed in the network the ticket names. When it
/// is not, everyone is let in, but a client with a valid ticket is still placed by it and shown under
/// its account.
/// </summary>
/// <remarks>
/// The account is attached to the client as <see cref="RelayClientInfo.State"/>, as a
/// <see cref="RelayUser"/>. The player name is left as the client gave it: it is the name the game
/// itself uses, so it matches what the other players see in game.
/// </remarks>
public sealed class LANCommanderRelayAuthenticator(RelaySettingsStore settings, RelayTicketService tickets) : IRelayAuthenticator
{
    public ValueTask<RelayAuthenticationResult> AuthenticateAsync(
        RelayAuthenticationRequest request, CancellationToken cancellationToken)
    {
        var required = settings.Current.RequireAuthentication;

        if (string.IsNullOrWhiteSpace(request.Ticket))
        {
            return ValueTask.FromResult(required
                ? RelayAuthenticationResult.Deny("this relay requires players to be signed in to LANCommander; no ticket was given")
                : RelayAuthenticationResult.Allow());
        }

        var ticket = tickets.Validate(request.Ticket);

        if (ticket is not null)
        {
            // The ticket, not the client, decides which network it joins
            return ValueTask.FromResult(RelayAuthenticationResult.Allow(
                network: ticket.Network,
                state: new RelayUser(ticket.UserId, ticket.UserName)));
        }

        return ValueTask.FromResult(required
            ? RelayAuthenticationResult.Deny("the ticket is not valid or has expired; start the game from the LANCommander launcher again")
            : RelayAuthenticationResult.Allow());
    }
}
