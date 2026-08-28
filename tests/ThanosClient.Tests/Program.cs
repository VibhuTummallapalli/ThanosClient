using System.Security.Cryptography;
using ThanosClient.Auth;
using ThanosClient.Client;
using ThanosClient.Commands;
using ThanosClient.Config;
using ThanosClient.Protocol;
using ThanosClient.Terminal;

namespace ThanosClient.Tests;

public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main()
    {
        ConsoleIO.Initialize(enableColors: false);

        Section("data types");
        TestVarIntRoundTrip();
        TestNumericRoundTrip();

        Section("crypto");
        TestServerHashVectors();
        TestCfb8AgainstPlatformCipher();
        TestCfb8RoundTrip();
        TestCfb8ConcurrentDirections();
        TestOfflineUuidIsStable();

        Section("framing");
        TestPacketFraming(compressionThreshold: -1, encrypted: false);
        TestPacketFraming(compressionThreshold: 1, encrypted: false);
        TestPacketFraming(compressionThreshold: 256, encrypted: false);
        TestPacketFraming(compressionThreshold: 1, encrypted: true);

        Section("chat");
        TestChatParsing();

        Section("addresses");
        TestAddressParsing();

        Section("srv resolution");
        await TestSrvGating();

        Section("session path resolution");
        TestSessionPathPrecedence();

        Section("discord bridge");
        BridgeTests.Run(Check, (name, expected, actual) => Equal(name, expected, actual));

        Section("login against a loopback server");
        await TestLoginAsync(compression: -1, encryption: false);
        await TestLoginAsync(compression: 1, encryption: false);
        await TestLoginAsync(compression: 256, encryption: true);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // --- assertions ------------------------------------------------------------

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {name}");
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"   ok   {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"   FAIL {name}{(detail is null ? "" : ": " + detail)}");
        }
    }

    private static void Equal<T>(string name, T expected, T actual) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected [{expected}], got [{actual}]");

    private static void Skip(string name, string why)
    {
        Console.WriteLine($"   skip {name}: {why}");
    }

    // --- data types ------------------------------------------------------------

    private static void TestVarIntRoundTrip()
    {
        int[] values = { 0, 1, 2, 127, 128, 255, 256, 2097151, 2097152, 1073741823, int.MaxValue, -1, -2147483648 };

        foreach (int value in values)
        {
            byte[] encoded = new PacketWriter().VarInt(value).ToArray();
            int decoded = new PacketReader(encoded).VarInt();
            Equal($"varint {value}", value, decoded);
        }

        // Known encodings from the protocol specification.
        Check("varint 0 encodes to one byte", new PacketWriter().VarInt(0).ToArray().SequenceEqual(new byte[] { 0x00 }));
        Check("varint 128 encodes to 0x80 0x01", new PacketWriter().VarInt(128).ToArray().SequenceEqual(new byte[] { 0x80, 0x01 }));
        Check("varint -1 encodes to five bytes",
            new PacketWriter().VarInt(-1).ToArray().SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }));
    }

    private static void TestNumericRoundTrip()
    {
        byte[] data = new PacketWriter()
            .String("hello world")
            .Double(-1234.5678)
            .Float(0.5f)
            .Long(long.MinValue)
            .UShort(25565)
            .Bool(true)
            .Array(new byte[] { 1, 2, 3 })
            .ToArray();

        var reader = new PacketReader(data);
        Equal("string round trip", "hello world", reader.String());
        Equal("double round trip", -1234.5678, reader.Double());
        Equal("float round trip", 0.5f, reader.Float());
        Equal("long round trip", long.MinValue, reader.Long());
        Equal("ushort round trip", (ushort)25565, reader.UShort());
        Equal("bool round trip", true, reader.Bool());
        Check("byte array round trip", reader.Array().SequenceEqual(new byte[] { 1, 2, 3 }));
        Equal("reader fully consumed", 0, reader.Remaining);
    }

    // --- crypto ----------------------------------------------------------------

    private static void TestServerHashVectors()
    {
        // The three published vectors for Minecraft's signed-hex digest. "jeb_" is the
        // interesting one: its hash is negative, which a plain hex digest gets wrong.
        byte[] empty = System.Array.Empty<byte>();

        Equal("server hash Notch", "4ed1f46bbe04bc756bcb17c0c7ce3e4632f06a48",
            CryptoUtil.ServerHash("Notch", empty, empty));
        Equal("server hash jeb_ (negative)", "-7c9d5b0044c130109a5d7b5fb5c317c02b4e28c1",
            CryptoUtil.ServerHash("jeb_", empty, empty));
        Equal("server hash simon (leading zero trimmed)", "88e16a1019277b15d58faf0541e11910eb756f6",
            CryptoUtil.ServerHash("simon", empty, empty));
    }

    /// <summary>Checks the hand-rolled CFB8 against the platform's own AES-CFB8.</summary>
    private static void TestCfb8AgainstPlatformCipher()
    {
        byte[] secret = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        byte[] plaintext = RandomNumberGenerator.GetBytes(133);   // deliberately not a block multiple

        byte[] expected;
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CFB;
            aes.FeedbackSize = 8;
            aes.Padding = PaddingMode.None;
            aes.Key = secret;
            aes.IV = secret;
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            expected = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            Skip("CFB8 matches the platform cipher", "this platform has no native AES-CFB8");
            return;
        }

        var sink = new MemoryStream();
        var cipher = new AesCfb8Stream(sink, secret);
        cipher.Write(plaintext, 0, plaintext.Length);

        Check("CFB8 matches the platform cipher", sink.ToArray().SequenceEqual(expected),
            $"expected {Convert.ToHexString(expected)[..32]}..., got {Convert.ToHexString(sink.ToArray())[..32]}...");
    }

    private static void TestCfb8RoundTrip()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(16);
        byte[] plaintext = RandomNumberGenerator.GetBytes(1000);

        var sink = new MemoryStream();
        var encrypting = new AesCfb8Stream(sink, secret);

        // Write in uneven chunks: the feedback register must carry across calls.
        encrypting.Write(plaintext, 0, 1);
        encrypting.Write(plaintext, 1, 16);
        encrypting.Write(plaintext, 17, plaintext.Length - 17);

        byte[] ciphertext = sink.ToArray();
        Check("ciphertext differs from plaintext", !ciphertext.SequenceEqual(plaintext));

        var decrypting = new AesCfb8Stream(new MemoryStream(ciphertext), secret);
        byte[] decrypted = new byte[plaintext.Length];
        int read = 0;
        while (read < decrypted.Length)
        {
            int got = decrypting.Read(decrypted, read, Math.Min(37, decrypted.Length - read));
            if (got <= 0) break;
            read += got;
        }

        Check("CFB8 round trips", decrypted.SequenceEqual(plaintext));
    }

    /// <summary>
    /// Reads and writes run on separate threads on a live connection. If the two
    /// directions share any cipher state, the streams corrupt each other - which shows
    /// up as a client that logs in and then silently stops decoding packets.
    /// </summary>
    private static void TestCfb8ConcurrentDirections()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(16);
        byte[] inbound = RandomNumberGenerator.GetBytes(20000);
        byte[] outbound = RandomNumberGenerator.GetBytes(20000);

        // Reference values from single-direction ciphers, computed before any threading.
        var inboundCipherSink = new MemoryStream();
        new AesCfb8Stream(inboundCipherSink, secret).Write(inbound, 0, inbound.Length);
        byte[] inboundCiphertext = inboundCipherSink.ToArray();

        var outboundCipherSink = new MemoryStream();
        new AesCfb8Stream(outboundCipherSink, secret).Write(outbound, 0, outbound.Length);
        byte[] expectedOutboundCiphertext = outboundCipherSink.ToArray();

        var sentBytes = new MemoryStream();
        var duplex = new AesCfb8Stream(new SplitStream(new MemoryStream(inboundCiphertext), sentBytes), secret);

        byte[] decrypted = new byte[inbound.Length];

        var reader = new Thread(() =>
        {
            int read = 0;
            while (read < decrypted.Length)
            {
                int got = duplex.Read(decrypted, read, Math.Min(64, decrypted.Length - read));
                if (got <= 0) break;
                read += got;
            }
        });

        var writer = new Thread(() =>
        {
            for (int i = 0; i < outbound.Length; i += 64)
                duplex.Write(outbound, i, Math.Min(64, outbound.Length - i));
        });

        reader.Start();
        writer.Start();
        reader.Join(TimeSpan.FromSeconds(30));
        writer.Join(TimeSpan.FromSeconds(30));

        Check("concurrent CFB8: decryption is uncorrupted", decrypted.SequenceEqual(inbound));
        Check("concurrent CFB8: encryption is uncorrupted", sentBytes.ToArray().SequenceEqual(expectedOutboundCiphertext));
    }

    private static void TestOfflineUuidIsStable()
    {
        Guid first = CryptoUtil.OfflineUuid("Notch");
        Guid second = CryptoUtil.OfflineUuid("Notch");

        Equal("offline uuid is deterministic", first, second);
        Check("offline uuid is name-based (version 3)", (first.ToByteArray(bigEndian: true)[6] >> 4) == 3);
        Check("offline uuid varies by name", CryptoUtil.OfflineUuid("jeb_") != first);
    }

    // --- framing ---------------------------------------------------------------

    private static void TestPacketFraming(int compressionThreshold, bool encrypted)
    {
        string label = $"framing (compression {compressionThreshold}, {(encrypted ? "encrypted" : "plain")})";

        byte[] secret = RandomNumberGenerator.GetBytes(16);
        byte[] smallPayload = new PacketWriter().String("ping").ToArray();
        byte[] largePayload = new PacketWriter().String(new string('x', 900)).ToArray();

        var wire = new MemoryStream();
        var sender = new PacketStream(wire);
        if (encrypted) sender.EnableEncryption(secret);
        if (compressionThreshold >= 0) sender.SetCompressionThreshold(compressionThreshold);

        sender.SendPacket(0x02, smallPayload);
        sender.SendPacket(0x40, largePayload);

        var receiver = new PacketStream(new MemoryStream(wire.ToArray()));
        if (encrypted) receiver.EnableEncryption(secret);
        if (compressionThreshold >= 0) receiver.SetCompressionThreshold(compressionThreshold);

        var first = new PacketReader(receiver.ReadPacket());
        Equal($"{label}: first packet id", 0x02, first.VarInt());
        Equal($"{label}: first payload", "ping", first.String());

        var second = new PacketReader(receiver.ReadPacket());
        Equal($"{label}: second packet id", 0x40, second.VarInt());
        Equal($"{label}: second payload length", 900, second.String().Length);
    }

    // --- session path ----------------------------------------------------------

    /// <summary>
    /// The container relies on the environment override to keep the cached token on its
    /// volume. Getting this wrong is silent - the bot works, then needs a fresh
    /// interactive sign-in after every redeploy - so the precedence is pinned here.
    /// </summary>
    private static void TestSessionPathPrecedence()
    {
        string? original = Environment.GetEnvironmentVariable(AccountSettings.SessionPathEnvironmentVariable);

        try
        {
            var settings = new AccountSettings();

            Environment.SetEnvironmentVariable(AccountSettings.SessionPathEnvironmentVariable, null);
            Equal("no config and no environment leaves it empty", "", settings.EffectiveSessionCachePath);

            settings.SessionCachePath = "/from/config.json";
            Equal("config is used when the environment is unset", "/from/config.json", settings.EffectiveSessionCachePath);

            Environment.SetEnvironmentVariable(AccountSettings.SessionPathEnvironmentVariable, "/data/session.json");
            Equal("the environment wins over config", "/data/session.json", settings.EffectiveSessionCachePath);

            Environment.SetEnvironmentVariable(AccountSettings.SessionPathEnvironmentVariable, "   ");
            Equal("a blank environment value falls back to config", "/from/config.json", settings.EffectiveSessionCachePath);

            Environment.SetEnvironmentVariable(AccountSettings.SessionPathEnvironmentVariable, "  /data/session.json  ");
            Equal("the environment value is trimmed", "/data/session.json", settings.EffectiveSessionCachePath);

            Check("the path is not serialised into the config file",
                !System.Text.Json.JsonSerializer.Serialize(settings).Contains("EffectiveSessionCachePath"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AccountSettings.SessionPathEnvironmentVariable, original);
        }
    }

    // --- srv -------------------------------------------------------------------

    /// <summary>
    /// Only the cases that need no network are asserted, so the suite stays offline and
    /// deterministic. What matters here is when the lookup is skipped: a wrong answer
    /// means either a pointless DNS round trip or connecting to the wrong machine.
    /// </summary>
    private static async Task TestSrvGating()
    {
        ServerAddress explicitPort = await SrvResolver.ResolveAsync("example.com", 25566);
        Equal("an explicit port skips the lookup", "example.com", explicitPort.ConnectHost);
        Equal("an explicit port is preserved", (ushort)25566, explicitPort.ConnectPort);
        Check("an explicit port is not a redirect", !explicitPort.WasRedirected);

        ServerAddress ipv4 = await SrvResolver.ResolveAsync("127.0.0.1", 25565);
        Equal("an IP literal skips the lookup", "127.0.0.1", ipv4.ConnectHost);
        Check("an IP literal is not a redirect", !ipv4.WasRedirected);

        ServerAddress ipv6 = await SrvResolver.ResolveAsync("::1", 25565);
        Equal("an IPv6 literal skips the lookup", "::1", ipv6.ConnectHost);

        ServerAddress bare = await SrvResolver.ResolveAsync("localhost", 25565);
        Equal("a name with no dot skips the lookup", "localhost", bare.ConnectHost);

        ServerAddress direct = ServerAddress.Direct("play.example.com", 25565);
        Equal("a direct address handshakes with itself", "play.example.com", direct.HandshakeHost);
        Check("a direct address is not a redirect", !direct.WasRedirected);

        // The handshake keeps the typed hostname so proxy forced-host routing still works.
        var redirected = new ServerAddress("node3.host.net", 25581, "play.example.com");
        Check("a redirect is reported as one", redirected.WasRedirected);
        Equal("a redirect connects to the SRV target", "node3.host.net", redirected.ConnectHost);
        Equal("a redirect handshakes with the typed host", "play.example.com", redirected.HandshakeHost);
        Check("a redirect describes both ends", redirected.ToString().Contains("->"), redirected.ToString());
    }

    // --- chat ------------------------------------------------------------------

    private static void TestChatParsing()
    {
        Equal("translate chat.type.text",
            "<Notch> hello there",
            ChatParser.ParsePlain("{\"translate\":\"chat.type.text\",\"with\":[{\"text\":\"Notch\"},{\"text\":\"hello there\"}]}"));

        Equal("translate multiplayer.player.joined",
            "Notch joined the game",
            ChatParser.ParsePlain("{\"translate\":\"multiplayer.player.joined\",\"with\":[{\"text\":\"Notch\"}]}"));

        Equal("nested extra components",
            "hello world",
            ChatParser.ParsePlain("{\"text\":\"hello \",\"extra\":[{\"text\":\"world\",\"color\":\"red\"}]}"));

        Equal("array of components",
            "ab",
            ChatParser.ParsePlain("[{\"text\":\"a\"},{\"text\":\"b\"}]"));

        Equal("legacy codes stripped from json text",
            "red text",
            ChatParser.ParsePlain("{\"text\":\"\u00a7cred \u00a7ftext\"}"));

        Equal("bare legacy string is not json",
            "green",
            ChatParser.ParsePlain("\u00a7agreen"));

        Equal("unknown translation key falls back to its arguments",
            "one two",
            ChatParser.ParsePlain("{\"translate\":\"some.mod.key\",\"with\":[{\"text\":\"one\"},{\"text\":\"two\"}]}"));

        string coloured = ChatParser.Parse("{\"text\":\"hi\",\"color\":\"red\"}", withColor: true);
        Check("colour output contains an ANSI sequence", coloured.Contains(ConsoleIO.Esc + "["));
        Check("colour output still contains the text", coloured.Contains("hi"));
    }

    // --- addresses -------------------------------------------------------------

    private static void TestAddressParsing()
    {
        Check("plain host", CommandHandler.TryParseAddress("play.example.com", out string h1, out ushort p1) && h1 == "play.example.com" && p1 == 25565);
        Check("host with port", CommandHandler.TryParseAddress("play.example.com:25566", out string h2, out ushort p2) && h2 == "play.example.com" && p2 == 25566);
        Check("ipv4 with port", CommandHandler.TryParseAddress("127.0.0.1:25565", out string h3, out ushort p3) && h3 == "127.0.0.1" && p3 == 25565);
        Check("bracketed ipv6", CommandHandler.TryParseAddress("[::1]:25565", out string h4, out ushort p4) && h4 == "::1" && p4 == 25565);
        Check("bracketed ipv6 without port", CommandHandler.TryParseAddress("[::1]", out string h5, out _) && h5 == "::1");
        Check("rejects a bad port", !CommandHandler.TryParseAddress("host:notaport", out _, out _));
    }

    // --- end to end ------------------------------------------------------------

    private static async Task TestLoginAsync(int compression, bool encryption)
    {
        string label = $"login (compression {compression}, {(encryption ? "encrypted" : "plain")})";

        using var server = new FakeServer { CompressionThreshold = compression, RequireEncryption = encryption };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task serverTask = Task.Run(async () =>
        {
            try { await server.RunAsync(cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        });

        var settings = new Settings();
        settings.Console.Colors = false;
        settings.Console.Timestamps = false;
        settings.Server.ClientBrand = "thanos-test";

        Session session = encryption
            ? new Session
            {
                Username = "TestPlayer",
                Uuid = "069a79f4-44e9-4726-a5be-fca90e38aaf5",
                AccessToken = "test-access-token",
                Offline = false,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            }
            : Session.ForOffline("TestPlayer");

        var recorder = new RecordingBot();
        var sessionServer = new StubSessionServer();

        using var client = new McClient(settings, session, new[] { recorder }, sessionServer);

        bool connected = await client.ConnectAsync("127.0.0.1", server.Port, cts.Token);
        Check($"{label}: connected", connected);
        if (!connected)
        {
            cts.Cancel();
            return;
        }

        bool joined = await Waited(recorder.Joined.Task);
        Check($"{label}: reached the play state", joined);

        Equal($"{label}: handshake protocol", Protocol47.Version, server.HandshakeProtocol);
        Equal($"{label}: handshake host", "127.0.0.1", server.HandshakeHost);
        Equal($"{label}: handshake port", server.Port, server.HandshakePort);
        Equal($"{label}: login username", "TestPlayer", server.LoginUsername);

        Check($"{label}: server chat was decoded", await Waited(recorder.ChatSeen.Task));
        Equal($"{label}: chat text", "<Notch> hello there", recorder.LastChat);

        Check($"{label}: client chat reached the server", await Waited(server.ChatReceived.Task));
        Check($"{label}: chat contents", server.ChatFromClient.Contains("hello from the client"));

        Check($"{label}: keep-alive was answered", await Waited(server.KeepAliveAnswered.Task));
        Check($"{label}: client settings were sent", server.GotClientSettings);
        Equal($"{label}: client brand", "thanos-test", server.ClientBrand);

        if (encryption)
        {
            Check($"{label}: verify token matched", server.VerifyTokenMatched);
            Equal($"{label}: shared secret is 16 bytes", 16, server.SharedSecret?.Length ?? 0);
            Check($"{label}: session server was told a hash", !string.IsNullOrEmpty(sessionServer.LastHash));
            Equal($"{label}: session server got the profile uuid", "069a79f444e94726a5befca90e38aaf5", sessionServer.LastProfile);
        }

        client.Disconnect("test over");
        cts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(2000));
    }

    private static async Task<bool> Waited(Task task, int timeoutMs = 10000) =>
        await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;
}
