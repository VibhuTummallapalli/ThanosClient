using ThanosClient.Terminal;

namespace ThanosClient.Config;

/// <summary>Command line arguments. Anything set here overrides the config file.</summary>
public sealed class CommandLineOptions
{
    public string ConfigPath { get; private set; } = Settings.DefaultFileName;
    public string? Host { get; private set; }
    public ushort? Port { get; private set; }
    public string? OfflineUsername { get; private set; }
    public bool PingOnly { get; private set; }
    public bool ForceLogin { get; private set; }
    public bool Debug { get; private set; }
    public bool NoColor { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "-h" or "--help":
                    options.ShowHelp = true;
                    break;

                case "-c" or "--config":
                    options.ConfigPath = Next() ?? options.ConfigPath;
                    break;

                case "-s" or "--host" or "--server":
                    options.Host = Next();
                    break;

                case "-p" or "--port":
                    if (ushort.TryParse(Next(), out ushort port)) options.Port = port;
                    break;

                case "--offline":
                    options.OfflineUsername = Next() ?? "Player";
                    break;

                case "--ping":
                    options.PingOnly = true;
                    break;

                case "--login" or "--relogin":
                    options.ForceLogin = true;
                    break;

                case "--debug":
                    options.Debug = true;
                    break;

                case "--no-color" or "--no-colour":
                    options.NoColor = true;
                    break;

                default:
                    // A bare argument is treated as the server address, so
                    // "ThanosClient play.example.com" just works.
                    if (!arg.StartsWith('-') && options.Host is null) options.Host = arg;
                    else Console.Error.WriteLine($"Ignoring unknown argument \"{arg}\"");
                    break;
            }
        }

        return options;
    }

    public void ApplyTo(Settings settings)
    {
        if (Host is not null)
        {
            if (Commands.CommandHandler.TryParseAddress(Host, out string host, out ushort port))
            {
                settings.Server.Host = host;
                if (Host.Contains(':')) settings.Server.Port = port;
            }
            else
            {
                settings.Server.Host = Host;
            }
        }

        if (Port is not null) settings.Server.Port = Port.Value;

        if (OfflineUsername is not null)
        {
            settings.Account.Mode = "offline";
            settings.Account.OfflineUsername = OfflineUsername;
        }

        if (Debug) settings.Console.DebugPackets = true;
        if (NoColor) settings.Console.Colors = false;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("ThanosClient - a headless console client for Minecraft 1.8.9 (protocol 47)");
        Console.WriteLine();
        Console.WriteLine("Usage: ThanosClient [host[:port]] [options]");
        Console.WriteLine();
        Console.WriteLine("  -s, --host <host[:port]>  server to join");
        Console.WriteLine("  -p, --port <port>         server port (default 25565)");
        Console.WriteLine("  -c, --config <file>       config file (default thanosclient.json)");
        Console.WriteLine("      --offline <name>      join as an offline-mode player instead of signing in");
        Console.WriteLine("      --login               ignore the cached session and sign in again");
        Console.WriteLine("      --ping                print the server list ping and exit");
        Console.WriteLine("      --debug               log every packet id received");
        Console.WriteLine("      --no-color            disable ANSI colours");
        Console.WriteLine("  -h, --help                this message");
    }
}
