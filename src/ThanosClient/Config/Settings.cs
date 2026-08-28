using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThanosClient.Config;

/// <summary>Everything the client reads from thanosclient.json, plus the defaults.</summary>
public sealed class Settings
{
    [JsonPropertyName("server")] public ServerSettings Server { get; set; } = new();
    [JsonPropertyName("account")] public AccountSettings Account { get; set; } = new();
    [JsonPropertyName("console")] public ConsoleSettings Console { get; set; } = new();
    [JsonPropertyName("bots")] public BotSettings Bots { get; set; } = new();
    [JsonPropertyName("discord")] public DiscordSettings Discord { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // Keeps the generated file readable: without this, characters like > in the
        // prompt come back out as >.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string DefaultFileName = "thanosclient.json";

    public static Settings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new Settings();
            defaults.Save(path);
            return defaults;
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Settings>(json, Options)
               ?? throw new InvalidDataException($"{path} is empty or not valid JSON");
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
}

public sealed class ServerSettings
{
    /// <summary>Host name or IP. May include a port as host:port.</summary>
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public ushort Port { get; set; } = 25565;

    /// <summary>Sent in the MC|Brand plugin message after joining.</summary>
    [JsonPropertyName("clientBrand")] public string ClientBrand { get; set; } = "vanilla";

    /// <summary>Seconds to wait for the TCP connect and the login sequence.</summary>
    [JsonPropertyName("connectTimeoutSeconds")] public int ConnectTimeoutSeconds { get; set; } = 15;
}

public sealed class AccountSettings
{
    /// <summary>"microsoft" for online-mode servers, "offline" for cracked/LAN servers.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "microsoft";

    /// <summary>Username used when mode is "offline". Ignored for Microsoft logins.</summary>
    [JsonPropertyName("offlineUsername")] public string OfflineUsername { get; set; } = "Player";

    /// <summary>
    /// Azure application id used for the device code flow. The default is the public
    /// The Minecraft launcher client id. Kept only as a default that fails loudly: it is a
    /// legacy Live Connect id the modern endpoint cannot resolve, so online-mode sign-in
    /// needs an Azure application of your own, allow-listed by Mojang. See the README.
    /// </summary>
    [JsonPropertyName("msClientId")] public string MsClientId { get; set; } = "00000000402b5328";

    /// <summary>Where the cached session is stored. Empty means the per-user default path.</summary>
    [JsonPropertyName("sessionCachePath")] public string SessionCachePath { get; set; } = "";

    /// <summary>
    /// Environment override for <see cref="SessionCachePath"/>. Containers need this: the
    /// per-user default resolves under HOME, which lives inside the image, so without it
    /// the cached token is silently lost on every rebuild.
    /// </summary>
    public const string SessionPathEnvironmentVariable = "THANOSCLIENT_SESSION_PATH";

    /// <summary>The session path actually used: environment first, then config.</summary>
    [JsonIgnore]
    public string EffectiveSessionCachePath
    {
        get
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable(SessionPathEnvironmentVariable);
            return string.IsNullOrWhiteSpace(fromEnvironment) ? SessionCachePath.Trim() : fromEnvironment.Trim();
        }
    }

    [JsonIgnore]
    public bool IsOffline => string.Equals(Mode, "offline", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConsoleSettings
{
    [JsonPropertyName("colors")] public bool Colors { get; set; } = true;

    /// <summary>Lines starting with this go to the client; everything else goes to the server.</summary>
    [JsonPropertyName("commandPrefix")] public string CommandPrefix { get; set; } = ".";

    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "> ";

    /// <summary>Logs every packet id received. Very noisy; for protocol work only.</summary>
    [JsonPropertyName("debugPackets")] public bool DebugPackets { get; set; }

    /// <summary>Prints timestamps in front of chat lines.</summary>
    [JsonPropertyName("timestamps")] public bool Timestamps { get; set; } = true;
}

public sealed class BotSettings
{
    [JsonPropertyName("antiAfk")] public AntiAfkSettings AntiAfk { get; set; } = new();
    [JsonPropertyName("autoRelog")] public AutoRelogSettings AutoRelog { get; set; } = new();
    [JsonPropertyName("chatLog")] public ChatLogSettings ChatLog { get; set; } = new();
    [JsonPropertyName("autoRespond")] public AutoRespondSettings AutoRespond { get; set; } = new();
}

public sealed class AntiAfkSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("intervalSeconds")] public int IntervalSeconds { get; set; } = 60;

    /// <summary>Send a small look/position change as well as the idle packet.</summary>
    [JsonPropertyName("moveSlightly")] public bool MoveSlightly { get; set; } = true;
}

public sealed class AutoRelogSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("delaySeconds")] public int DelaySeconds { get; set; } = 10;

    /// <summary>0 means retry forever.</summary>
    [JsonPropertyName("maxAttempts")] public int MaxAttempts { get; set; } = 5;

    /// <summary>Kick reasons containing any of these are treated as permanent, so no retry.</summary>
    [JsonPropertyName("ignoreKickWords")] public List<string> IgnoreKickWords { get; set; } =
        new() { "banned", "whitelist" };
}

public sealed class ChatLogSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "logs/chat.log";
    [JsonPropertyName("includeTimestamps")] public bool IncludeTimestamps { get; set; } = true;
}

public sealed class AutoRespondSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("rules")] public List<AutoRespondRule> Rules { get; set; } = new();

    /// <summary>Minimum seconds between two responses, so the client cannot spam itself into a kick.</summary>
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; } = 5;
}

public sealed class AutoRespondRule
{
    /// <summary>.NET regular expression matched against the plain-text chat line.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "";

    /// <summary>Text sent back. $1, $2 ... expand to regex capture groups.</summary>
    [JsonPropertyName("send")] public string Send { get; set; } = "";
}
