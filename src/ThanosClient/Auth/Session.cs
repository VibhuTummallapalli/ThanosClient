using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThanosClient.Auth;

/// <summary>A logged-in Minecraft profile plus the tokens needed to keep it alive.</summary>
public sealed class Session
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("expiresAtUtc")] public DateTime ExpiresAtUtc { get; set; }
    [JsonPropertyName("msRefreshToken")] public string? MsRefreshToken { get; set; }
    [JsonPropertyName("offline")] public bool Offline { get; set; }

    /// <summary>UUID without dashes, the form the session server expects.</summary>
    [JsonIgnore] public string UuidCompact => Uuid.Replace("-", "");

    [JsonIgnore] public bool IsExpired => !Offline && DateTime.UtcNow >= ExpiresAtUtc.AddMinutes(-5);

    public static Session ForOffline(string username) => new()
    {
        Username = username,
        Uuid = CryptoUtil.OfflineUuid(username).ToString(),
        AccessToken = "0",
        Offline = true,
        ExpiresAtUtc = DateTime.MaxValue,
    };
}

/// <summary>
/// Persists the session between runs so the device-code prompt only appears once.
/// The file holds live tokens in plain text - see the security note in the README.
/// </summary>
public static class SessionCache
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ThanosClient",
            "session.json");

    public static Session? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Session>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Terminal.ConsoleIO.WriteWarning($"Could not read cached session: {ex.Message}");
            return null;
        }
    }

    public static void Save(string path, Session session)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(session, Options));
        }
        catch (Exception ex)
        {
            Terminal.ConsoleIO.WriteWarning($"Could not save session: {ex.Message}");
        }
    }

    public static void Clear(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Terminal.ConsoleIO.WriteWarning($"Could not clear session: {ex.Message}"); }
    }
}
