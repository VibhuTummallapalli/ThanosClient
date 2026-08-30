# ThanosClient

Deployment for the Minecraft bots on **cosmicreborn.net** — two accounts, each running
[Minecraft Console Client](https://github.com/MCCTeam/Minecraft-Console-Client) (MCC) in
its own container, each bridged to its own Discord channel.

This repo is configuration and operational notes. It contains no client code.

## Why MCC, and not the client that used to live here

This repo previously held a hand-written C# protocol-47 client. It worked against a fake
server and against offline-mode servers, but it could never log into a real one:

Mojang manually allow-lists every new Azure application that talks to the Java Edition
game service APIs. Until an application is approved, the final hop of the sign-in chain
returns **`Invalid app registration`** while every earlier step — MSA device code, Xbox
Live, XSTS — succeeds, which makes it look like a bug in your XSTS payload for a long
time before you work out that it is a policy wall. The review form is
<https://aka.ms/mce-reviewappid> and submissions are reviewed weekly. The launcher's own
legacy client id is not a shortcut: the modern endpoint cannot resolve it at all, and its
legacy endpoint issues a device code and shows a real consent screen before refusing the
token exchange for anyone but the launcher.

MCC is already an approved application, so it signs in immediately. Everything the old
client did — chat relay, auto-relog, scripted commands, a Discord bridge — MCC does too.
The old code is preserved in git history (`git log -- src/`) if it is ever wanted.

## Layout

```
Dockerfile                    MCC image, built from the project's linux-x64 release
Dockerfile.limbo              NanoLimbo image -- the parking server, see below
docker-compose.yml            one service per account (mcc-1, mcc-2), plus limbo
MinecraftClient.example.ini   the settings that differ from MCC's defaults
limbo-settings.yml            NanoLimbo config, mounted read-only into the container
join.txt                      post-login script: move, then hop to alienplanet
data-1/  data-2/              per-account runtime state -- gitignored, see below
```

`data-N/` holds that account's filled-in `MinecraftClient.ini`, its `SessionCache.db`,
and its copy of `join.txt`. **It is gitignored and must stay that way**: the ini carries
the account password and the Discord bot token in plain text, and the session cache
carries a live Minecraft session. `chmod 600` the ini.

## Where it can run

It runs on a cloud VM today: a GCP `e2-micro` in `us-central1-a`, deploy directory
`/opt/mcc`. That is worth stating plainly because this file used to claim the opposite.

CosmicReborn does flag datacenter ranges as VPN/proxy, and the block was real: on
2026-08-29 a working container on that VM authenticated, joined, and was kicked inside a
second with *"Your IP is flagged as a VPN/Proxy"*, while the identical container joined
fine from the home connection minutes earlier. It is no longer blocked — the same VM, on
the same external IP, has since held a connection for 18 hours straight. Something changed
in between, and the likely cause is the whitelist below, but that has not been confirmed.

The server's VPN whitelist is conditional on enabling `/2fa` on the account. If a fresh
cloud host ever gets kicked this way, enable `/2fa` first rather than concluding the
provider's ranges are unusable.

Resource use is small: MCC sits well under the 320 MB cap in `docker-compose.yml`, even
with `TerrainAndMovements` on, and limbo idles around 84 MB against its 192 MB cap.

## First run

```sh
mkdir -p data-1
cp MinecraftClient.example.ini data-1/MinecraftClient.ini
cp join.txt data-1/
chmod 600 data-1/MinecraftClient.ini
```

Edit `data-1/MinecraftClient.ini`: set the account `Login`, the Discord bot `Token`, the
`ChannelId`, and the `OwnersIds` list. Then sign in once, interactively:

```sh
docker compose build
docker compose run --rm -it mcc-1
```

MCC prints a `microsoft.com/link` device code. Enter it, approve, and let it reach the
server once — that writes `SessionCache.db`, which carries the login across every later
restart. Stop it, then run it detached:

```sh
docker compose up -d
docker compose logs -f mcc-1
```

`docker compose run --rm -it` is the one place a pty is correct, because a human is typing
into it. The long-running services must not have one — see below.

Adding a second account is the same with `data-2` and `mcc-2`. Give it its own Discord
channel; two bridges posting into one channel is unreadable.

## Day-to-day

| Task | Command |
| --- | --- |
| Follow one bot | `docker compose logs -f mcc-1` |
| Type a command in | `docker attach mcc-1` (detach with `Ctrl-P Ctrl-Q`) |
| Restart one | `docker compose restart mcc-1` |
| Stop everything | `docker compose down` |
| Re-authenticate | delete `data-1/SessionCache.db`, then the interactive run above |

## Discord bridge

Whitelisted members drive the bot from its channel using MCC's own command set behind a
`.` prefix — **not** the `/mc` slash commands the old client registered. The ones that
matter:

| Command | Description |
| --- | --- |
| `.send <text>` | send chat, or a server command when it starts with `/` |
| `.send /2fa <code>` | clear the 2FA prompt (see below) |
| `.send /server alienplanet` | hop to the survival server by hand |
| `.connect limbo` | park the bot -- take the account off cosmicreborn |
| `.connect cosmic` | send it back to cosmicreborn |
| `.list` | players currently online |
| `.script join.txt` | re-run the login hop |
| `.reco` | reconnect |

**Access control is by Discord user id only.** MCC's bridge gates on `OwnersIds` and has
no role support, so the "pika friends" role had to be expanded into 14 explicit user ids
in each account's ini. Adding a person means editing both inis and restarting.

`Relay_All_Messages = true` relays system messages and join/leave notices, not just player
chat. Messages are batched on a 3-second interval, because one Discord message per chat
line hits the per-channel rate limit almost immediately on a busy server.

## Taking a bot offline

`.connect limbo` parks a bot, `.connect cosmic` sends it back, and both work from Discord.

The indirection is there because MCC's Discord bridge is a `ChatBot` living inside the
client, so it exists only while MCC is connected to *a* server. Quitting MCC to free the
account takes the bridge down with it, and then nothing is left listening to hear the
command to bring it back. So the bot moves rather than disconnects: `limbo` is a
[NanoLimbo](https://github.com/Nan1t/NanoLimbo) instance in this compose stack whose only
job is to hold a connection open. Parked there the account is genuinely off cosmicreborn —
free to log in on yourself, not holding a slot, not visible — while the bridge keeps
answering Discord.

Limbo has no authentication of any kind and is deliberately not published to the host, so
it is reachable only from inside the compose network.

**A park does not survive a container restart.** `/connect` writes the new server to MCC's
in-memory `InternalConfig`, never to `MinecraftClient.ini`, so `docker compose restart
mcc-1` or a host reboot puts the bot back on cosmicreborn by itself. That fails toward
"online", which is usually what you want — but if you parked the bot to play on the
account yourself, a reboot will take the account back.

Three smaller things:

- **The alias names come from the ini, not from MCC.** `cosmic` and `limbo` are keys in
  `[Main.Advanced.ServerList]`, and MCC matches them case-sensitively. They have to be
  aliases: MCC rejects any host with no dot in it that is not literally `localhost`, and
  applies that test to an alias's target too — which is why the limbo entry points at the
  dotted network alias `limbo.internal` from `docker-compose.yml` and not at the compose
  service name.
- **If limbo is down when you park, the bot gets stuck.** AutoRelog retries forever, so it
  will sit trying to reach a server that is not there. `docker compose restart mcc-1` gets
  it back, since a restart returns it to the ini's server.
- **`join.txt` runs on limbo too**, because the `ScriptScheduler` trigger fires on every
  login. Its `move` steps and its `/server alienplanet` are both no-ops there — limbo
  ignores chat — so it is log noise, not a problem. If it ever grates, MCC's `execif` can
  gate the hop on `MCC.GetServerHost() == "cosmicreborn.net"`.

Upgrading limbo works like MCC: `LIMBO_VERSION` is pinned in `Dockerfile.limbo` and the
resulting tag in `docker-compose.yml`, so bump both together.

## Server behaviour worth knowing

**Chat is gated on movement.** CosmicReborn refuses all chat and commands until the
character has moved. This is why `TerrainAndMovements` must be `true` and why `join.txt`
steps north and back before sending anything — with movement off, the `/server alienplanet`
hop can never fire, and the failure looks like the command being ignored.

**2FA is prompted occasionally on login.** While the account is unauthenticated the 2FA
plugin swallows every command and answers *"Unknown command"*, so a login hop that seems
to be silently failing usually means a 2FA prompt is waiting. MCC cannot generate TOTP;
enter the code by hand or through the bridge with `.send /2fa <code>`.

**Relogs are throttled.** "logging in too fast" is added to `Kick_Messages` so AutoRelog
treats it as retryable; it is not in MCC's default list.

## Never run MCC with `tty: true`

With a pty, MCC's stdin, stdout and stderr all point at the same terminal, and its echo
flag loops everything MCC prints back onto its own input. MCC reads that as typed lines
and sends every one that does not start with `/` to the server as chat. It emitted 26
garbage messages this way before it was caught; only CosmicReborn's move-before-you-chat
rule kept them out of public chat, which would otherwise have been a spam ban.

`stdin_open: false` alone does **not** fix it, because the pty is still shared. The
compose file uses `stdin_open: true` with `tty: false`: stdin is a plain pipe, so
`docker attach` still works for manual input, but nothing echoes back. MCC does not crash
headless, despite what its documentation implies.

## Upgrading MCC

`MCC_VERSION` is pinned in the `Dockerfile` and the resulting tag is pinned in
`docker-compose.yml`. Bump both together, rebuild, and start one account first — MCC
rewrites `MinecraftClient.ini` on startup when its config schema has changed, so keep a
copy of the old ini to diff against if a setting silently reverts.

## A note on use

Automated clients are against the rules on many servers. Check before pointing this at
one you do not run.
