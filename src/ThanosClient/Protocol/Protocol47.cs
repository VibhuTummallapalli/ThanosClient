namespace ThanosClient.Protocol;

/// <summary>
/// Packet identifiers for protocol 47 (Minecraft 1.8 - 1.8.9). Packet ids are
/// state-dependent, so they are grouped by connection state rather than flattened.
/// </summary>
public static class Protocol47
{
    public const int Version = 47;
    public const string VersionName = "1.8.9";

    public enum State
    {
        Handshaking = 0,
        Status = 1,
        Login = 2,
        Play = 3,
    }

    /// <summary>Server -> client, login state.</summary>
    public static class LoginIn
    {
        public const int Disconnect = 0x00;
        public const int EncryptionRequest = 0x01;
        public const int LoginSuccess = 0x02;
        public const int SetCompression = 0x03;
    }

    /// <summary>Client -> server, login state.</summary>
    public static class LoginOut
    {
        public const int LoginStart = 0x00;
        public const int EncryptionResponse = 0x01;
    }

    /// <summary>Server -> client, play state. Only the packets the client acts on are named.</summary>
    public static class PlayIn
    {
        public const int KeepAlive = 0x00;
        public const int JoinGame = 0x01;
        public const int ChatMessage = 0x02;
        public const int TimeUpdate = 0x03;
        public const int UpdateHealth = 0x06;
        public const int Respawn = 0x07;
        public const int PlayerPositionAndLook = 0x08;
        public const int SpawnPosition = 0x05;
        public const int PlayerListItem = 0x38;
        public const int PluginMessage = 0x3F;
        public const int Disconnect = 0x40;
        public const int SetCompression = 0x46;
        public const int PlayerListHeaderFooter = 0x47;
        public const int ServerDifficulty = 0x41;
        public const int Title = 0x45;
    }

    /// <summary>Client -> server, play state.</summary>
    public static class PlayOut
    {
        public const int KeepAlive = 0x00;
        public const int ChatMessage = 0x01;
        public const int UseEntity = 0x02;
        public const int Player = 0x03;
        public const int PlayerPosition = 0x04;
        public const int PlayerLook = 0x05;
        public const int PlayerPositionAndLook = 0x06;
        public const int Animation = 0x0A;
        public const int EntityAction = 0x0B;
        public const int ClientStatus = 0x16;
        public const int ClientSettings = 0x15;
        public const int PluginMessage = 0x17;
    }

    /// <summary>Both directions, status state.</summary>
    public static class Status
    {
        public const int Request = 0x00;
        public const int Response = 0x00;
        public const int Ping = 0x01;
        public const int Pong = 0x01;
    }

    /// <summary>Handshake, the single packet that selects the next state.</summary>
    public const int Handshake = 0x00;
}
