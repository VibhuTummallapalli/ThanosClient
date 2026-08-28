# ThanosClient

A headless console client for **Minecraft: Java Edition 1.8.9** (protocol 47).

It logs into a server with a real Microsoft account, joins the world, and gives you chat
in a terminal — no game window, no rendering, no world download. Useful for staying
connected, relaying chat, scripting simple bots, or poking at the protocol.

It can also be [driven from Discord](#discord-bridge), so a whole server can share one
Minecraft connection.

```
$ ThanosClient play.example.com

ThanosClient - console client for Minecraft 1.8.9 (protocol 47)

Microsoft sign-in required.
  1. Open  https://www.microsoft.com/link
  2. Enter code  H8KP2QXV
Waiting for approval...

Signed in as Steve.
Connecting to play.example.com:25565 ...
Logged in as Steve (069a79f4-44e9-4726-a5be-fca90e38aaf5)
Joined the game as Steve (survival, default).
[21:04:11] <Alex> anyone online?
> hello
[21:04:19] <Steve> hello
> .list
2 player(s) online:
  Alex               48 ms  survival
  Steve              31 ms  survival
```

## Requirements

- .NET 9 SDK (or runtime, to run a published build)
- A Microsoft account that owns Minecraft: Java Edition, for online-mode servers

## Build and run

```sh
dotnet build
dotnet run --project src/ThanosClient -- play.example.com
```

Publish a single executable:

```sh
dotnet publish src/ThanosClient -c Release -r win-x64
```

## Command line

```
ThanosClient [host[:port]] [options]

  -s, --host <host[:port]>  server to join
  -p, --port <port>         server port (default 25565)
  -c, --config <file>       config file (default thanosclient.json)
      --offline <name>      join as an offline-mode player instead of signing in
      --login               ignore the cached session and sign in again
      --auth-only           sign in, save the session, and exit (for headless hosts)
      --ping                print the server list ping and exit
      --debug               log every packet id received
      --no-color            disable ANSI colours
  -h, --help                this message
```

`--ping` is the quickest way to check a server before joining, and it reports the
server's real protocol version:

```sh
dotnet run --project src/ThanosClient -- --ping play.example.com
```

## Signing in

Online-mode servers need a genuine Mojang session, so the client runs the full chain:

```
MSA device code -> Xbox Live -> XSTS -> Minecraft services -> profile -> sessionserver join
```

You get a code to type at `microsoft.com/link`; there is no embedded browser. The
resulting session is cached (see below) so the prompt only appears once, and it is
refreshed silently with the stored refresh token when it expires.

### You need your own Azure application, and Mojang has to approve it

This is the fiddliest part of running the client, and there is no way around it.

**1. Register an Azure application.** Azure portal → *App registrations* → *New
registration*. Set supported account types to **Personal Microsoft accounts only**, and
leave the redirect URI blank — the device code flow never redirects. Then under
*Authentication* → *Advanced settings*, turn on **Allow public client flows**; without it
the token request fails with `AADSTS7000218`. Put the Application (client) id in
`account.msClientId`.

Registering anything requires an Azure **directory**. A plain Microsoft account does not
have one, and the portal refuses with *"The ability to create applications outside of a
directory has been deprecated."* Signing up for a free Azure account provisions one.

**2. Get the application allow-listed by Mojang.** Mojang now manually reviews every new
application that talks to the Java Edition game service APIs. Until yours is approved the
final hop fails with **`Invalid app registration`** — while every step before it,
including Xbox Live and XSTS, succeeds. Apply at <https://aka.ms/mce-reviewappid>;
submissions are reviewed weekly.

**The launcher's own client id (`00000000402b5328`) is not a shortcut.** It is a legacy
Live Connect id, not an Azure one: the modern endpoint cannot resolve it at all
(`AADSTS700016`), and its own legacy endpoint will issue a device code and show a real
consent screen before refusing the token exchange for anyone but the launcher.

#### Sign-in errors, and what they actually mean

| Error | Cause |
| --- | --- |
| `AADSTS700016: Application ... was not found` | `msClientId` is not a registered Azure application id |
| `AADSTS7000218` | *Allow public client flows* is off on the registration |
| `invalid_grant` / *"user could not be authenticated"* | the legacy endpoint refusing a non-launcher caller |
| `Xbox Live authentication failed (HTTP 400)` | malformed XBL request — the payload must be exact PascalCase |
| `Invalid app registration` | the application is not on Mojang's allow list yet |

**Offline mode.** For a cracked or LAN server, set `account.mode` to `"offline"` or pass
`--offline <name>`. No account is involved. Connecting this way to a server that
*requires* authentication fails with a clear message rather than a protocol error.

### Where tokens are stored

The cached session lives at `%APPDATA%\ThanosClient\session.json` (override with
`account.sessionCachePath`). It contains a live Minecraft access token and a Microsoft
refresh token **in plain text**, exactly like the official launcher's `launcher_accounts.json`.
Anyone who can read that file can use your account. Delete it, or run `--login`, to
invalidate the local copy.

## Console commands

Lines starting with `.` (configurable via `console.commandPrefix`) are handled by the
client. Everything else is sent to the server as chat, so server commands like
`/tp` and `/msg` work exactly as they normally do.

| Command | Description |
| --- | --- |
| `.help` | list all commands |
| `.status` | connection, account and bot summary |
| `.list` | players currently online, with ping and gamemode |
| `.tab` | tab list header and footer |
| `.pos` | your current position |
| `.health` | your current health |
| `.say <message>` | send chat explicitly |
| `.ping [host[:port]]` | server list ping, defaults to the current server |
| `.bots` | list bots; `.bots on\|off <name>` toggles one |
| `.debug` | toggle packet logging |
| `.reconnect` | drop and rejoin |
| `.disconnect` | leave but keep the client running |
| `.quit` | leave and exit |

Up and down arrows scroll through input history. Incoming chat never mangles a line
you are halfway through typing.

## Configuration

`thanosclient.json` is created on first run. Command line arguments override it.

```jsonc
{
  "server": {
    "host": "play.example.com",
    "port": 25565,
    "clientBrand": "vanilla",       // sent as the MC|Brand plugin message
    "connectTimeoutSeconds": 15
  },
  "account": {
    "mode": "microsoft",            // or "offline"
    "offlineUsername": "Player",
    "msClientId": "00000000-0000-0000-0000-000000000000",  // yours; see "Signing in"
    "sessionCachePath": ""          // empty means the per-user default
  },
  "console": {
    "colors": true,
    "commandPrefix": ".",
    "prompt": "> ",
    "debugPackets": false,
    "timestamps": true
  },
  "bots": {
    "antiAfk":   { "enabled": false, "intervalSeconds": 60, "moveSlightly": true },
    "autoRelog": { "enabled": false, "delaySeconds": 10, "maxAttempts": 5,
                   "ignoreKickWords": ["banned", "whitelist"] },
    "chatLog":   { "enabled": false, "file": "logs/chat.log", "includeTimestamps": true },
    "autoRespond": {
      "enabled": false,
      "cooldownSeconds": 5,
      "rules": [
        { "match": "^<(\w+)> !ping$", "send": "pong, $1" }
      ]
    }
  }
}
```

## Bots

Bots are small plugins that react to client events. Four ship with the client:

- **chatLog** — appends every chat line to a file, formatting stripped.
- **antiAfk** — swings the arm and nudges the view on a timer.
- **autoRelog** — reconnects after an unexpected disconnect, with a delay and an attempt
  limit. Kicks whose reason matches `ignoreKickWords` (a ban, for example) are treated as
  permanent and are not retried.
- **autoRespond** — replies to chat matching a regular expression. `$1`, `$2` … expand to
  capture groups, and a shared cooldown stops a chatty trigger from spam-kicking you.

Writing your own means subclassing `ChatBot` and adding it in `Program.BuildBots`:

```csharp
public sealed class GreeterBot : ChatBot
{
    public override void OnPlayerJoin(PlayerInfo player) => SendChat($"welcome, {player.Name}");
}
```

The hooks are `OnJoinedGame`, `OnChat`, `OnPlayerJoin`, `OnPlayerLeave`, `OnUpdate`
(about ten times a second), and `OnDisconnect`.

## Discord bridge

The client can be driven from Discord, so a whole server can share one Minecraft
connection. In-game chat is relayed into a channel, and whitelisted members run
`/mc` slash commands to talk and control the client.

### Setting it up

1. Create an application at <https://discord.com/developers/applications>, add a bot,
   and copy its token.
2. Invite it with the `bot` and `applications.commands` scopes and the **Send Messages**
   permission.
3. Turn on Developer Mode in Discord (Settings -> Advanced) so you can right-click to
   **Copy ID** for your server, channel and roles.
4. Put the token in the `THANOSCLIENT_DISCORD_TOKEN` environment variable — preferred, so
   it stays out of the config file — or in `discord.token`.
5. Fill in `guildId`, `channelIds`, and at least one entry in `allowedRoleIds` or
   `allowedUserIds`, then set `enabled` to `true`.

```jsonc
"discord": {
  "enabled": true,
  "token": "",                        // prefer THANOSCLIENT_DISCORD_TOKEN
  "guildId": 123456789012345678,      // 0 registers commands globally (slow to appear)
  "channelIds": [123456789012345678], // where it relays and listens; empty means anywhere
  "allowedRoleIds": [123456789012345678],
  "allowedUserIds": [],
  "relayGameChat": true,
  "relayJoinLeave": true,
  "relayConnectionEvents": true,
  "relayDiscordMessages": false,      // see "Chat relay" below
  "relayIntervalSeconds": 2,
  "perUserCooldownSeconds": 2,
  "maxCommandsPerMinute": 30,
  "gameChatPrefix": "[Discord] "
}
```

### Who can use it

**Every command requires the whitelist.** A member passes if they hold a role in
`allowedRoleIds` or their id is in `allowedUserIds`; everyone else is refused, including
server administrators. This fails closed on purpose — with both lists empty the bridge
starts, relays chat, and accepts nothing, and it says so at startup.

Whitelisted members can also run **in-game commands**: `/mc say /tp me somewhere` is sent
to the server verbatim. Treat the whitelist as equivalent to handing someone the account,
and keep in mind what that account can do on the server it is logged into. Plain chat is
prefixed with the sender's Discord name; in-game commands are not, because a prefix would
break them.

Two throttles protect the account from being spam-kicked: a per-user cooldown and a
global ceiling per minute.

### Commands

| Command | Description |
| --- | --- |
| `/mc say <message>` | send chat, or an in-game command when it starts with `/` |
| `/mc status` | connection, account and bot summary |
| `/mc list` | players currently online |
| `/mc pos` | the client's position |
| `/mc health` | the client's health |
| `/mc tab` | tab list header and footer |
| `/mc ping [address]` | server list ping |
| `/mc bots [on\|off] [name]` | list bots, or toggle one |
| `/mc reconnect` | drop the connection and rejoin |
| `/mc disconnect` | leave the server, client keeps running |

Refusals are ephemeral, so a denied command is not broadcast to the channel. There is
deliberately no `/mc quit`: stopping the process would need someone at the console to
start it again.

### Chat relay

Game chat is batched and posted every `relayIntervalSeconds`, because one Discord message
per chat line hits the per-channel rate limit almost immediately on a busy server.
Outbound text is escaped and posted with mentions disabled, so **nothing a player types
in game can ping your Discord server** — someone typing `@everyone` in Minecraft produces
inert text.

`relayDiscordMessages` sends ordinary channel messages in game, so the channel reads like
a chat bridge instead of needing `/mc say` every time. It is off by default because it
needs the privileged **Message Content** intent enabled for your application in the
Developer Portal. Senders still have to pass the whitelist.

Everything sent to the server is filtered to the characters vanilla 1.8 accepts. Discord
text is full of newlines, emoji and control characters, and a section sign alone is
enough to get the client kicked for "Illegal characters in chat".

## Running it 24/7

The client is built to run unattended: it reconnects after drops, refreshes its login
before every connection attempt, and does not need a terminal attached.

Resources are tiny — no world model, no rendering — so the smallest box any provider
sells is enough. 1 vCPU and 512 MB is generous.

### Docker

Sign in once interactively, then leave it running detached:

```sh
mkdir -p data
docker compose build

# One-time: prints a device code, saves data/session.json, exits.
docker compose run --rm -it thanosclient --auth-only

docker compose up -d
docker compose logs -f
```

`./data` holds `thanosclient.json`, `session.json` and `logs/`, so a rebuild or a host
reboot loses nothing. The image sets `THANOSCLIENT_SESSION_PATH=/data/session.json`, so the
cached token lands on the volume without any config change — otherwise it would default to
a path under `HOME` inside the image and be discarded on every rebuild.

On a **Linux** host, the container runs as uid 1000 and the bind mount keeps the host's
ownership, so create the directory with a matching owner before the first run:

```sh
mkdir -p data && sudo chown -R 1000:1000 data
```

Skip that and the first run fails with permission errors writing the config. Docker Desktop
on Windows and macOS handles this for you.

The Discord token comes from the environment. Put it in a `.env` file next to
`docker-compose.yml` (gitignored):

```
THANOSCLIENT_DISCORD_TOKEN=...
```

### systemd

If you would rather not use containers:

```sh
dotnet publish src/ThanosClient -c Release -o /opt/thanosclient
sudo -u thanos /opt/thanosclient/ThanosClient --auth-only   # once, interactively
```

```ini
# /etc/systemd/system/thanosclient.service
[Unit]
Description=ThanosClient
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=thanos
WorkingDirectory=/var/lib/thanosclient
ExecStart=/usr/bin/dotnet /opt/thanosclient/ThanosClient.dll
Environment=THANOSCLIENT_DISCORD_TOKEN=...
Restart=always
RestartSec=30
StandardInput=null

[Install]
WantedBy=multi-user.target
```

Put the token in a root-owned `EnvironmentFile=` instead of the unit if other users can
read it. Then `systemctl enable --now thanosclient` and `journalctl -u thanosclient -f`.

### What to expect

Sign-in is the only interactive step, and only the first time. After that the stored
Microsoft refresh token renews the login on its own — including on reconnects days later,
which is the case that matters: a Minecraft access token lasts about a day, and without
that refresh a long-running bot would be rejected by the session server on its first
reconnect after 24 hours.

If the refresh ever fails (the account changed its password, or Microsoft invalidated the
token), the log says so and asks for `--auth-only` on that host. Nothing recovers from
that automatically, by design — it needs a human at a browser.

The console still works if you attach a terminal, and is simply idle when you do not.

## What it implements

Full login: handshake, login start, AES-128/CFB8 encryption with the RSA key exchange and
the session-server join, zlib packet compression, and login success.

Addresses are resolved through Minecraft's SRV convention (`_minecraft._tcp.<host>`) like
the vanilla client, so a domain whose bare A record points at a website works. An explicit
port or an IP literal skips the lookup. The handshake still names the address you typed
rather than the SRV target, so proxy forced-host routing keeps working.

In the play state it handles keep-alives, join game, chat in both directions, position
and look (including the relative-flag arithmetic and the teleport echo), health and
automatic respawn, the tab list, and disconnects. Also implements the server list ping.

**Not implemented:** chunks and block data, entities, inventory, and everything else that
needs a world model. Unrecognised packets are skipped safely, so the client stays
connected regardless — it simply does not know what is around it.

**One protocol version.** Protocol 47 only, which is 1.8 through 1.8.9. Packet ids moved
in later versions, so a 1.9+ server will refuse the connection; `--ping` will tell you
which protocol a server actually speaks.

## Tests

```sh
dotnet run --project tests/ThanosClient.Tests
```

179 checks covering VarInt and numeric encoding, the signed server-hash digest against
the three published vectors, the CFB8 cipher against the platform's own AES-CFB8, packet
framing across every compression and encryption combination, chat component parsing,
address parsing, the SRV skip conditions, and session-path precedence.

The Discord bridge is covered where it matters and can be tested without a gateway
connection: the whitelist decision (including that an unconfigured whitelist denies
everyone, and that the channel check runs first), both rate limits against a fake clock,
outbound chat sanitising, mention and markdown escaping, and relay batching.

The end-to-end tests run a fake protocol-47 server on loopback and take the real client
through login, join, chat both ways, and keep-alive — plain, compressed, and encrypted
(with a stubbed session server, so no network and no account are needed).

## A note on use

Automated clients are against the rules on many servers. Check before pointing this at
one you do not run.
