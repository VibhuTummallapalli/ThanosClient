using System.Text.Json.Serialization;

namespace ThanosClient.Config;

/// <summary>
/// Discord bridge configuration. Every command is gated on the whitelist below, so a
/// bridge with no roles and no users configured accepts nothing at all - the safe
/// default for a bot that can act with the account's in-game permissions.
/// </summary>
public sealed class DiscordSettings
{
    /// <summary>Environment variable checked before <see cref="Token"/>, so the token need not sit in the config file.</summary>
    public const string TokenEnvironmentVariable = "THANOSCLIENT_DISCORD_TOKEN";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>Bot token. Prefer the environment variable; this is the fallback.</summary>
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    /// <summary>
    /// Server id. Slash commands register instantly for a single guild; leaving this at
    /// 0 registers them globally, which Discord can take up to an hour to propagate.
    /// </summary>
    [JsonPropertyName("guildId")] public ulong GuildId { get; set; }

    /// <summary>Channels the bridge relays into and accepts commands from. Empty means any channel.</summary>
    [JsonPropertyName("channelIds")] public List<ulong> ChannelIds { get; set; } = new();

    /// <summary>Roles allowed to run commands. Members of any of these pass the gate.</summary>
    [JsonPropertyName("allowedRoleIds")] public List<ulong> AllowedRoleIds { get; set; } = new();

    /// <summary>Individual users allowed to run commands, regardless of their roles.</summary>
    [JsonPropertyName("allowedUserIds")] public List<ulong> AllowedUserIds { get; set; } = new();

    /// <summary>Post in-game chat to the configured channels.</summary>
    [JsonPropertyName("relayGameChat")] public bool RelayGameChat { get; set; } = true;

    /// <summary>Post join and leave notices as well as chat.</summary>
    [JsonPropertyName("relayJoinLeave")] public bool RelayJoinLeave { get; set; } = true;

    /// <summary>Post connection state changes (joined the server, kicked, reconnecting).</summary>
    [JsonPropertyName("relayConnectionEvents")] public bool RelayConnectionEvents { get; set; } = true;

    /// <summary>
    /// Treat ordinary messages in the configured channels as chat to forward in-game.
    /// Requires the privileged Message Content intent to be enabled for the application.
    /// Senders still have to pass the whitelist.
    /// </summary>
    [JsonPropertyName("relayDiscordMessages")] public bool RelayDiscordMessages { get; set; }

    /// <summary>How long relayed game chat is batched before posting, to stay inside Discord's rate limits.</summary>
    [JsonPropertyName("relayIntervalSeconds")] public int RelayIntervalSeconds { get; set; } = 2;

    /// <summary>Minimum gap between two commands from the same user.</summary>
    [JsonPropertyName("perUserCooldownSeconds")] public int PerUserCooldownSeconds { get; set; } = 2;

    /// <summary>Ceiling on commands accepted per minute across everyone, so the account cannot be spam-kicked.</summary>
    [JsonPropertyName("maxCommandsPerMinute")] public int MaxCommandsPerMinute { get; set; } = 30;

    /// <summary>Prefix shown on relayed chat sent from Discord, so players can see where it came from.</summary>
    [JsonPropertyName("gameChatPrefix")] public string GameChatPrefix { get; set; } = "[Discord] ";

    [JsonIgnore]
    public bool HasWhitelist => AllowedRoleIds.Count > 0 || AllowedUserIds.Count > 0;

    /// <summary>The token actually used, environment variable first.</summary>
    [JsonIgnore]
    public string EffectiveToken
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
            return string.IsNullOrWhiteSpace(fromEnvironment) ? Token.Trim() : fromEnvironment.Trim();
        }
    }
}
