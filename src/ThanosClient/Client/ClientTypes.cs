namespace ThanosClient.Client;

/// <summary>Player position and orientation, in Minecraft's coordinate conventions.</summary>
public struct Location
{
    public double X;
    public double Y;
    public double Z;
    public float Yaw;
    public float Pitch;

    public override string ToString() => $"{X:0.00}, {Y:0.00}, {Z:0.00} (yaw {Yaw:0.0}, pitch {Pitch:0.0})";
}

public enum DisconnectReason
{
    /// <summary>The socket died or the server stopped responding.</summary>
    ConnectionLost,

    /// <summary>Login never completed: auth, encryption, or a login-state disconnect.</summary>
    LoginFailed,

    /// <summary>The server sent a play-state Disconnect packet.</summary>
    InGameKick,

    /// <summary>The user asked to leave.</summary>
    UserRequested,
}

/// <summary>Where a chat message appeared on the vanilla client.</summary>
public enum ChatPosition
{
    Chat = 0,
    System = 1,
    ActionBar = 2,
}
