using ThanosClient.Auth;
using ThanosClient.Bots;
using ThanosClient.Client;

namespace ThanosClient.Tests;

/// <summary>Records client events and sends one chat line once it is in the world.</summary>
public sealed class RecordingBot : ChatBot
{
    public override string Name => "recorder";

    public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ChatSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? LastChat { get; private set; }
    public List<string> AllChat { get; } = new();

    public override void OnJoinedGame()
    {
        Joined.TrySetResult();
        SendChat("hello from the client");
    }

    public override void OnChat(string text, string rawJson, ChatPosition position)
    {
        LastChat = text;
        AllChat.Add(text);
        ChatSeen.TrySetResult();
    }
}

/// <summary>Stands in for Mojang's session server so the encrypted path can be tested offline.</summary>
public sealed class StubSessionServer : SessionServer
{
    public string? LastHash { get; private set; }
    public string? LastProfile { get; private set; }

    public override Task JoinAsync(Session session, string serverHash, CancellationToken ct = default)
    {
        LastHash = serverHash;
        LastProfile = session.UuidCompact;
        return Task.CompletedTask;
    }
}
