# LANCommander Relay

A LANCommander server plugin that hosts the [Interposer](https://github.com/LANCommander/LANCommander.Interposer)'s
network relay. When enabled, games running under Interposer can connect to the relay to play over the network. It works by hooking standard Windows networking APIs linking your game to other players without the requirement of a standard VPN.

## How players connect

The LANCommander launcher on Windows does it by itself. When a player starts a game that has a **LAN**
multiplayer mode over **TCP/IP** or **IPX**, the launcher:

1. asks this plugin for a relay session (`POST /api/Plugins/Relay/Sessions`, as the signed-in player);
2. gets back where the relay listens, the game's relay network, and a ticket;
3. starts the game under the Interposer with those settings, injecting it or, for a game that already
   carries the Interposer as a proxy DLL, handing them to the proxy. 32-bit and 64-bit games both work.

Everyone playing the same game lands in the same network, one per game, and sees each other's sessions. Players can turn the relay off with `NetworkRelay.Enabled:
false` in the launcher's `Settings.yml`.

## What it adds

**Settings → Relay** (`/Plugins/Relay`), administrators only:

- **Overview**: whether the relay is listening, uptime, players connected, networks, peak players,
  sessions, refusals and traffic. Below that, the connected players (the game, its network, virtual
  address, where they connect from, UDP or TCP path, traffic, how long they have been connected, the
  LANCommander account they signed in as) with a button to disconnect each one. Then a log of the
  relay's recent activity: starts, joins, departures, refusals and errors. The page updates live.
- **Settings**:
  - **Enabled**: the relay is off until an administrator turns it on.
  - **Port**: the TCP port games connect to, and the UDP port datagrams use. Default `41300`.
  - **Use UDP**: turn off where the UDP port cannot be opened; everything then travels over TCP.
  - **Require sign-in**: see [Authentication](#authentication). On by default.
  - **Players per network**.
  - **Public address**: the host games connect to, when it is not the one the launcher reaches the server
    at, such as behind a reverse proxy that only carries HTTP.

  Saving restarts the relay. Connected games reconnect by themselves and ask for their old virtual
  addresses back.

**API**: `POST /api/Plugins/Relay/Sessions`, for any signed-in user, through LANCommander's plugin endpoint
support. It returns `503` while the relay is not running.

Settings and the ticket signing key are stored in the plugin's own folder, in `Plugins/LANCommander.Plugins.Relay/Data/`.
The activity log is kept in memory (the latest 500 entries); everything also goes to the server's own log.

## Authentication

With **Require sign-in** on, the relay only admits a game that presents a **relay ticket** this server
issued. The launcher gets one per launch from the sessions endpoint, as the signed-in player. A ticket:

- is signed (HMAC-SHA256) with a key only this server has, kept in `Data/ticket.key`. Deleting
  the key and restarting the server revokes every ticket issued so far;
- names the player's account and **one network**, the game's, and the relay puts the game there whatever
  network it asked for;
- expires after **24 hours**, long enough for a session, since the Interposer reconnects with the ticket
  it started with;
- is good for nothing but the relay. It is not the player's access token, so one seen on the wire cannot
  be used against the server's API.

Relay traffic, ticket included, **is not encrypted**. Anyone who can watch a player's traffic could reuse their
ticket to join that game's network, as them, until it expires.

With sign-in not required, anyone who can reach the port may join, into any network. A game with a valid
ticket is still placed in its ticket's network and shown under its account.

A ticket is checked when a game connects, including each reconnect, so a player removed from the server
can rejoin until their ticket expires. **Disconnect** drops a game, but it reconnects with the same ticket;
to shut everyone out at once, delete the key and restart the server.

## Installing

On a LANCommander server (2.2.0 or later), open **Settings → Plugins**. Find Relay on the **Browse**
tab and install it, or download the `.nupkg` attached to a
[release](https://github.com/LANCommander/LANCommander.Plugins.Relay/releases) and upload it on the
**Installed** tab. The plugin is installed when the server restarts.

## Hosting notes

- Open the relay port for **both TCP and UDP** in the server's firewall, and forward both if players
  connect from outside your network. In Docker, publish both: `-p 41300:41300/tcp -p 41300:41300/udp`.
- Every network hands out addresses from `10.13.37.0/24`; networks are isolated from each other.
