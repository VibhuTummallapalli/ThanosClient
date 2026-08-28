using Discord;
using Discord.WebSocket;
using ThanosClient.Client;
using ThanosClient.Config;
using ThanosClient.Terminal;

namespace ThanosClient.Bridge;

/// <summary>
/// Connects to Discord and wires the two worlds together: slash commands come in and
/// drive the Minecraft client, in-game chat goes out to the configured channels.
///
/// Every inbound command is checked against the whitelist and the rate limiter before it
/// touches the client, and every outbound message is posted with mentions disabled, so
/// nothing a player types in game can ping the Discord server.
/// </summary>
public sealed class DiscordBridge : IAsyncDisposable
{
    private const string RootCommand = "mc";

    private readonly DiscordSettings _settings;
    private readonly DiscordCommands _commands;
    private readonly GameChatRelay _relay;
    private readonly CommandRateLimiter _rateLimiter;
    private readonly DiscordSocketClient _discord;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _relayLoop;
    private bool _commandsRegistered;

    public DiscordBridge(Settings settings, McClient mcClient, GameChatRelay relay, Action requestReconnect)
    {
        _settings = settings.Discord;
        _relay = relay;
        _commands = new DiscordCommands(mcClient, settings, requestReconnect);
        _rateLimiter = new CommandRateLimiter(_settings.PerUserCooldownSeconds, _settings.MaxCommandsPerMinute);

        // Message Content is a privileged intent, so it is only requested when the
        // feature that needs it is actually switched on.
        GatewayIntents intents = GatewayIntents.Guilds;
        if (_settings.RelayDiscordMessages)
            intents |= GatewayIntents.GuildMessages | GatewayIntents.MessageContent;

        _discord = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = intents,
            LogLevel = LogSeverity.Warning,
            AlwaysDownloadUsers = false,
        });

        _discord.Log += OnLogAsync;
        _discord.Ready += OnReadyAsync;
        _discord.SlashCommandExecuted += OnSlashCommandAsync;

        if (_settings.RelayDiscordMessages)
            _discord.MessageReceived += OnMessageAsync;
    }

    /// <summary>Logs in and starts the gateway. Returns false if the bridge cannot start.</summary>
    public async Task<bool> StartAsync()
    {
        string token = _settings.EffectiveToken;

        if (string.IsNullOrWhiteSpace(token))
        {
            ConsoleIO.WriteError(
                "Discord is enabled but no bot token is set. Put one in discord.token, or set " +
                $"the {DiscordSettings.TokenEnvironmentVariable} environment variable.");
            return false;
        }

        if (!_settings.HasWhitelist)
            ConsoleIO.WriteWarning(
                "Discord is enabled but no roles or users are whitelisted, so every command will be " +
                "refused. Add ids to discord.allowedRoleIds or discord.allowedUserIds.");

        if (_settings.ChannelIds.Count == 0)
            ConsoleIO.WriteWarning("No discord.channelIds are set: the bridge will accept commands from any channel.");

        try
        {
            await _discord.LoginAsync(TokenType.Bot, token);
            await _discord.StartAsync();
        }
        catch (Exception ex)
        {
            ConsoleIO.WriteError($"Could not connect to Discord: {ex.Message}");
            return false;
        }

        _relayLoop = Task.Run(() => RelayLoopAsync(_stopping.Token));
        ConsoleIO.WriteInfo("Discord bridge starting...");
        return true;
    }

    // --- gateway events --------------------------------------------------------

    private Task OnLogAsync(LogMessage message)
    {
        string text = $"[discord] {message.Message}{(message.Exception is null ? "" : " - " + message.Exception.Message)}";

        if (message.Severity <= LogSeverity.Error) ConsoleIO.WriteError(text);
        else if (message.Severity == LogSeverity.Warning) ConsoleIO.WriteWarning(text);
        else ConsoleIO.WriteDebug(text);

        return Task.CompletedTask;
    }

    /// <summary>Ready fires again after a gateway reconnect, so registration happens once.</summary>
    private async Task OnReadyAsync()
    {
        ConsoleIO.WriteSuccess($"Discord bridge connected as {_discord.CurrentUser?.Username ?? "unknown"}.");

        if (_commandsRegistered) return;
        _commandsRegistered = true;

        try
        {
            SlashCommandProperties command = BuildCommand();

            if (_settings.GuildId != 0)
            {
                SocketGuild? guild = _discord.GetGuild(_settings.GuildId);
                if (guild is null)
                {
                    ConsoleIO.WriteError($"The bot is not in guild {_settings.GuildId}; slash commands were not registered.");
                    return;
                }

                await guild.CreateApplicationCommandAsync(command);
                ConsoleIO.WriteInfo($"Registered /{RootCommand} in {guild.Name}.");
            }
            else
            {
                await _discord.CreateGlobalApplicationCommandAsync(command);
                ConsoleIO.WriteInfo($"Registered /{RootCommand} globally; it can take up to an hour to appear.");
            }
        }
        catch (Exception ex)
        {
            ConsoleIO.WriteError($"Could not register slash commands: {ex.Message}");
        }
    }

    private static SlashCommandProperties BuildCommand()
    {
        var root = new SlashCommandBuilder()
            .WithName(RootCommand)
            .WithDescription("Control the Minecraft client");

        root.AddOption(new SlashCommandOptionBuilder()
            .WithName("say")
            .WithDescription("Send chat, or an in-game command when it starts with /")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("message", ApplicationCommandOptionType.String, "What to send", isRequired: true));

        root.AddOption(Simple("status", "Connection, account and bot summary"));
        root.AddOption(Simple("list", "Players currently online"));
        root.AddOption(Simple("pos", "The client's position"));
        root.AddOption(Simple("health", "The client's health"));
        root.AddOption(Simple("tab", "Tab list header and footer"));
        root.AddOption(Simple("reconnect", "Drop the connection and rejoin"));
        root.AddOption(Simple("disconnect", "Leave the server without stopping the client"));

        root.AddOption(new SlashCommandOptionBuilder()
            .WithName("ping")
            .WithDescription("Server list ping")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption("address", ApplicationCommandOptionType.String, "host or host:port; defaults to the current server", isRequired: false));

        root.AddOption(new SlashCommandOptionBuilder()
            .WithName("bots")
            .WithDescription("List bots, or turn one on or off")
            .WithType(ApplicationCommandOptionType.SubCommand)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("on or off")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .AddChoice("on", "on")
                .AddChoice("off", "off"))
            .AddOption("name", ApplicationCommandOptionType.String, "Bot name", isRequired: false));

        return root.Build();
    }

    private static SlashCommandOptionBuilder Simple(string name, string description) =>
        new SlashCommandOptionBuilder()
            .WithName(name)
            .WithDescription(description)
            .WithType(ApplicationCommandOptionType.SubCommand);

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.CommandName != RootCommand) return;

        try
        {
            if (!Authorize(command.User, command.Channel.Id, out string denial))
            {
                await command.RespondAsync(denial, ephemeral: true);
                return;
            }

            SocketSlashCommandDataOption subCommand = command.Data.Options.First();
            string name = subCommand.Name;

            string? Argument(string key) =>
                subCommand.Options.FirstOrDefault(o => o.Name == key)?.Value as string;

            // Ping talks to another server, so it can outlast Discord's three second
            // window for a first response.
            if (name == "ping")
            {
                await command.DeferAsync();
                string pingResult = await _commands.PingAsync(Argument("address"));
                await command.FollowupAsync(pingResult, allowedMentions: AllowedMentions.None);
                return;
            }

            string reply = name switch
            {
                "say" => _commands.Say(Argument("message") ?? "", command.User.Username),
                "status" => _commands.Status(),
                "list" => _commands.List(),
                "pos" => _commands.Position(),
                "health" => _commands.Health(),
                "tab" => _commands.TabList(),
                "reconnect" => _commands.Reconnect(),
                "disconnect" => _commands.Disconnect(),
                "bots" => _commands.Bots(Argument("action"), Argument("name")),
                _ => $"Unknown subcommand `{name}`.",
            };

            await command.RespondAsync(DiscordFormat.Truncate(reply), allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            ConsoleIO.WriteError($"[discord] command failed: {ex.Message}");

            try
            {
                if (command.HasResponded) await command.FollowupAsync($"Command failed: {ex.Message}", ephemeral: true);
                else await command.RespondAsync($"Command failed: {ex.Message}", ephemeral: true);
            }
            catch (Exception) { /* the interaction already expired */ }
        }
    }

    /// <summary>Forwards ordinary channel messages in game, when that is switched on.</summary>
    private async Task OnMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot || message.Author.IsWebhook) return;
        if (string.IsNullOrWhiteSpace(message.Content)) return;
        if (message.Content.StartsWith('/')) return;   // slash commands arrive as interactions
        if (!IsServedChannel(message.Channel.Id)) return;

        if (!Authorize(message.Author, message.Channel.Id, out string denial))
        {
            if (denial.Length > 0)
                await message.Channel.SendMessageAsync(denial, allowedMentions: AllowedMentions.None);
            return;
        }

        string reply = _commands.Say(message.Content, message.Author.Username);
        ConsoleIO.WriteDebug($"[discord] {message.Author.Username}: {reply}");
    }

    private bool IsServedChannel(ulong channelId) =>
        _settings.ChannelIds.Count == 0 || _settings.ChannelIds.Contains(channelId);

    /// <summary>Whitelist first, then rate limit. Both must pass.</summary>
    private bool Authorize(SocketUser user, ulong channelId, out string denial)
    {
        IEnumerable<ulong> roles = user is SocketGuildUser guildUser
            ? guildUser.Roles.Select(r => r.Id)
            : Enumerable.Empty<ulong>();

        AuthorizationResult result = DiscordAuthorizer.Check(user.Id, roles, channelId, _settings);

        if (result != AuthorizationResult.Allowed)
        {
            denial = DiscordAuthorizer.Explain(result);
            return false;
        }

        if (!_rateLimiter.TryAcquire(user.Id, out string reason))
        {
            denial = reason;
            return false;
        }

        denial = "";
        return true;
    }

    // --- outbound relay --------------------------------------------------------

    private async Task RelayLoopAsync(CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, _settings.RelayIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                if (_discord.ConnectionState != ConnectionState.Connected) continue;
                if (!_relay.TryDrain(out string message)) continue;

                foreach (ulong channelId in _settings.ChannelIds)
                {
                    if (_discord.GetChannel(channelId) is IMessageChannel channel)
                        await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
                    else
                        ConsoleIO.WriteWarning($"[discord] channel {channelId} is not reachable; is the bot in that server?");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ConsoleIO.WriteWarning($"[discord] relay error: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();

        if (_relayLoop is not null)
        {
            try { await _relayLoop; } catch (Exception) { /* shutting down */ }
        }

        try
        {
            await _discord.LogoutAsync();
            await _discord.StopAsync();
        }
        catch (Exception) { /* shutting down */ }

        _discord.Dispose();
        _stopping.Dispose();
    }
}
