using System.Text.RegularExpressions;
using ThanosClient.Client;
using ThanosClient.Config;

namespace ThanosClient.Bots;

/// <summary>
/// Replies to chat lines matching configured regular expressions. A shared cooldown
/// stops a chatty trigger from turning into self-inflicted spam-kick.
/// </summary>
public sealed class AutoRespondBot : ChatBot
{
    private readonly AutoRespondSettings _settings;
    private readonly List<(Regex Pattern, string Response)> _rules = new();
    private DateTime _nextAllowed = DateTime.MinValue;

    public override string Name => "autorespond";

    public AutoRespondBot(AutoRespondSettings settings)
    {
        _settings = settings;

        foreach (AutoRespondRule rule in settings.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Match) || string.IsNullOrWhiteSpace(rule.Send))
                continue;

            try
            {
                _rules.Add((new Regex(rule.Match, RegexOptions.IgnoreCase | RegexOptions.Compiled), rule.Send));
            }
            catch (ArgumentException ex)
            {
                LogError($"Ignoring rule \"{rule.Match}\": {ex.Message}");
            }
        }
    }

    public override void OnChat(string text, string rawJson, ChatPosition position)
    {
        if (_rules.Count == 0 || position == ChatPosition.ActionBar) return;
        if (DateTime.UtcNow < _nextAllowed) return;

        foreach ((Regex pattern, string response) in _rules)
        {
            Match match = pattern.Match(text);
            if (!match.Success) continue;

            // Never react to our own messages; that is how feedback loops start.
            if (text.Contains(Client.Username, StringComparison.Ordinal) &&
                text.StartsWith($"<{Client.Username}>", StringComparison.Ordinal))
                return;

            string reply = match.Result(response);
            _nextAllowed = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.CooldownSeconds));

            LogInfo($"Matched \"{pattern}\", sending: {reply}");
            SendChat(reply);
            return;
        }
    }
}
