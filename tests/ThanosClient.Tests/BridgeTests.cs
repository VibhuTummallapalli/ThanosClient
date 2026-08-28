using ThanosClient.Bridge;
using ThanosClient.Client;
using ThanosClient.Config;

namespace ThanosClient.Tests;

/// <summary>
/// Covers the Discord bridge logic that can be tested without a gateway connection:
/// who is allowed to do what, how often, and how text is cleaned in both directions.
/// These are the parts where a mistake is a security or spam problem rather than a
/// visible bug, so they are worth pinning down.
/// </summary>
public static class BridgeTests
{
    public static void Run(Action<string, bool, string?> check, Action<string, string, string> equal)
    {
        Authorization(check);
        RateLimiting(check);
        Sanitizing(equal, check);
        Escaping(equal, check);
        Batching(equal, check);
    }

    private static void Authorization(Action<string, bool, string?> check)
    {
        const ulong role = 111;
        const ulong user = 222;
        const ulong channel = 333;

        var open = new DiscordSettings();
        check("empty whitelist denies everyone",
            DiscordAuthorizer.Check(user, new[] { role }, channel, open) == AuthorizationResult.NoWhitelistConfigured, null);

        var byRole = new DiscordSettings { AllowedRoleIds = { role } };
        check("a whitelisted role is allowed",
            DiscordAuthorizer.Check(user, new[] { role }, channel, byRole) == AuthorizationResult.Allowed, null);
        check("a different role is refused",
            DiscordAuthorizer.Check(user, new ulong[] { 999 }, channel, byRole) == AuthorizationResult.NotWhitelisted, null);
        check("no roles at all is refused",
            DiscordAuthorizer.Check(user, System.Array.Empty<ulong>(), channel, byRole) == AuthorizationResult.NotWhitelisted, null);

        var byUser = new DiscordSettings { AllowedUserIds = { user } };
        check("a whitelisted user is allowed regardless of roles",
            DiscordAuthorizer.Check(user, System.Array.Empty<ulong>(), channel, byUser) == AuthorizationResult.Allowed, null);
        check("another user is refused",
            DiscordAuthorizer.Check(444, System.Array.Empty<ulong>(), channel, byUser) == AuthorizationResult.NotWhitelisted, null);

        var scoped = new DiscordSettings { AllowedUserIds = { user }, ChannelIds = { channel } };
        check("the served channel is allowed",
            DiscordAuthorizer.Check(user, System.Array.Empty<ulong>(), channel, scoped) == AuthorizationResult.Allowed, null);
        check("another channel is refused even for a whitelisted user",
            DiscordAuthorizer.Check(user, System.Array.Empty<ulong>(), 555, scoped) == AuthorizationResult.WrongChannel, null);

        // The channel check runs first, so the whitelist state is not disclosed to
        // people talking in channels the bridge does not serve.
        var scopedNoWhitelist = new DiscordSettings { ChannelIds = { channel } };
        check("channel is checked before the whitelist",
            DiscordAuthorizer.Check(user, System.Array.Empty<ulong>(), 555, scopedNoWhitelist) == AuthorizationResult.WrongChannel, null);

        check("every refusal explains itself",
            DiscordAuthorizer.Explain(AuthorizationResult.NotWhitelisted).Length > 0 &&
            DiscordAuthorizer.Explain(AuthorizationResult.WrongChannel).Length > 0 &&
            DiscordAuthorizer.Explain(AuthorizationResult.NoWhitelistConfigured).Length > 0, null);
    }

    private static void RateLimiting(Action<string, bool, string?> check)
    {
        DateTime now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new CommandRateLimiter(perUserCooldownSeconds: 5, maxPerMinute: 3, () => now);

        check("first command is allowed", limiter.TryAcquire(1, out _), null);
        check("the same user is refused inside the cooldown", !limiter.TryAcquire(1, out string cooldownReason), null);
        check("the cooldown refusal says how long to wait", cooldownReason.Contains("second"), cooldownReason);

        check("a different user is unaffected by someone else's cooldown", limiter.TryAcquire(2, out _), null);

        now = now.AddSeconds(6);
        check("the cooldown expires", limiter.TryAcquire(1, out _), null);

        // Three slots are now used within the minute, so the global cap applies.
        check("the global cap refuses a fourth command", !limiter.TryAcquire(3, out string capReason), null);
        check("the cap refusal mentions the limit", capReason.Contains("3"), capReason);

        now = now.AddMinutes(2);
        check("the window slides", limiter.TryAcquire(3, out _), null);

        var unlimited = new CommandRateLimiter(0, 0, () => now);
        check("zeroed limits disable throttling",
            unlimited.TryAcquire(9, out _) && unlimited.TryAcquire(9, out _), null);
    }

    private static void Sanitizing(Action<string, string, string> equal, Action<string, bool, string?> check)
    {
        equal("plain chat is untouched", "hello there", ChatSanitizer.Sanitize("hello there"));
        equal("newlines become spaces", "one two", ChatSanitizer.Sanitize("one\ntwo"));
        equal("carriage returns and tabs collapse", "one two", ChatSanitizer.Sanitize("one\r\n\ttwo"));
        equal("repeated spaces collapse", "a b", ChatSanitizer.Sanitize("a     b"));
        equal("surrounding whitespace is trimmed", "hi", ChatSanitizer.Sanitize("   hi   "));

        // A section sign gets the client kicked for illegal characters, so it is the
        // single most important thing to strip from anything arriving via Discord.
        equal("section signs are removed", "cred", ChatSanitizer.Sanitize("§cred"));
        equal("control characters are removed", "ab", ChatSanitizer.Sanitize("ab"));
        equal("delete is removed", "ab", ChatSanitizer.Sanitize("ab"));
        equal("accented text survives", "café crème", ChatSanitizer.Sanitize("café crème"));
        equal("a string of only bad characters becomes empty", "", ChatSanitizer.Sanitize("§"));
        equal("empty input is safe", "", ChatSanitizer.Sanitize(""));

        check("every surviving character is one the server accepts",
            ChatSanitizer.Sanitize("mixed §c text\nhere").All(ChatSanitizer.IsAllowed), null);
    }

    private static void Escaping(Action<string, string, string> equal, Action<string, bool, string?> check)
    {
        string escaped = DiscordFormat.Escape("@everyone look at this");
        check("mentions are defused", !escaped.Contains("@everyone"), escaped);
        check("the text is still readable", escaped.Contains("everyone"), escaped);

        check("markdown is escaped", DiscordFormat.Escape("**bold**").StartsWith("\\*"), null);
        check("backticks are escaped", DiscordFormat.Escape("`code`").Contains("\\`"), null);
        check("underscores are escaped", DiscordFormat.Escape("snake_case").Contains("\\_"), null);

        equal("ordinary text is unchanged", "hello there", DiscordFormat.Escape("hello there"));

        string longText = new('x', 2500);
        check("truncation respects the limit", DiscordFormat.Truncate(longText).Length < 2000, null);
        check("truncation is marked", DiscordFormat.Truncate(longText).EndsWith("(truncated)"), null);
        equal("short text is not truncated", "short", DiscordFormat.Truncate("short"));
    }

    private static void Batching(Action<string, string, string> equal, Action<string, bool, string?> check)
    {
        var settings = new DiscordSettings { RelayGameChat = true, RelayJoinLeave = true };
        var relay = new GameChatRelay(settings);

        check("an empty relay has nothing to send", !relay.TryDrain(out _), null);

        relay.OnChat("<Alex> hello", "{}", ChatPosition.Chat);
        relay.OnChat("<Steve> hi", "{}", ChatPosition.Chat);

        check("queued chat drains", relay.TryDrain(out string batch), null);
        check("both lines are in one message", batch.Contains("Alex") && batch.Contains("Steve"), batch);
        check("lines are separated", batch.Contains('\n'), batch);
        check("the queue empties", !relay.TryDrain(out _), null);

        // Action bar text is the popup above the hotbar, not chat; relaying it would be noise.
        relay.OnChat("hotbar popup", "{}", ChatPosition.ActionBar);
        check("action bar text is not relayed", !relay.TryDrain(out _), null);

        var silent = new GameChatRelay(new DiscordSettings { RelayGameChat = false });
        silent.OnChat("<Alex> hello", "{}", ChatPosition.Chat);
        check("relay can be switched off", !silent.TryDrain(out _), null);

        var big = new GameChatRelay(settings);
        for (int i = 0; i < 200; i++) big.Enqueue(new string('y', 200));

        check("an oversized backlog still drains", big.TryDrain(out string first), null);
        check("one message stays within Discord's limit", first.Length <= 2000, first.Length.ToString());
        check("the rest is kept for the next message", big.TryDrain(out _), null);

        var raid = new GameChatRelay(settings);
        raid.OnChat("@everyone raid now", "{}", ChatPosition.Chat);
        raid.TryDrain(out string relayed);
        check("in-game chat cannot ping Discord", !relayed.Contains("@everyone"), relayed);
    }
}
