using System.Collections.Concurrent;
using ThanosClient.Bots;
using ThanosClient.Client;
using ThanosClient.Config;

namespace ThanosClient.Bridge;

/// <summary>
/// Collects in-game events for the Discord side. It is an ordinary bot, so it sees
/// everything the client sees; the bridge drains the queue on a timer and posts one
/// batched message rather than one per chat line, which would hit Discord's per-channel
/// rate limit almost immediately on a busy server.
/// </summary>
public sealed class GameChatRelay : ChatBot
{
    private readonly DiscordSettings _settings;
    private readonly ConcurrentQueue<string> _pending = new();

    /// <summary>Cap on the backlog, so a disconnected bridge cannot grow without bound.</summary>
    private const int MaxQueued = 500;

    public override string Name => "discord";

    public GameChatRelay(DiscordSettings settings) => _settings = settings;

    public override void OnChat(string text, string rawJson, ChatPosition position)
    {
        if (!_settings.RelayGameChat || position == ChatPosition.ActionBar) return;
        Enqueue(DiscordFormat.Escape(text));
    }

    public override void OnPlayerJoin(PlayerInfo player)
    {
        if (!_settings.RelayJoinLeave) return;
        Enqueue($"-> {DiscordFormat.Escape(player.Name)} joined");
    }

    public override void OnPlayerLeave(PlayerInfo player)
    {
        if (!_settings.RelayJoinLeave) return;
        Enqueue($"<- {DiscordFormat.Escape(player.Name)} left");
    }

    public override void OnJoinedGame()
    {
        if (!_settings.RelayConnectionEvents) return;
        Enqueue($"**Connected** to `{Client.Host}:{Client.Port}` as `{Client.Username}`.");
    }

    public override bool OnDisconnect(DisconnectReason reason, string message)
    {
        if (_settings.RelayConnectionEvents)
        {
            string label = reason switch
            {
                DisconnectReason.InGameKick => "Kicked",
                DisconnectReason.LoginFailed => "Login failed",
                DisconnectReason.ConnectionLost => "Connection lost",
                _ => "Disconnected",
            };
            Enqueue($"**{label}**: {DiscordFormat.Escape(message)}");
        }

        return false;   // this bot never owns the reconnect; that is autorelog's job
    }

    /// <summary>Adds a line for the bridge to post. Never blocks the client.</summary>
    public void Enqueue(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (_pending.Count >= MaxQueued) _pending.TryDequeue(out _);
        _pending.Enqueue(line);
    }

    /// <summary>
    /// Takes as many queued lines as fit in one Discord message. Returns false when
    /// there is nothing to post.
    /// </summary>
    public bool TryDrain(out string message, int limit = 1900)
    {
        message = "";
        if (_pending.IsEmpty) return false;

        var lines = new List<string>();
        int length = 0;

        while (_pending.TryPeek(out string? next))
        {
            int cost = next.Length + 1;

            // Always take at least one line, even an oversized one, so nothing wedges the queue.
            if (lines.Count > 0 && length + cost > limit) break;

            _pending.TryDequeue(out _);
            lines.Add(next);
            length += cost;
        }

        message = DiscordFormat.Truncate(string.Join('\n', lines), limit);
        return message.Length > 0;
    }
}
