using ThanosClient.Client;
using ThanosClient.Config;

namespace ThanosClient.Bots;

/// <summary>
/// Reconnects after an unexpected disconnect. Kicks whose reason matches a configured
/// word (a ban, for example) are treated as permanent, because retrying those is both
/// pointless and rude to the server.
/// </summary>
public sealed class AutoRelogBot : ChatBot
{
    private readonly AutoRelogSettings _settings;
    private int _attempts;

    public override string Name => "autorelog";

    public AutoRelogBot(AutoRelogSettings settings) => _settings = settings;

    public override void OnJoinedGame() => _attempts = 0;

    public override bool OnDisconnect(DisconnectReason reason, string message)
    {
        if (reason == DisconnectReason.UserRequested) return false;

        string reasonText = message ?? "";
        foreach (string word in _settings.IgnoreKickWords)
        {
            if (!string.IsNullOrWhiteSpace(word) &&
                reasonText.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                LogWarning($"Not reconnecting: the kick reason mentions \"{word}\".");
                return false;
            }
        }

        if (_settings.MaxAttempts > 0 && _attempts >= _settings.MaxAttempts)
        {
            LogWarning($"Giving up after {_attempts} reconnect attempt(s).");
            return false;
        }

        _attempts++;
        int delay = Math.Max(1, _settings.DelaySeconds);
        LogInfo($"Reconnecting in {delay}s (attempt {_attempts}" +
                (_settings.MaxAttempts > 0 ? $"/{_settings.MaxAttempts}" : "") + ").");

        // Off the receive thread, so the socket teardown can finish first.
        Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ => Client.RequestReconnect());
        return true;
    }
}
