using System.Net;
using DnsClient;
using ThanosClient.Terminal;

namespace ThanosClient.Protocol;

/// <summary>
/// Where to connect, and what to claim in the handshake. These differ whenever a server
/// publishes an SRV record: the socket goes to the record's target, but the handshake
/// still names the address the user typed, because proxies such as BungeeCord route on
/// that field (forced hosts) and would otherwise not recognise the connection.
/// </summary>
public sealed record ServerAddress(string ConnectHost, ushort ConnectPort, string HandshakeHost)
{
    /// <summary>No SRV indirection: connect to exactly what was asked for.</summary>
    public static ServerAddress Direct(string host, ushort port) => new(host, port, host);

    /// <summary>True when an SRV record sent us somewhere other than the typed address.</summary>
    public bool WasRedirected => !string.Equals(ConnectHost, HandshakeHost, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        WasRedirected
            ? $"{HandshakeHost} -> {ConnectHost}:{ConnectPort}"
            : $"{ConnectHost}:{ConnectPort}";
}

/// <summary>
/// Minecraft's SRV convention: a domain may publish _minecraft._tcp.&lt;host&gt; pointing at
/// the host and port that actually run the server. The vanilla client follows it, so a
/// client that does not will fail on a large share of public servers - typically by
/// hanging, because the bare domain often resolves to a website behind a CDN.
/// </summary>
public static class SrvResolver
{
    /// <summary>The port at which we assume the user did not state one explicitly.</summary>
    public const ushort DefaultPort = 25565;

    /// <summary>
    /// Resolves <paramref name="host"/> through SRV when appropriate. An explicit port,
    /// or an IP literal, skips the lookup - both mean the caller already knows exactly
    /// where the server is, which is also how the vanilla client behaves.
    /// Any DNS failure falls back to a direct connection rather than erroring.
    /// </summary>
    public static async Task<ServerAddress> ResolveAsync(string host, ushort port, CancellationToken ct = default)
    {
        ServerAddress direct = ServerAddress.Direct(host, port);

        if (port != DefaultPort) return direct;
        if (IPAddress.TryParse(host, out _)) return direct;
        if (!host.Contains('.')) return direct;      // "localhost" and similar have no SRV record

        try
        {
            var lookup = new LookupClient(new LookupClientOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                UseCache = true,
                Retries = 1,
            });

            IDnsQueryResponse response = await lookup.QueryAsync(
                $"_minecraft._tcp.{host}", QueryType.SRV, cancellationToken: ct);

            DnsClient.Protocol.SrvRecord? record = response.Answers.SrvRecords()
                .OrderBy(r => r.Priority)
                .ThenByDescending(r => r.Weight)
                .FirstOrDefault();

            if (record is null) return direct;

            string target = record.Target.Value.TrimEnd('.');
            if (target.Length == 0) return direct;

            return new ServerAddress(target, record.Port, host);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing or broken SRV record is normal, not an error worth failing on.
            ConsoleIO.WriteDebug($"SRV lookup for {host} failed ({ex.Message}); connecting directly.");
            return direct;
        }
    }
}
