using ThanosClient.Auth;
using ThanosClient.Bots;
using ThanosClient.Bridge;
using ThanosClient.Client;
using ThanosClient.Commands;
using ThanosClient.Config;
using ThanosClient.Protocol;
using ThanosClient.Terminal;

namespace ThanosClient;

public static class Program
{
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly ManualResetEventSlim Wake = new(false);
    private static volatile bool _quit;

    public static async Task<int> Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);

        if (options.ShowHelp)
        {
            CommandLineOptions.PrintUsage();
            return 0;
        }

        Settings settings;
        try
        {
            settings = Settings.Load(options.ConfigPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not load {options.ConfigPath}: {ex.Message}");
            return 1;
        }

        options.ApplyTo(settings);
        ConsoleIO.Initialize(settings.Console.Colors);

        ConsoleIO.WriteInfo($"ThanosClient - console client for Minecraft {Protocol47.VersionName} (protocol {Protocol47.Version})");

        (string host, ushort port) = ResolveServer(settings);
        if (host.Length == 0)
        {
            ConsoleIO.WriteError("No server configured. Pass --host <address> or set server.host in the config.");
            return 1;
        }

        if (options.PingOnly)
            return await PingOnlyAsync(host, port);

        Session? session;
        try
        {
            session = await ResolveSessionAsync(settings, options.ForceLogin, Shutdown.Token);
        }
        catch (AuthException ex)
        {
            ConsoleIO.WriteError(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }

        if (session is null) return 1;

        ConsoleIO.WriteInfo(session.Offline
            ? $"Using offline account \"{session.Username}\"."
            : $"Signed in as {session.Username}.");

        return await RunAsync(settings, session, host, port);
    }

    private static async Task<int> RunAsync(Settings settings, Session session, string host, ushort port)
    {
        // The relay is an ordinary bot, so it has to exist before the client is built.
        // The bridge is built afterwards, because it needs the client.
        GameChatRelay? relay = settings.Discord.Enabled ? new GameChatRelay(settings.Discord) : null;

        List<ChatBot> bots = BuildBots(settings);
        if (relay is not null) bots.Insert(0, relay);

        using var client = new McClient(settings, session, bots);

        var commands = new CommandHandler(client, settings, RequestQuit, RequestReconnect);

        client.Disconnected += (reason, message) => OnDisconnected(client, reason, message);
        client.ReconnectRequested += RequestReconnect;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ConsoleIO.WriteInfo("Shutting down...");
            RequestQuit();
        };

        ConsoleIO.StartReading(line =>
        {
            try { commands.Handle(line); }
            catch (Exception ex) { ConsoleIO.WriteError(ex.Message); }
        }, settings.Console.Prompt);

        DiscordBridge? bridge = null;
        if (relay is not null)
        {
            bridge = new DiscordBridge(settings, client, relay, RequestReconnect);

            // A bridge that fails to start is not fatal: the console client still works.
            if (!await bridge.StartAsync())
            {
                await bridge.DisposeAsync();
                bridge = null;
            }
        }

        try
        {
            while (!_quit)
            {
                Wake.Reset();

                bool connected = await client.ConnectAsync(host, port, Shutdown.Token);

                if (!connected && !client.DisconnectHandledByBot)
                {
                    ConsoleIO.WriteError("Could not connect and no bot is retrying; exiting.");
                    return 1;
                }

                // Park until a bot, a command, or Ctrl+C wakes us.
                Wake.Wait();
            }

            client.Disconnect("Client shutting down");
            return 0;
        }
        finally
        {
            if (bridge is not null) await bridge.DisposeAsync();
            ConsoleIO.StopReading();
        }
    }

    private static void OnDisconnected(McClient client, DisconnectReason reason, string message)
    {
        switch (reason)
        {
            case DisconnectReason.InGameKick:
                ConsoleIO.WriteWarning($"Kicked: {message}");
                break;
            case DisconnectReason.LoginFailed:
                ConsoleIO.WriteError(message);
                break;
            case DisconnectReason.ConnectionLost:
                ConsoleIO.WriteWarning(message);
                break;
            case DisconnectReason.UserRequested:
                ConsoleIO.WriteInfo(message);
                break;
        }

        if (!_quit && reason != DisconnectReason.UserRequested && !client.DisconnectHandledByBot)
            ConsoleIO.WriteInfo("Type .reconnect to try again, or .quit to exit.");
    }

    private static void RequestQuit()
    {
        _quit = true;
        Shutdown.Cancel();
        Wake.Set();
    }

    private static void RequestReconnect() => Wake.Set();

    private static List<ChatBot> BuildBots(Settings settings)
    {
        var bots = new List<ChatBot>();

        if (settings.Bots.ChatLog.Enabled) bots.Add(new ChatLogBot(settings.Bots.ChatLog));
        if (settings.Bots.AntiAfk.Enabled) bots.Add(new AntiAfkBot(settings.Bots.AntiAfk));
        if (settings.Bots.AutoRespond.Enabled) bots.Add(new AutoRespondBot(settings.Bots.AutoRespond));

        // Auto-relog goes last so the other bots see the disconnect first.
        if (settings.Bots.AutoRelog.Enabled) bots.Add(new AutoRelogBot(settings.Bots.AutoRelog));

        return bots;
    }

    private static (string Host, ushort Port) ResolveServer(Settings settings)
    {
        string host = settings.Server.Host.Trim();
        ushort port = settings.Server.Port;

        if (host.Length == 0) return ("", port);

        // A host written as "example.com:25566" overrides the separate port setting.
        if (host.Contains(':') && CommandHandler.TryParseAddress(host, out string parsedHost, out ushort parsedPort))
            return (parsedHost, parsedPort);

        return (host, port);
    }

    private static async Task<Session?> ResolveSessionAsync(Settings settings, bool forceLogin, CancellationToken ct)
    {
        if (settings.Account.IsOffline)
        {
            string name = settings.Account.OfflineUsername.Trim();
            if (name.Length is 0 or > 16)
            {
                ConsoleIO.WriteError("account.offlineUsername must be 1-16 characters.");
                return null;
            }
            return Session.ForOffline(name);
        }

        string cachePath = string.IsNullOrWhiteSpace(settings.Account.SessionCachePath)
            ? SessionCache.DefaultPath
            : settings.Account.SessionCachePath;

        var auth = new MicrosoftAuth(settings.Account.MsClientId);

        if (forceLogin) SessionCache.Clear(cachePath);

        Session? cached = forceLogin ? null : SessionCache.Load(cachePath);

        if (cached is { Offline: false })
        {
            if (!cached.IsExpired) return cached;

            if (!string.IsNullOrEmpty(cached.MsRefreshToken))
            {
                try
                {
                    ConsoleIO.WriteInfo("Cached token expired; refreshing...");
                    Session refreshed = await auth.RefreshAsync(cached.MsRefreshToken!, ct);
                    SessionCache.Save(cachePath, refreshed);
                    return refreshed;
                }
                catch (AuthException ex)
                {
                    ConsoleIO.WriteWarning($"Refresh failed ({ex.Message}); signing in again.");
                }
            }
        }

        Session session = await auth.LoginInteractiveAsync(ct);
        SessionCache.Save(cachePath, session);
        return session;
    }

    private static async Task<int> PingOnlyAsync(string host, ushort port)
    {
        try
        {
            ServerStatus status = await ServerPing.QueryAsync(host, port);
            ConsoleIO.WriteInfo($"{host}:{port}");
            ConsoleIO.WriteLine($"  version   {status.VersionName} (protocol {status.ProtocolVersion})");
            ConsoleIO.WriteLine($"  players   {status.OnlinePlayers}/{status.MaxPlayers}");
            ConsoleIO.WriteLine($"  latency   {status.LatencyMs} ms");
            if (status.Description.Length > 0)
                ConsoleIO.WriteLine($"  motd      {status.Description.Replace("\n", " | ")}");
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleIO.WriteError($"Ping failed: {ex.Message}");
            return 1;
        }
    }
}
