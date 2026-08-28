using ThanosClient.Config;

namespace ThanosClient.Bridge;

public enum AuthorizationResult
{
    Allowed,

    /// <summary>The command came from a channel the bridge does not serve.</summary>
    WrongChannel,

    /// <summary>No roles and no users are whitelisted, so nothing is permitted.</summary>
    NoWhitelistConfigured,

    /// <summary>The user holds none of the whitelisted roles and is not individually listed.</summary>
    NotWhitelisted,
}

/// <summary>
/// Decides whether a Discord user may drive the Minecraft client. Deliberately fails
/// closed: an unconfigured whitelist permits nobody, because a whitelisted user can act
/// with whatever in-game permissions the account has.
/// </summary>
public static class DiscordAuthorizer
{
    public static AuthorizationResult Check(
        ulong userId,
        IEnumerable<ulong> userRoleIds,
        ulong channelId,
        DiscordSettings settings)
    {
        if (settings.ChannelIds.Count > 0 && !settings.ChannelIds.Contains(channelId))
            return AuthorizationResult.WrongChannel;

        if (!settings.HasWhitelist)
            return AuthorizationResult.NoWhitelistConfigured;

        if (settings.AllowedUserIds.Contains(userId))
            return AuthorizationResult.Allowed;

        foreach (ulong roleId in userRoleIds)
        {
            if (settings.AllowedRoleIds.Contains(roleId))
                return AuthorizationResult.Allowed;
        }

        return AuthorizationResult.NotWhitelisted;
    }

    /// <summary>The message shown to the user when a check fails.</summary>
    public static string Explain(AuthorizationResult result) => result switch
    {
        AuthorizationResult.Allowed => "",
        AuthorizationResult.WrongChannel => "This command only works in the channels the bridge is configured for.",
        AuthorizationResult.NoWhitelistConfigured =>
            "No Discord whitelist is configured, so nobody can control the client. " +
            "Add role or user ids to discord.allowedRoleIds / discord.allowedUserIds.",
        AuthorizationResult.NotWhitelisted => "You are not on the whitelist for this bot.",
        _ => "Not permitted.",
    };
}
