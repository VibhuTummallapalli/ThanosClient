using ThanosClient.Client;
using ThanosClient.Config;
using ThanosClient.Protocol;
using ThanosClient.Terminal;

namespace ThanosClient.Commands;

/// <summary>
/// Client-side commands. Anything typed without the configured prefix is chat and goes
/// straight to the server, so server commands like /tp still work as they normally do.
/// </summary>
public sealed class CommandHandler
{
    private readonly McClient _client;
    private readonly Settings _settings;
    private readonly Action _requestQuit;
    private readonly Action _requestReconnect;

    public CommandHandler(McClient client, Settings settings, Action requestQuit, Action requestReconnect)
    {
        _client = client;
        _settings = settings;
        _requestQuit = requestQuit;
        _requestReconnect = requestReconnect;
    }

    /// <summary>Handles one line of console input.</summary>
    public void Handle(string line)
    {
        string prefix = _settings.Console.CommandPrefix;

        if (string.IsNullOrEmpty(prefix) || !line.StartsWith(prefix, StringComparison.Ordinal))
        {
            _client.SendChat(line);
            return;
        }

        string body = line[prefix.Length..].Trim();
        if (body.Length == 0) return;

        string[] parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1].Trim() : "";

        switch (command)
        {
            case "help" or "?":
                ShowHelp();
                break;

            case "quit" or "exit":
                _requestQuit();
                break;

            case "reconnect":
                ConsoleIO.WriteInfo("Reconnecting...");
                _requestReconnect();
                break;

            case "disconnect":
                _client.Disconnect("Disconnected by user");
                break;

            case "say":
                if (argument.Length == 0) ConsoleIO.WriteWarning($"Usage: {prefix}say <message>");
                else _client.SendChat(argument);
                break;

            case "list" or "players":
                ListPlayers();
                break;

            case "pos" or "where":
                ConsoleIO.WriteInfo(_client.CurrentLocation is Location location
                    ? $"Position: {location}"
                    : "Position unknown; the server has not sent one yet.");
                break;

            case "health":
                ConsoleIO.WriteInfo($"Health: {_client.Health:0.#}/20");
                break;

            case "tab":
                ShowTabList();
                break;

            case "bots":
                HandleBots(argument, prefix);
                break;

            case "ping":
                _ = PingAsync(argument);
                break;

            case "debug":
                _settings.Console.DebugPackets = !_settings.Console.DebugPackets;
                ConsoleIO.WriteInfo($"Packet debugging {(_settings.Console.DebugPackets ? "on" : "off")}.");
                break;

            case "status":
                ShowStatus();
                break;

            default:
                ConsoleIO.WriteWarning($"Unknown command \"{command}\". Try {prefix}help.");
                break;
        }
    }

    private void ShowHelp()
    {
        string p = _settings.Console.CommandPrefix;
        ConsoleIO.WriteLine("");
        ConsoleIO.WriteInfo("Client commands:");
        ConsoleIO.WriteLine($"  {p}help                 this list");
        ConsoleIO.WriteLine($"  {p}status               connection, account and bot summary");
        ConsoleIO.WriteLine($"  {p}list                 players currently online");
        ConsoleIO.WriteLine($"  {p}tab                  tab list header and footer");
        ConsoleIO.WriteLine($"  {p}pos                  your current position");
        ConsoleIO.WriteLine($"  {p}health               your current health");
        ConsoleIO.WriteLine($"  {p}say <message>        send chat explicitly");
        ConsoleIO.WriteLine($"  {p}ping [host[:port]]   server list ping, defaults to the current server");
        ConsoleIO.WriteLine($"  {p}bots                 list bots; {p}bots on|off <name> to toggle");
        ConsoleIO.WriteLine($"  {p}debug                toggle packet logging");
        ConsoleIO.WriteLine($"  {p}reconnect            drop and rejoin");
        ConsoleIO.WriteLine($"  {p}disconnect           leave but stay running");
        ConsoleIO.WriteLine($"  {p}quit                 leave and exit");
        ConsoleIO.WriteLine("");
        ConsoleIO.WriteInfo("Anything not starting with the prefix is sent to the server as chat.");
        ConsoleIO.WriteLine("");
    }

    private void ListPlayers()
    {
        var players = _client.Players.All.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if (players.Count == 0)
        {
            ConsoleIO.WriteInfo("No players in the tab list.");
            return;
        }

        ConsoleIO.WriteInfo($"{players.Count} player(s) online:");
        foreach (PlayerInfo player in players)
            ConsoleIO.WriteLine($"  {player.Name,-16} {player.Ping,4} ms  {DescribeGamemode(player.Gamemode)}");
    }

    private static string DescribeGamemode(int gamemode) => gamemode switch
    {
        0 => "survival",
        1 => "creative",
        2 => "adventure",
        3 => "spectator",
        _ => "",
    };

    private void ShowTabList()
    {
        if (!string.IsNullOrWhiteSpace(_client.Players.Header)) ConsoleIO.WriteLine(_client.Players.Header!);
        if (!string.IsNullOrWhiteSpace(_client.Players.Footer)) ConsoleIO.WriteLine(_client.Players.Footer!);
        if (string.IsNullOrWhiteSpace(_client.Players.Header) && string.IsNullOrWhiteSpace(_client.Players.Footer))
            ConsoleIO.WriteInfo("The server has not sent a tab list header or footer.");
    }

    private void ShowStatus()
    {
        ConsoleIO.WriteInfo($"Account   {_client.Username} ({(_settings.Account.IsOffline ? "offline" : "Microsoft")})");
        ConsoleIO.WriteInfo($"Server    {_client.Host}:{_client.Port} (protocol {Protocol47.Version}, {Protocol47.VersionName})");
        ConsoleIO.WriteInfo($"State     {(_client.IsInGame ? "in game" : _client.IsConnected ? "connected" : "disconnected")}");
        if (_client.GameMode is not null) ConsoleIO.WriteInfo($"Gamemode  {_client.GameMode}");
        ConsoleIO.WriteInfo($"Players   {_client.Players.Count}");

        string bots = _client.Bots.Count == 0
            ? "none"
            : string.Join(", ", _client.Bots.Select(b => b.Name + (b.Enabled ? "" : " (off)")));
        ConsoleIO.WriteInfo($"Bots      {bots}");
    }

    private void HandleBots(string argument, string prefix)
    {
        if (argument.Length == 0)
        {
            if (_client.Bots.Count == 0)
            {
                ConsoleIO.WriteInfo("No bots are loaded.");
                return;
            }

            foreach (Bots.ChatBot bot in _client.Bots)
                ConsoleIO.WriteLine($"  {bot.Name,-14} {(bot.Enabled ? "on" : "off")}");
            return;
        }

        string[] parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || (parts[0] != "on" && parts[0] != "off"))
        {
            ConsoleIO.WriteWarning($"Usage: {prefix}bots on|off <name>");
            return;
        }

        Bots.ChatBot? target = _client.Bots
            .FirstOrDefault(b => string.Equals(b.Name, parts[1], StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            ConsoleIO.WriteWarning($"No bot called \"{parts[1]}\".");
            return;
        }

        target.Enabled = parts[0] == "on";
        ConsoleIO.WriteInfo($"{target.Name} is now {(target.Enabled ? "on" : "off")}.");
    }

    private async Task PingAsync(string argument)
    {
        string host = _client.Host;
        ushort port = _client.Port;

        if (argument.Length > 0 && !TryParseAddress(argument, out host, out port))
        {
            ConsoleIO.WriteWarning($"Could not parse \"{argument}\" as host or host:port.");
            return;
        }

        if (string.IsNullOrEmpty(host))
        {
            ConsoleIO.WriteWarning("No server to ping; pass a host.");
            return;
        }

        try
        {
            ServerStatus status = await ServerPing.QueryAsync(host, port);
            ConsoleIO.WriteInfo($"{host}:{port}");
            ConsoleIO.WriteLine($"  version   {status.VersionName} (protocol {status.ProtocolVersion})");
            ConsoleIO.WriteLine($"  players   {status.OnlinePlayers}/{status.MaxPlayers}");
            ConsoleIO.WriteLine($"  latency   {status.LatencyMs} ms");
            if (status.Description.Length > 0)
                ConsoleIO.WriteLine($"  motd      {status.Description.Replace("\n", " | ")}");

            if (status.ProtocolVersion != Protocol47.Version && status.ProtocolVersion > 0)
                ConsoleIO.WriteWarning(
                    $"This server speaks protocol {status.ProtocolVersion}; this client only speaks " +
                    $"{Protocol47.Version} ({Protocol47.VersionName}).");
        }
        catch (Exception ex)
        {
            ConsoleIO.WriteError($"Ping failed: {ex.Message}");
        }
    }

    /// <summary>Parses host, host:port, or an IPv6 literal in brackets.</summary>
    public static bool TryParseAddress(string input, out string host, out ushort port)
    {
        host = input.Trim();
        port = 25565;

        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            if (close < 0) return false;

            string address = host[1..close];
            string rest = host[(close + 1)..];
            host = address;
            return rest.Length == 0 || (rest[0] == ':' && ushort.TryParse(rest[1..], out port));
        }

        int colon = host.LastIndexOf(':');
        if (colon < 0) return host.Length > 0;

        string portText = host[(colon + 1)..];
        if (!ushort.TryParse(portText, out port)) return false;

        host = host[..colon];
        return host.Length > 0;
    }
}
