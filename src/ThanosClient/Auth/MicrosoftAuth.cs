using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ThanosClient.Terminal;

namespace ThanosClient.Auth;

/// <summary>
/// Microsoft account login for Minecraft, using the OAuth 2.0 device code flow:
///   MSA device code -> Xbox Live -> XSTS -> Minecraft services -> profile.
/// Device code rather than a redirect flow, because this is a console app with no
/// browser or loopback listener of its own.
///
/// Needs account.msClientId to name an Azure application registered for personal Microsoft
/// accounts, with public client flows enabled. The launcher id this used to default to is a
/// Live Connect app id that AAD will not resolve (AADSTS700016); its own legacy endpoint
/// does resolve it but then refuses the token exchange for anyone but the launcher, and
/// refuses outright on accounts with current security requirements. So: register one.
/// </summary>
public sealed class MicrosoftAuth
{
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";
    private const string Scope = "XboxLive.signin offline_access";

    private readonly HttpClient _http;
    private readonly string _clientId;

    /// <summary>
    /// Fires as soon as the Microsoft sign-in itself succeeds, before the Xbox and Minecraft
    /// exchanges that follow it. Those can fail for reasons that have nothing to do with the
    /// sign-in, and without this the refresh token dies with them -- sending the user back
    /// through a device code they already completed. Persist it here and a retry is silent.
    /// </summary>
    public Action<string>? MicrosoftSignInSucceeded { get; set; }

    public MicrosoftAuth(string clientId, HttpClient? http = null)
    {
        _clientId = clientId;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Full interactive login. Blocks while the user approves the device code.</summary>
    public async Task<Session> LoginInteractiveAsync(CancellationToken ct = default)
    {
        DeviceCodeResponse device = await StartDeviceCodeAsync(ct);

        ConsoleIO.WriteLine("");
        ConsoleIO.WriteInfo("Microsoft sign-in required.");
        ConsoleIO.WriteInfo($"  1. Open  {device.VerificationUri}");
        ConsoleIO.WriteInfo($"  2. Enter code  {device.UserCode}");
        ConsoleIO.WriteInfo("Waiting for approval...");
        ConsoleIO.WriteLine("");

        TokenResponse token = await PollForTokenAsync(device, ct);

        if (!string.IsNullOrEmpty(token.RefreshToken))
            MicrosoftSignInSucceeded?.Invoke(token.RefreshToken!);

        return await ExchangeForSessionAsync(token.AccessToken, token.RefreshToken, ct);
    }

    /// <summary>Silent re-login with a stored refresh token. Throws if the token is no longer valid.</summary>
    public async Task<Session> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope,
        };

        using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException($"Refreshing the Microsoft token failed: {Describe(body)}");

        TokenResponse token = Parse<TokenResponse>(body);
        return await ExchangeForSessionAsync(token.AccessToken, token.RefreshToken, ct);
    }

    private async Task<DeviceCodeResponse> StartDeviceCodeAsync(CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = Scope,
        };

        using var response = await _http.PostAsync(DeviceCodeUrl, new FormUrlEncodedContent(form), ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException(
                $"Could not start the device code flow: {Describe(body)}. " +
                "If this says the application was not found, account.msClientId is not a " +
                "registered Azure application id -- register one (public client flows on, " +
                "personal Microsoft accounts allowed) and set it (see README).");

        return Parse<DeviceCodeResponse>(body);
    }

    private async Task<TokenResponse> PollForTokenAsync(DeviceCodeResponse device, CancellationToken ct)
    {
        int interval = Math.Max(device.Interval, 1);
        DateTime deadline = DateTime.UtcNow.AddSeconds(device.ExpiresIn > 0 ? device.ExpiresIn : 900);

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = device.DeviceCode,
        };

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            using var response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return Parse<TokenResponse>(body);

            string error = ReadField(body, "error") ?? "unknown_error";
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "authorization_declined":
                    throw new AuthException("Sign-in was declined in the browser.");
                case "expired_token":
                    throw new AuthException("The device code expired before it was approved.");
                default:
                    throw new AuthException($"Device code sign-in failed: {Describe(body)}");
            }
        }

        throw new AuthException("Timed out waiting for the device code to be approved.");
    }

    /// <summary>MSA token -> Xbox Live -> XSTS -> Minecraft token -> profile.</summary>
    private async Task<Session> ExchangeForSessionAsync(string msAccessToken, string? refreshToken, CancellationToken ct)
    {
        (string xblToken, string userHash) = await AuthenticateXboxLiveAsync(msAccessToken, ct);
        string xstsToken = await AuthorizeXstsAsync(xblToken, ct);
        (string mcToken, int expiresIn) = await LoginWithXboxAsync(userHash, xstsToken, ct);
        (string uuid, string name) = await FetchProfileAsync(mcToken, ct);

        return new Session
        {
            Username = name,
            Uuid = uuid,
            AccessToken = mcToken,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 86400),
            MsRefreshToken = refreshToken,
            Offline = false,
        };
    }

    private async Task<(string Token, string UserHash)> AuthenticateXboxLiveAsync(string msAccessToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + msAccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        using var response = await PostXboxAsync(XblUrl, payload, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException(
                $"Xbox Live authentication failed (HTTP {(int)response.StatusCode}): " +
                $"{Describe(body)}{XboxError(response)}");

        using JsonDocument doc = JsonDocument.Parse(body);
        string token = doc.RootElement.GetProperty("Token").GetString()
            ?? throw new AuthException("Xbox Live returned no token.");
        string hash = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0]
            .GetProperty("uhs").GetString() ?? throw new AuthException("Xbox Live returned no user hash.");
        return (token, hash);
    }

    private async Task<string> AuthorizeXstsAsync(string xblToken, CancellationToken ct)
    {
        var payload = new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        using var response = await PostXboxAsync(XstsUrl, payload, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            string? xerr = ReadField(body, "XErr");
            string hint = xerr switch
            {
                "2148916233" => "this Microsoft account has no Xbox profile - create one at xbox.com and retry",
                "2148916235" => "Xbox Live is not available in this account's country",
                "2148916236" or "2148916237" => "this account needs adult verification",
                "2148916238" => "this is a child account and must be added to a Microsoft family group",
                _ => Describe(body),
            };
            throw new AuthException($"XSTS authorization failed: {hint}");
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("Token").GetString()
            ?? throw new AuthException("XSTS returned no token.");
    }

    private async Task<(string Token, int ExpiresIn)> LoginWithXboxAsync(string userHash, string xstsToken, CancellationToken ct)
    {
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };

        using var response = await _http.PostAsJsonAsync(McLoginUrl, payload, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException($"Minecraft services login failed: {Describe(body)}");

        using JsonDocument doc = JsonDocument.Parse(body);
        string token = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new AuthException("Minecraft services returned no access token.");
        int expires = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) ? e.GetInt32() : 86400;
        return (token, expires);
    }

    private async Task<(string Uuid, string Name)> FetchProfileAsync(string mcToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);

        using var response = await _http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new AuthException("This Microsoft account does not own Minecraft: Java Edition.");
        if (!response.IsSuccessStatusCode)
            throw new AuthException($"Could not fetch the Minecraft profile: {Describe(body)}");

        using JsonDocument doc = JsonDocument.Parse(body);
        string id = doc.RootElement.GetProperty("id").GetString() ?? throw new AuthException("Profile has no id.");
        string name = doc.RootElement.GetProperty("name").GetString() ?? throw new AuthException("Profile has no name.");
        return (id, name);
    }

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json) ?? throw new AuthException("Unexpected empty response from the auth server.");

    private static string? ReadField(string json, string field)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(field, out JsonElement value)) return null;
            return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Turns an error body into something short enough for one console line.</summary>
    /// <summary>
    /// POSTs JSON to an Xbox endpoint. Both of them want an explicit JSON Accept and the
    /// contract-version header; without those they can answer with a bare status and no
    /// body at all, which leaves nothing to report.
    /// </summary>
    private async Task<HttpResponseMessage> PostXboxAsync(string url, object payload, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(payload));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("x-xbl-contract-version", "1");

        return await _http.SendAsync(request, ct);
    }

    /// <summary>An empty-bodied Xbox rejection still names its reason in a header.</summary>
    private static string XboxError(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Err", out var values))
            return $"[X-Err {string.Join(",", values)}]";

        return "";
    }

    private static string Describe(string body)
    {
        string? description = ReadField(body, "error_description")
            ?? ReadField(body, "errorMessage")
            ?? ReadField(body, "error");

        if (!string.IsNullOrWhiteSpace(description))
        {
            int newline = description.IndexOfAny(new[] { '\r', '\n' });
            return newline > 0 ? description[..newline] : description;
        }

        return body.Length > 200 ? body[..200] + "..." : body;
    }

    private sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = "";
        [JsonPropertyName("verification_uri")] public string VerificationUri { get; set; } = "";
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 900;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    }
}

public class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}
