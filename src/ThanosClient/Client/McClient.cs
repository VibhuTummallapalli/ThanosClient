using System.Net.Sockets;
using ThanosClient.Auth;
using ThanosClient.Bots;
using ThanosClient.Config;
using ThanosClient.Protocol;
using ThanosClient.Terminal;

namespace ThanosClient.Client;

/// <summary>
/// A headless Minecraft client speaking protocol 47 (1.8 - 1.8.9). It owns the socket,
/// runs the login sequence, then pumps play packets on a background thread and fans the
/// interesting ones out to the attached bots.
/// </summary>
public sealed class McClient : IDisposable
{
    private readonly Settings _settings;
    private readonly Session _session;
    private readonly List<ChatBot> _bots;
    private readonly SessionServer _sessionServer;

    private TcpClient? _tcp;
    private NetworkStream? _network;
    private PacketStream? _packets;
    private Thread? _receiveThread;
    private Thread? _tickThread;

    private volatile bool _running;
    private volatile bool _joined;
    private int _disconnectRaised;

    /// <summary>Idle read timeout during play. Vanilla 1.8 sends keep-alives every 20s.</summary>
    private static readonly TimeSpan PlayReadTimeout = TimeSpan.FromSeconds(60);

    /// <param name="sessionServer">
    /// Overridable so tests can stand in for Mojang's session server; production code
    /// leaves this null and gets the real one.
    /// </param>
    public McClient(Settings settings, Session session, IEnumerable<ChatBot> bots, SessionServer? sessionServer = null)
    {
        _settings = settings;
        _session = session;
        _sessionServer = sessionServer ?? new SessionServer();
        _bots = bots.ToList();
        foreach (ChatBot bot in _bots) bot.Attach(this);
    }

    public string Username => _session.Username;
    public string Host { get; private set; } = "";
    public ushort Port { get; private set; }

    /// <summary>The address currently in use, including any SRV redirection.</summary>
    public ServerAddress? Address { get; private set; }
    public PlayerList Players { get; } = new();
    public Location? CurrentLocation { get; private set; }
    public int EntityId { get; private set; }
    public string? GameMode { get; private set; }
    public float Health { get; private set; } = 20f;
    public bool IsConnected => _running;
    public bool IsInGame => _running && _joined;
    public IReadOnlyList<ChatBot> Bots => _bots;

    /// <summary>True when a bot took responsibility for reconnecting after the last disconnect.</summary>
    public bool DisconnectHandledByBot { get; private set; }

    /// <summary>Raised exactly once per connection, after the socket is torn down.</summary>
    public event Action<DisconnectReason, string>? Disconnected;

    /// <summary>
    /// Connects, authenticates, and blocks until login succeeds or fails. Follows the
    /// server's SRV record when it publishes one, as the vanilla client does.
    /// </summary>
    public async Task<bool> ConnectAsync(string host, ushort port, CancellationToken ct = default)
    {
        ServerAddress resolved = await SrvResolver.ResolveAsync(host, port, ct);

        if (resolved.WasRedirected)
            ConsoleIO.WriteInfo($"SRV record points {host} at {resolved.ConnectHost}:{resolved.ConnectPort}.");

        return await ConnectAsync(resolved, ct);
    }

    public async Task<bool> ConnectAsync(ServerAddress address, CancellationToken ct = default)
    {
        string host = address.ConnectHost;
        ushort port = address.ConnectPort;

        Address = address;
        Host = host;
        Port = port;
        _disconnectRaised = 0;

        try
        {
            _tcp = new TcpClient { NoDelay = true };

            int timeoutSeconds = Math.Max(1, _settings.Server.ConnectTimeoutSeconds);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            ConsoleIO.WriteInfo($"Connecting to {host}:{port} ...");
            await _tcp.ConnectAsync(host, port, timeout.Token);

            _network = _tcp.GetStream();
            _network.ReadTimeout = timeoutSeconds * 1000;
            _packets = new PacketStream(_network);

            await PerformLoginAsync(address, ct);

            _network.ReadTimeout = (int)PlayReadTimeout.TotalMilliseconds;
            _running = true;

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "mc-receive" };
            _receiveThread.Start();

            _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "mc-tick" };
            _tickThread.Start();

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail($"Timed out connecting to {host}:{port}");
            return false;
        }
        catch (SocketException ex)
        {
            Fail($"Could not reach {host}:{port} ({ex.SocketErrorCode})");
            return false;
        }
        catch (AuthException ex)
        {
            Fail(ex.Message);
            return false;
        }
        catch (LoginRejectedException ex)
        {
            Fail(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Fail($"Login failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reports a failure during connect/login and tears the socket back down.</summary>
    private void Fail(string message)
    {
        CloseSocket();
        RaiseDisconnected(DisconnectReason.LoginFailed, message);
    }

    /// <summary>Handshake, login start, optional encryption and compression, login success.</summary>
    private async Task PerformLoginAsync(ServerAddress address, CancellationToken ct)
    {
        PacketStream packets = _packets!;

        // The handshake names the address the user typed, not the SRV target, so proxy
        // forced-host routing still sees the hostname it expects.
        packets.SendPacket(Protocol47.Handshake, new PacketWriter()
            .VarInt(Protocol47.Version)
            .String(address.HandshakeHost)
            .UShort(address.ConnectPort)
            .VarInt((int)Protocol47.State.Login)
            .ToArray());

        packets.SendPacket(Protocol47.LoginOut.LoginStart,
            new PacketWriter().String(_session.Username).ToArray());

        while (true)
        {
            byte[] data = packets.ReadPacket();
            var reader = new PacketReader(data);
            int id = reader.VarInt();

            switch (id)
            {
                case Protocol47.LoginIn.Disconnect:
                {
                    string reason = ChatParser.ParsePlain(reader.String());
                    throw new LoginRejectedException($"Server refused the connection: {reason}");
                }

                case Protocol47.LoginIn.EncryptionRequest:
                    await HandleEncryptionRequestAsync(reader, ct);
                    break;

                case Protocol47.LoginIn.SetCompression:
                {
                    int threshold = reader.VarInt();
                    packets.SetCompressionThreshold(threshold);
                    if (_settings.Console.DebugPackets)
                        ConsoleIO.WriteDebug($"compression enabled, threshold {threshold}");
                    break;
                }

                case Protocol47.LoginIn.LoginSuccess:
                {
                    string uuid = reader.String(36);
                    string name = reader.String(16);
                    ConsoleIO.WriteSuccess($"Logged in as {name} ({uuid})");
                    return;
                }

                default:
                    throw new ProtocolException($"unexpected login packet 0x{id:X2}");
            }
        }
    }

    /// <summary>
    /// Online-mode encryption: tell the session server which hash we are about to join
    /// with, send the RSA-wrapped shared secret, then switch the transport to AES-CFB8.
    /// </summary>
    private async Task HandleEncryptionRequestAsync(PacketReader reader, CancellationToken ct)
    {
        string serverId = reader.String(20);
        byte[] publicKey = reader.Array();
        byte[] verifyToken = reader.Array();

        if (_session.Offline)
            throw new LoginRejectedException(
                "This server is in online mode, but the client is configured for offline login. " +
                "Set account.mode to microsoft in the config.");

        byte[] sharedSecret = CryptoUtil.GenerateSharedSecret();
        string hash = CryptoUtil.ServerHash(serverId, sharedSecret, publicKey);

        await _sessionServer.JoinAsync(_session, hash, ct);

        _packets!.SendPacket(Protocol47.LoginOut.EncryptionResponse, new PacketWriter()
            .Array(CryptoUtil.RsaEncrypt(publicKey, sharedSecret))
            .Array(CryptoUtil.RsaEncrypt(publicKey, verifyToken))
            .ToArray());

        _packets.EnableEncryption(sharedSecret);

        if (_settings.Console.DebugPackets)
            ConsoleIO.WriteDebug("encryption enabled (AES-128/CFB8)");
    }

    // --- packet pump -----------------------------------------------------------

    private void ReceiveLoop()
    {
        try
        {
            while (_running)
            {
                byte[] data = _packets!.ReadPacket();
                var reader = new PacketReader(data);
                int id = reader.VarInt();

                if (_settings.Console.DebugPackets)
                    ConsoleIO.WriteDebug($"recv 0x{id:X2} ({data.Length} bytes)");

                try
                {
                    HandlePlayPacket(id, reader);
                }
                catch (ProtocolException ex)
                {
                    // One malformed packet should not kill the session.
                    ConsoleIO.WriteWarning($"Could not parse packet 0x{id:X2}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Closing the socket from Disconnect() unblocks the read with an IOException.
            // That is our own teardown, not a failure, so it must not be reported or rethrown.
            if (!_running) return;

            string message = ex switch
            {
                EndOfStreamException => "The server closed the connection.",
                IOException io when io.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut
                    => "No keep-alive from the server for 60s; assuming the connection is dead.",
                IOException io => $"Connection error: {io.Message}",
                _ => $"Connection error: {ex.Message}",
            };

            _running = false;
            CloseSocket();
            RaiseDisconnected(DisconnectReason.ConnectionLost, message);
        }
    }

    private void HandlePlayPacket(int id, PacketReader reader)
    {
        switch (id)
        {
            case Protocol47.PlayIn.KeepAlive:
                _packets!.SendPacket(Protocol47.PlayOut.KeepAlive,
                    new PacketWriter().VarInt(reader.VarInt()).ToArray());
                break;

            case Protocol47.PlayIn.JoinGame:
                HandleJoinGame(reader);
                break;

            case Protocol47.PlayIn.ChatMessage:
                HandleChat(reader);
                break;

            case Protocol47.PlayIn.PlayerPositionAndLook:
                HandlePositionAndLook(reader);
                break;

            case Protocol47.PlayIn.UpdateHealth:
                HandleUpdateHealth(reader);
                break;

            case Protocol47.PlayIn.Respawn:
                reader.Int();                       // dimension
                reader.Byte();                      // difficulty
                GameMode = DescribeGameMode(reader.Byte());
                CurrentLocation = null;             // a fresh position packet always follows
                ConsoleIO.WriteInfo("Respawned.");
                break;

            case Protocol47.PlayIn.PlayerListItem:
                HandlePlayerListItem(reader);
                break;

            case Protocol47.PlayIn.PlayerListHeaderFooter:
                Players.Header = ChatParser.ParsePlain(reader.String());
                Players.Footer = ChatParser.ParsePlain(reader.String());
                break;

            case Protocol47.PlayIn.SetCompression:
                _packets!.SetCompressionThreshold(reader.VarInt());
                break;

            case Protocol47.PlayIn.Disconnect:
            {
                string reason = ChatParser.ParsePlain(reader.String());
                _running = false;
                CloseSocket();
                RaiseDisconnected(DisconnectReason.InGameKick, reason);
                break;
            }

            default:
                break;   // everything else (world, entities, inventory) is not modelled
        }
    }

    private void HandleJoinGame(PacketReader reader)
    {
        EntityId = reader.Int();
        GameMode = DescribeGameMode(reader.Byte());
        reader.SByte();                             // dimension
        reader.Byte();                              // difficulty
        reader.Byte();                              // max players
        string levelType = reader.String(16);

        _joined = true;
        Players.Clear();

        // Vanilla sends these immediately after joining; some plugins expect them.
        SendClientSettings();
        SendBrand();

        ConsoleIO.WriteSuccess($"Joined the game as {Username} ({GameMode}, {levelType}).");
        NotifyBots(bot => bot.OnJoinedGame());
    }

    private void HandleChat(PacketReader reader)
    {
        string json = reader.String();
        var position = (ChatPosition)reader.SByte();

        string plain = ChatParser.ParsePlain(json);
        if (string.IsNullOrWhiteSpace(plain)) return;

        if (position != ChatPosition.ActionBar)
        {
            string display = ChatParser.Parse(json, ConsoleIO.ColorsEnabled);
            ConsoleIO.WriteLine(_settings.Console.Timestamps
                ? $"[{DateTime.Now:HH:mm:ss}] {display}"
                : display);
        }

        NotifyBots(bot => bot.OnChat(plain, json, position));
    }

    /// <summary>
    /// The server's authoritative position. In 1.8 the flags byte marks each field as
    /// relative, and the client must echo the corrected position straight back.
    /// </summary>
    private void HandlePositionAndLook(PacketReader reader)
    {
        double x = reader.Double();
        double y = reader.Double();
        double z = reader.Double();
        float yaw = reader.Float();
        float pitch = reader.Float();
        byte flags = reader.Byte();

        Location current = CurrentLocation ?? new Location();

        current.X = (flags & 0x01) != 0 ? current.X + x : x;
        current.Y = (flags & 0x02) != 0 ? current.Y + y : y;
        current.Z = (flags & 0x04) != 0 ? current.Z + z : z;
        current.Yaw = (flags & 0x08) != 0 ? current.Yaw + yaw : yaw;
        current.Pitch = (flags & 0x10) != 0 ? current.Pitch + pitch : pitch;

        CurrentLocation = current;
        SendPositionAndLook(current, onGround: true);
    }

    private void HandleUpdateHealth(PacketReader reader)
    {
        Health = reader.Float();
        reader.VarInt();      // food
        reader.Float();       // saturation

        if (Health > 0f) return;

        ConsoleIO.WriteWarning("Died; respawning.");
        _packets!.SendPacket(Protocol47.PlayOut.ClientStatus, new PacketWriter().VarInt(0).ToArray());
    }

    private void HandlePlayerListItem(PacketReader reader)
    {
        (List<PlayerInfo> added, List<PlayerInfo> removed) = Players.Apply(reader);

        // The initial tab list arrives in one burst at join time; announcing it is noise.
        if (!_joined) return;

        foreach (PlayerInfo player in added) NotifyBots(bot => bot.OnPlayerJoin(player));
        foreach (PlayerInfo player in removed) NotifyBots(bot => bot.OnPlayerLeave(player));
    }

    private static string DescribeGameMode(byte raw) => (raw & 0x07) switch
    {
        0 => "survival",
        1 => "creative",
        2 => "adventure",
        3 => "spectator",
        _ => "unknown",
    };

    // --- outgoing --------------------------------------------------------------

    /// <summary>Sends a chat line. 1.8 caps messages at 100 characters, so longer text is split.</summary>
    public void SendChat(string message)
    {
        if (!IsInGame)
        {
            ConsoleIO.WriteWarning("Not in game yet; message not sent.");
            return;
        }

        // Sanitise here rather than at each call site: console input, bots and the
        // Discord bridge all funnel through this method, and one bad character is a kick.
        string safe = ChatSanitizer.Sanitize(message);
        if (safe.Length == 0) return;

        foreach (string chunk in SplitForChat(safe))
            _packets!.SendPacket(Protocol47.PlayOut.ChatMessage, new PacketWriter().String(chunk).ToArray());
    }

    private static IEnumerable<string> SplitForChat(string message)
    {
        const int limit = 100;

        if (message.Length <= limit)
        {
            if (message.Length > 0) yield return message;
            yield break;
        }

        for (int i = 0; i < message.Length; i += limit)
            yield return message.Substring(i, Math.Min(limit, message.Length - i));
    }

    /// <summary>Locale, view distance and skin settings, as vanilla sends after joining.</summary>
    public void SendClientSettings()
    {
        _packets!.SendPacket(Protocol47.PlayOut.ClientSettings, new PacketWriter()
            .String("en_GB")
            .SByte(8)          // view distance in chunks
            .SByte(0)          // chat mode: enabled
            .Bool(true)        // chat colours
            .Byte(0x7F)        // all skin parts shown
            .ToArray());
    }

    /// <summary>The MC|Brand plugin message. Some servers log or gate on this.</summary>
    public void SendBrand()
    {
        byte[] brand = new PacketWriter().String(_settings.Server.ClientBrand).ToArray();
        _packets!.SendPacket(Protocol47.PlayOut.PluginMessage, new PacketWriter()
            .String("MC|Brand")
            .Raw(brand)
            .ToArray());
    }

    public void SendPositionAndLook(Location location, bool onGround)
    {
        if (!IsInGame) return;

        _packets!.SendPacket(Protocol47.PlayOut.PlayerPositionAndLook, new PacketWriter()
            .Double(location.X)
            .Double(location.Y)
            .Double(location.Z)
            .Float(location.Yaw)
            .Float(location.Pitch)
            .Bool(onGround)
            .ToArray());

        CurrentLocation = location;
    }

    /// <summary>Swings the arm. Cheap, visible activity for anti-idle purposes.</summary>
    public void SendSwingArm()
    {
        if (IsInGame) _packets!.SendPacket(Protocol47.PlayOut.Animation, System.Array.Empty<byte>());
    }

    /// <summary>The bare "still here, on the ground" packet vanilla sends every tick.</summary>
    private void SendIdlePosition()
    {
        if (!IsInGame) return;
        _packets!.SendPacket(Protocol47.PlayOut.Player, new PacketWriter().Bool(true).ToArray());
    }

    // --- timing ----------------------------------------------------------------

    private void TickLoop()
    {
        DateTime lastIdlePacket = DateTime.MinValue;

        while (_running)
        {
            try
            {
                if (_joined && DateTime.UtcNow - lastIdlePacket >= TimeSpan.FromSeconds(1))
                {
                    SendIdlePosition();
                    lastIdlePacket = DateTime.UtcNow;
                }

                if (_joined) NotifyBots(bot => bot.OnUpdate());
            }
            catch (Exception ex)
            {
                if (!_running) return;
                ConsoleIO.WriteWarning($"Tick error: {ex.Message}");
            }

            Thread.Sleep(100);
        }
    }

    private void NotifyBots(Action<ChatBot> action)
    {
        foreach (ChatBot bot in _bots)
        {
            if (!bot.Enabled) continue;

            try { action(bot); }
            catch (Exception ex) { ConsoleIO.WriteError($"[{bot.Name}] {ex.Message}"); }
        }
    }

    // --- teardown --------------------------------------------------------------

    /// <summary>Leaves the server. Safe to call more than once.</summary>
    public void Disconnect(string reason = "Disconnected by user")
    {
        if (!_running && _disconnectRaised != 0) return;

        _running = false;
        CloseSocket();
        RaiseDisconnected(DisconnectReason.UserRequested, reason);
    }

    /// <summary>Asks the host application to reconnect. Used by the auto-relog bot.</summary>
    public void RequestReconnect() => ReconnectRequested?.Invoke();

    public event Action? ReconnectRequested;

    private void CloseSocket()
    {
        _joined = false;

        try { _packets?.Dispose(); } catch { /* already gone */ }
        try { _tcp?.Close(); } catch { /* already gone */ }

        _packets = null;
        _network = null;
        _tcp = null;
    }

    /// <summary>
    /// Fires the disconnect notification exactly once per connection. Bots are asked
    /// first, so the host application can see whether one of them owns the reconnect.
    /// </summary>
    private void RaiseDisconnected(DisconnectReason reason, string message)
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0) return;

        Players.Clear();
        CurrentLocation = null;

        bool handled = false;
        foreach (ChatBot bot in _bots)
        {
            if (!bot.Enabled) continue;

            try { handled |= bot.OnDisconnect(reason, message); }
            catch (Exception ex) { ConsoleIO.WriteError($"[{bot.Name}] {ex.Message}"); }
        }

        DisconnectHandledByBot = handled;
        Disconnected?.Invoke(reason, message);
    }

    public void Dispose()
    {
        _running = false;
        CloseSocket();
    }
}

/// <summary>The server declined the login, with a reason worth showing the user.</summary>
public class LoginRejectedException : Exception
{
    public LoginRejectedException(string message) : base(message) { }
}
