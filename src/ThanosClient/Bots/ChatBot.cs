using ThanosClient.Client;
using ThanosClient.Terminal;

namespace ThanosClient.Bots;

/// <summary>
/// Base class for automation plugins. A bot is attached to the client once and then
/// receives events for the lifetime of the process, across reconnects.
/// </summary>
public abstract class ChatBot
{
    protected McClient Client { get; private set; } = null!;

    public virtual string Name => GetType().Name;
    public bool Enabled { get; set; } = true;

    internal void Attach(McClient client) => Client = client;

    /// <summary>Called once the server has accepted the player into the world.</summary>
    public virtual void OnJoinedGame() { }

    /// <summary>Called for every chat packet. <paramref name="text"/> has formatting stripped.</summary>
    public virtual void OnChat(string text, string rawJson, ChatPosition position) { }

    public virtual void OnPlayerJoin(PlayerInfo player) { }

    public virtual void OnPlayerLeave(PlayerInfo player) { }

    /// <summary>Called roughly ten times a second while connected.</summary>
    public virtual void OnUpdate() { }

    /// <summary>
    /// Called after the connection ends. Return true if this bot has taken charge of
    /// reconnecting, which stops other bots and the main loop from also trying.
    /// </summary>
    public virtual bool OnDisconnect(DisconnectReason reason, string message) => false;

    protected void SendChat(string message) => Client.SendChat(message);

    protected void LogInfo(string message) => ConsoleIO.WriteInfo($"[{Name}] {message}");
    protected void LogWarning(string message) => ConsoleIO.WriteWarning($"[{Name}] {message}");
    protected void LogError(string message) => ConsoleIO.WriteError($"[{Name}] {message}");
}
