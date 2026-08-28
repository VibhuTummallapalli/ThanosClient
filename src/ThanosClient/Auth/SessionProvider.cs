using ThanosClient.Config;
using ThanosClient.Terminal;

namespace ThanosClient.Auth;

/// <summary>
/// Owns the login session for the lifetime of the process: acquires it at startup and
/// keeps it usable afterwards.
///
/// The second part matters for unattended hosting. A Minecraft access token lasts about
/// a day, and it is needed on every login - so a bot that stays up for a week would
/// authenticate once, then fail every reconnect after the first 24 hours with a session
/// server rejection, retrying forever. Refreshing before each connect avoids that.
/// </summary>
public sealed class SessionProvider
{
    private readonly AccountSettings _settings;
    private readonly MicrosoftAuth _auth;
    private readonly string _cachePath;

    public SessionProvider(AccountSettings settings)
    {
        _settings = settings;
        _auth = new MicrosoftAuth(settings.MsClientId);
        _cachePath = string.IsNullOrWhiteSpace(settings.SessionCachePath)
            ? SessionCache.DefaultPath
            : settings.SessionCachePath;
    }

    public string CachePath => _cachePath;

    /// <summary>
    /// Gets a session at startup: cached if still good, refreshed if it can be, and only
    /// then falling back to an interactive device-code sign-in.
    /// </summary>
    public async Task<Session?> AcquireAsync(bool forceLogin, CancellationToken ct = default)
    {
        if (_settings.IsOffline)
        {
            string name = _settings.OfflineUsername.Trim();
            if (name.Length is 0 or > 16)
            {
                ConsoleIO.WriteError("account.offlineUsername must be 1-16 characters.");
                return null;
            }
            return Session.ForOffline(name);
        }

        if (forceLogin) SessionCache.Clear(_cachePath);

        Session? cached = forceLogin ? null : SessionCache.Load(_cachePath);

        if (cached is { Offline: false })
        {
            if (!cached.IsExpired) return cached;

            Session? refreshed = await TryRefreshAsync(cached, ct);
            if (refreshed is not null) return refreshed;

            ConsoleIO.WriteWarning("Signing in again.");
        }

        Session session = await _auth.LoginInteractiveAsync(ct);
        SessionCache.Save(_cachePath, session);
        return session;
    }

    /// <summary>
    /// Returns a session that is safe to log in with, refreshing first if the current one
    /// has aged out. Called before every connection attempt, including reconnects.
    ///
    /// Never prompts: an unattended host has nobody to read a device code, so a failed
    /// refresh returns the stale session and lets the login report the real reason.
    /// </summary>
    public async Task<Session> EnsureFreshAsync(Session current, CancellationToken ct = default)
    {
        if (current.Offline || !current.IsExpired) return current;

        ConsoleIO.WriteInfo("Access token has expired; refreshing before connecting.");

        Session? refreshed = await TryRefreshAsync(current, ct);
        if (refreshed is not null) return refreshed;

        ConsoleIO.WriteError(
            "Could not refresh the login. Run the client with --auth-only on this host to sign in again.");

        return current;
    }

    /// <summary>Refreshes via the stored Microsoft refresh token. Null means it could not be done.</summary>
    private async Task<Session?> TryRefreshAsync(Session current, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(current.MsRefreshToken)) return null;

        try
        {
            Session refreshed = await _auth.RefreshAsync(current.MsRefreshToken!, ct);
            SessionCache.Save(_cachePath, refreshed);
            ConsoleIO.WriteSuccess($"Refreshed the session for {refreshed.Username}.");
            return refreshed;
        }
        catch (AuthException ex)
        {
            ConsoleIO.WriteWarning($"Token refresh failed: {ex.Message}");
            return null;
        }
    }
}
