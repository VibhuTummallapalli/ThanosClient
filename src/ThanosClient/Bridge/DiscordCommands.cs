using ThanosClient.Client;
using ThanosClient.Commands;
using ThanosClient.Config;
using ThanosClient.Protocol;

namespace ThanosClient.Bridge;

/// <summary>
/// The Minecraft-side implementation of each Discord slash command. Separate from
/// <see cref="CommandHandler"/> because that one writes to the console and returns
/// nothing, whereas Discord needs a string back to reply with.
///
/// Every method here assumes the caller has already passed the whitelist and the rate
/// limiter; authorisation is the bridge's job, not this class's.
/// </summary>
public sealed class DiscordCommands
{
    private readonly McClient _client;
    private readonly Settings _settings;
    private readonly Action _requestReconnect;

    public DiscordCommands(McClient client, Settings settings, Action requestReconnect)
    {
        _client = client;
        _settings = settings;
        _requestReconnect = requestReconnect;
    }

    /// <summary>
    /// Sends chat, or an in-game command when the text starts with a slash. Whitelisted
    /// users are permitted both, so the text goes through as typed - after sanitising,
    /// which the client does on the way out.
    /// </summary>
    public string Say(string message, string discordUserName)
    {
        string text = message.Trim();

        if (text.Length == 0) return "Nothing to send.";
        if (!_client.IsInGame) return "Not connected to the server right now.";

        bool isGameCommand = text.StartsWith('/');

        // Plain chat is attributed so players can see where it came from. Commands go
        // verbatim: a prefix would make them invalid.
        string outbound = isGameCommand
            ? text
            : _settings.Discord.GameChatPrefix + discordUserName + ": " + text;

        _client.SendChat(outbound);

        string echo = ChatSanitizer.Sanitize(outbound);
        return isGameCommand
            ? $"Ran in-game command: `{DiscordFormat.Escape(echo)}`"
            : $"Sent: `{DiscordFormat.Escape(echo)}`";
    }

    public string Status()
    {
        string state = _client.IsInGame
            ? "in game"
            : _client.IsConnected ? "connected, not yet in game" : "disconnected";

        var lines = new List<string>
        {
            $"**Account**  `{_client.Username}` ({(_settings.Account.IsOffline ? "offline" : "Microsoft")})",
            $"**Server**   `{_client.Host}:{_client.Port}` (protocol {Protocol47.Version}, {Protocol47.VersionName})",
            $"**State**    {state}",
        };

        if (_client.GameMode is not null) lines.Add($"**Gamemode** {_client.GameMode}");
        lines.Add($"**Health**   {_client.Health:0.#}/20");
        lines.Add($"**Players**  {_client.Players.Count}");

        string bots = _client.Bots.Count == 0
            ? "none"
            : string.Join(", ", _client.Bots.Select(b => b.Name + (b.Enabled ? "" : " (off)")));
        lines.Add($"**Bots**     {bots}");

        return string.Join('\n', lines);
    }

    public string List()
    {
        var players = _client.Players.All
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (players.Count == 0) return "No players in the tab list.";

        var rows = players.Select(p => $"{p.Name,-16} {p.Ping,4} ms  {Gamemode(p.Gamemode)}");
        string table = "```\n" + string.Join('\n', rows) + "\n```";

        return DiscordFormat.Truncate($"**{players.Count} player(s) online**\n{table}");
    }

    public string Position() =>
        _client.CurrentLocation is Location location
            ? $"Position: `{location}`"
            : "Position unknown; the server has not sent one yet.";

    public string Health() => $"Health: {_client.Health:0.#}/20";

    public string TabList()
    {
        string header = _client.Players.Header ?? "";
        string footer = _client.Players.Footer ?? "";

        if (header.Length == 0 && footer.Length == 0)
            return "The server has not sent a tab list header or footer.";

        return DiscordFormat.Truncate(DiscordFormat.Escape((header + "\n" + footer).Trim()));
    }

    public string Reconnect()
    {
        if (_client.IsConnected) _client.Disconnect("Reconnecting on request from Discord");
        _requestReconnect();
        return "Reconnecting...";
    }

    public string Disconnect()
    {
        if (!_client.IsConnected) return "Already disconnected.";

        _client.Disconnect("Disconnected from Discord");
        return "Disconnected. Use `/mc reconnect` to rejoin.";
    }

    public string Bots(string? action, string? name)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            if (_client.Bots.Count == 0) return "No bots are loaded.";

            var rows = _client.Bots.Select(b => $"{b.Name,-14} {(b.Enabled ? "on" : "off")}");
            return "```\n" + string.Join('\n', rows) + "\n```";
        }

        if (string.IsNullOrWhiteSpace(name)) return "Which bot? Pass a name as well.";

        ThanosClient.Bots.ChatBot? target = _client.Bots
            .FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        if (target is null) return $"No bot called `{DiscordFormat.Escape(name)}`.";

        target.Enabled = string.Equals(action, "on", StringComparison.OrdinalIgnoreCase);
        return $"`{target.Name}` is now {(target.Enabled ? "on" : "off")}.";
    }

    public async Task<string> PingAsync(string? address)
    {
        string host = _client.Host;
        ushort port = _client.Port;

        if (!string.IsNullOrWhiteSpace(address) && !CommandHandler.TryParseAddress(address, out host, out port))
            return $"Could not parse `{DiscordFormat.Escape(address)}` as host or host:port.";

        if (string.IsNullOrEmpty(host)) return "No server to ping.";

        try
        {
            ServerStatus status = await ServerPing.QueryAsync(host, port);

            var lines = new List<string>
            {
                $"**{DiscordFormat.Escape(host)}:{port}**",
                $"version  {DiscordFormat.Escape(status.VersionName)} (protocol {status.ProtocolVersion})",
                $"players  {status.OnlinePlayers}/{status.MaxPlayers}",
                $"latency  {status.LatencyMs} ms",
            };

            if (status.Description.Length > 0)
                lines.Add($"motd     {DiscordFormat.Escape(status.Description.Replace("\n", " | "))}");

            if (status.ProtocolVersion > 0 && status.ProtocolVersion != Protocol47.Version)
                lines.Add($"note: this server speaks protocol {status.ProtocolVersion}, the client only speaks {Protocol47.Version}");

            return DiscordFormat.Truncate(string.Join('\n', lines));
        }
        catch (Exception ex)
        {
            return $"Ping failed: {DiscordFormat.Escape(ex.Message)}";
        }
    }

    private static string Gamemode(int gamemode) => gamemode switch
    {
        0 => "survival",
        1 => "creative",
        2 => "adventure",
        3 => "spectator",
        _ => "",
    };
}
