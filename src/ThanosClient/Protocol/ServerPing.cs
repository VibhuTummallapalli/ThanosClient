using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace ThanosClient.Protocol;

public sealed record ServerStatus(
    string Description,
    string VersionName,
    int ProtocolVersion,
    int OnlinePlayers,
    int MaxPlayers,
    long LatencyMs);

/// <summary>
/// The server list ping. Useful on its own for checking a server without logging in,
/// and it reports the server's real protocol version, which is the quickest way to
/// confirm something actually speaks 1.8.
/// </summary>
public static class ServerPing
{
    /// <summary>Pings a server, following its SRV record when it publishes one.</summary>
    public static async Task<ServerStatus> QueryAsync(string host, ushort port, int timeoutSeconds = 10, CancellationToken ct = default)
    {
        ServerAddress address = await SrvResolver.ResolveAsync(host, port, ct);
        return await QueryAsync(address, timeoutSeconds, ct);
    }

    public static async Task<ServerStatus> QueryAsync(ServerAddress address, int timeoutSeconds = 10, CancellationToken ct = default)
    {
        try
        {
            return await QueryCoreAsync(address, timeoutSeconds, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Without this the caller only sees "The operation was canceled", which says
            // nothing about what actually went wrong.
            throw new TimeoutException($"no response from {address} within {timeoutSeconds}s");
        }
    }

    private static async Task<ServerStatus> QueryCoreAsync(ServerAddress address, int timeoutSeconds, CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await tcp.ConnectAsync(address.ConnectHost, address.ConnectPort, timeout.Token);

        NetworkStream network = tcp.GetStream();
        network.ReadTimeout = timeoutSeconds * 1000;
        using var packets = new PacketStream(network);

        packets.SendPacket(Protocol47.Handshake, new PacketWriter()
            .VarInt(Protocol47.Version)
            .String(address.HandshakeHost)
            .UShort(address.ConnectPort)
            .VarInt((int)Protocol47.State.Status)
            .ToArray());

        packets.SendPacket(Protocol47.Status.Request, System.Array.Empty<byte>());

        var response = new PacketReader(packets.ReadPacket());
        int responseId = response.VarInt();
        if (responseId != Protocol47.Status.Response)
            throw new ProtocolException($"expected a status response, got 0x{responseId:X2}");

        string json = response.String();

        var stopwatch = Stopwatch.StartNew();
        packets.SendPacket(Protocol47.Status.Ping, new PacketWriter().Long(DateTime.UtcNow.Ticks).ToArray());
        var pong = new PacketReader(packets.ReadPacket());
        pong.VarInt();
        stopwatch.Stop();

        return ParseStatus(json, stopwatch.ElapsedMilliseconds);
    }

    private static ServerStatus ParseStatus(string json, long latencyMs)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string description = "";
        if (root.TryGetProperty("description", out JsonElement desc))
            description = Client.ChatParser.ParsePlain(desc.GetRawText());

        string versionName = "unknown";
        int protocol = -1;
        if (root.TryGetProperty("version", out JsonElement version))
        {
            if (version.TryGetProperty("name", out JsonElement name)) versionName = name.GetString() ?? versionName;
            if (version.TryGetProperty("protocol", out JsonElement proto)) protocol = proto.GetInt32();
        }

        int online = 0, max = 0;
        if (root.TryGetProperty("players", out JsonElement players))
        {
            if (players.TryGetProperty("online", out JsonElement o)) online = o.GetInt32();
            if (players.TryGetProperty("max", out JsonElement m)) max = m.GetInt32();
        }

        return new ServerStatus(description.Trim(), versionName, protocol, online, max, latencyMs);
    }
}
