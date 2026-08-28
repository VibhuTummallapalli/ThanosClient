using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ThanosClient.Protocol;

namespace ThanosClient.Tests;

/// <summary>
/// A minimal protocol-47 server, just complete enough to take a client through login and
/// into the play state. It exists so the client can be exercised end to end - framing,
/// compression, encryption, chat - without touching a real Minecraft server.
/// </summary>
public sealed class FakeServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly RSA _rsa = RSA.Create(1024);
    private readonly byte[] _verifyToken = RandomNumberGenerator.GetBytes(4);

    public FakeServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public ushort Port { get; }

    /// <summary>Negative disables compression.</summary>
    public int CompressionThreshold { get; set; } = -1;

    public bool RequireEncryption { get; set; }

    // Everything the server observed, for assertions.
    public int HandshakeProtocol { get; private set; }
    public string HandshakeHost { get; private set; } = "";
    public ushort HandshakePort { get; private set; }
    public string LoginUsername { get; private set; } = "";
    public byte[]? SharedSecret { get; private set; }
    public bool VerifyTokenMatched { get; private set; }
    public bool GotKeepAliveResponse { get; private set; }
    public bool GotClientSettings { get; private set; }
    public string? ClientBrand { get; private set; }
    public List<string> ChatFromClient { get; } = new();

    public TaskCompletionSource PlayStateReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ChatReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource KeepAliveAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task RunAsync(CancellationToken ct)
    {
        using TcpClient client = await _listener.AcceptTcpClientAsync(ct);
        client.NoDelay = true;

        NetworkStream network = client.GetStream();
        using var packets = new PacketStream(network);

        ReadHandshake(packets);
        ReadLoginStart(packets);

        if (RequireEncryption) DoEncryption(packets);

        if (CompressionThreshold >= 0)
        {
            packets.SendPacket(Protocol47.LoginIn.SetCompression,
                new PacketWriter().VarInt(CompressionThreshold).ToArray());
            packets.SetCompressionThreshold(CompressionThreshold);
        }

        packets.SendPacket(Protocol47.LoginIn.LoginSuccess, new PacketWriter()
            .String("069a79f4-44e9-4726-a5be-fca90e38aaf5")
            .String(LoginUsername)
            .ToArray());

        SendJoinGame(packets);
        PlayStateReached.TrySetResult();

        SendChat(packets, "{\"translate\":\"chat.type.text\",\"with\":[{\"text\":\"Notch\"},{\"text\":\"hello there\"}]}");
        packets.SendPacket(Protocol47.PlayIn.KeepAlive, new PacketWriter().VarInt(4242).ToArray());

        PumpClientPackets(packets, ct);
    }

    private void ReadHandshake(PacketStream packets)
    {
        var reader = new PacketReader(packets.ReadPacket());
        int id = reader.VarInt();
        if (id != Protocol47.Handshake) throw new InvalidOperationException($"expected handshake, got 0x{id:X2}");

        HandshakeProtocol = reader.VarInt();
        HandshakeHost = reader.String();
        HandshakePort = reader.UShort();
        int nextState = reader.VarInt();
        if (nextState != (int)Protocol47.State.Login)
            throw new InvalidOperationException($"expected next state login, got {nextState}");
    }

    private void ReadLoginStart(PacketStream packets)
    {
        var reader = new PacketReader(packets.ReadPacket());
        int id = reader.VarInt();
        if (id != Protocol47.LoginOut.LoginStart) throw new InvalidOperationException($"expected login start, got 0x{id:X2}");
        LoginUsername = reader.String(16);
    }

    /// <summary>Server half of the encryption handshake, mirroring vanilla exactly.</summary>
    private void DoEncryption(PacketStream packets)
    {
        packets.SendPacket(Protocol47.LoginIn.EncryptionRequest, new PacketWriter()
            .String("")                                   // 1.8 servers send an empty server id
            .Array(_rsa.ExportSubjectPublicKeyInfo())
            .Array(_verifyToken)
            .ToArray());

        var reader = new PacketReader(packets.ReadPacket());
        int id = reader.VarInt();
        if (id != Protocol47.LoginOut.EncryptionResponse)
            throw new InvalidOperationException($"expected encryption response, got 0x{id:X2}");

        byte[] secret = _rsa.Decrypt(reader.Array(), RSAEncryptionPadding.Pkcs1);
        byte[] token = _rsa.Decrypt(reader.Array(), RSAEncryptionPadding.Pkcs1);

        SharedSecret = secret;
        VerifyTokenMatched = token.AsSpan().SequenceEqual(_verifyToken);

        packets.EnableEncryption(secret);
    }

    private static void SendJoinGame(PacketStream packets)
    {
        packets.SendPacket(Protocol47.PlayIn.JoinGame, new PacketWriter()
            .Int(7)             // entity id
            .Byte(0)            // survival
            .SByte(0)           // overworld
            .Byte(2)            // normal difficulty
            .Byte(20)           // max players
            .String("default")
            .Bool(false)
            .ToArray());
    }

    private static void SendChat(PacketStream packets, string json)
    {
        packets.SendPacket(Protocol47.PlayIn.ChatMessage, new PacketWriter()
            .String(json)
            .SByte(0)
            .ToArray());
    }

    /// <summary>Reads client traffic until the socket closes, recording what arrived.</summary>
    private void PumpClientPackets(PacketStream packets, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] data;
            try { data = packets.ReadPacket(); }
            catch (Exception) { return; }   // client hung up

            var reader = new PacketReader(data);
            int id = reader.VarInt();

            switch (id)
            {
                case Protocol47.PlayOut.KeepAlive:
                    if (reader.VarInt() == 4242)
                    {
                        GotKeepAliveResponse = true;
                        KeepAliveAnswered.TrySetResult();
                    }
                    break;

                case Protocol47.PlayOut.ChatMessage:
                    ChatFromClient.Add(reader.String(100));
                    ChatReceived.TrySetResult();
                    break;

                case Protocol47.PlayOut.ClientSettings:
                    GotClientSettings = true;
                    break;

                case Protocol47.PlayOut.PluginMessage:
                    if (reader.String() == "MC|Brand") ClientBrand = reader.String();
                    break;

                default:
                    break;      // position and look updates are not interesting here
            }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _rsa.Dispose();
    }
}
