using System.Net.Http.Json;

namespace ThanosClient.Auth;

/// <summary>
/// The "join server" half of online-mode login. Before the client sends its encryption
/// response, it tells Mojang's session server which server hash it is about to join;
/// the server then verifies that claim out-of-band. This endpoint is version agnostic,
/// so a modern Microsoft-account token works fine against a 1.8.9 server.
/// </summary>
public class SessionServer
{
    private const string JoinUrl = "https://sessionserver.mojang.com/session/minecraft/join";

    private readonly HttpClient _http;

    public SessionServer(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public virtual async Task JoinAsync(Session session, string serverHash, CancellationToken ct = default)
    {
        var payload = new
        {
            accessToken = session.AccessToken,
            selectedProfile = session.UuidCompact,
            serverId = serverHash,
        };

        using var response = await _http.PostAsJsonAsync(JoinUrl, payload, ct);

        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(ct);
        string detail = string.IsNullOrWhiteSpace(body) ? response.StatusCode.ToString() : body.Trim();

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new AuthException(
                $"The session server rejected the join request ({detail}). " +
                "The access token is probably expired or was issued for a different profile.");

        throw new AuthException($"Session server join failed: {detail}");
    }
}
