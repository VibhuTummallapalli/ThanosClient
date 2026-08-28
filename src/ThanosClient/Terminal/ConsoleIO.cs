using System.Runtime.InteropServices;
using System.Text;

namespace ThanosClient.Terminal;

/// <summary>
/// Console input and output that coexist. Incoming chat can arrive at any moment while
/// the user is halfway through typing, so every write erases the prompt line, prints,
/// then redraws the prompt and whatever was typed so far.
/// </summary>
public static class ConsoleIO
{
    /// <summary>ASCII escape, built from its code point to keep the source printable.</summary>
    public static readonly string Esc = ((char)27).ToString();

    private static readonly object Sync = new();
    private static readonly StringBuilder Buffer = new();
    private static readonly List<string> History = new();

    private static int _historyIndex = -1;
    private static string _prompt = "> ";
    private static bool _interactive;
    private static Thread? _readerThread;
    private static volatile bool _running;

    public static bool ColorsEnabled { get; private set; } = true;

    /// <summary>Turns on ANSI escape handling where the host needs to be asked first.</summary>
    public static void Initialize(bool enableColors)
    {
        ColorsEnabled = enableColors;
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* redirected */ }

        if (enableColors && OperatingSystem.IsWindows())
            ColorsEnabled = TryEnableVirtualTerminal();
    }

    /// <summary>Starts the input thread. Each completed line is handed to <paramref name="onLine"/>.</summary>
    public static void StartReading(Action<string> onLine, string prompt = "> ")
    {
        _prompt = prompt;
        _running = true;
        _interactive = !Console.IsInputRedirected;

        _readerThread = new Thread(() => ReadLoop(onLine))
        {
            IsBackground = true,
            Name = "console-input",
        };
        _readerThread.Start();
    }

    public static void StopReading() => _running = false;

    private static void ReadLoop(Action<string> onLine)
    {
        while (_running)
        {
            try
            {
                string? line = _interactive ? ReadLineInteractive() : Console.ReadLine();
                if (line is null)
                {
                    _running = false;   // stdin closed; the client itself keeps running
                    return;
                }

                if (line.Length == 0) continue;
                onLine(line);
            }
            catch (Exception ex)
            {
                WriteError($"Console input error: {ex.Message}");
            }
        }
    }

    /// <summary>Line editor with history, built on raw key reads so output can interleave safely.</summary>
    private static string? ReadLineInteractive()
    {
        RedrawPrompt();

        while (_running)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(15);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            lock (Sync)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                    {
                        string line = Buffer.ToString();
                        Buffer.Clear();
                        _historyIndex = -1;
                        Console.Write(Environment.NewLine);
                        if (line.Length > 0)
                        {
                            History.Add(line);
                            if (History.Count > 200) History.RemoveAt(0);
                        }
                        return line;
                    }

                    case ConsoleKey.Backspace:
                        if (Buffer.Length > 0)
                        {
                            Buffer.Length--;
                            Redraw();
                        }
                        break;

                    case ConsoleKey.UpArrow:
                        StepHistory(-1);
                        break;

                    case ConsoleKey.DownArrow:
                        StepHistory(1);
                        break;

                    case ConsoleKey.Escape:
                        Buffer.Clear();
                        Redraw();
                        break;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            Buffer.Append(key.KeyChar);
                            Redraw();
                        }
                        break;
                }
            }
        }

        return null;
    }

    private static void StepHistory(int direction)
    {
        if (History.Count == 0) return;

        if (_historyIndex == -1)
            _historyIndex = direction < 0 ? History.Count - 1 : -1;
        else
            _historyIndex = Math.Clamp(_historyIndex + direction, 0, History.Count - 1);

        if (_historyIndex < 0) return;

        Buffer.Clear();
        Buffer.Append(History[_historyIndex]);
        Redraw();
    }

    public static void WriteLine(string message) => Write(message);
    public static void WriteInfo(string message) => Write(Colorize(message, "36"));
    public static void WriteWarning(string message) => Write(Colorize("[!] " + message, "33"));
    public static void WriteError(string message) => Write(Colorize("[x] " + message, "31"));
    public static void WriteSuccess(string message) => Write(Colorize(message, "32"));
    public static void WriteDebug(string message) => Write(Colorize("[debug] " + message, "90"));

    /// <summary>Prints a line without disturbing whatever the user is typing.</summary>
    public static void Write(string message)
    {
        lock (Sync)
        {
            ClearLine();
            Console.WriteLine(message);
            RedrawNoLock();
        }
    }

    private static void RedrawPrompt()
    {
        lock (Sync) RedrawNoLock();
    }

    private static void Redraw()
    {
        ClearLine();
        RedrawNoLock();
    }

    private static void RedrawNoLock()
    {
        if (!_interactive) return;
        Console.Write(_prompt + Buffer);
    }

    private static void ClearLine()
    {
        if (!_interactive) return;

        if (ColorsEnabled)
        {
            Console.Write("\r" + Esc + "[2K");
            return;
        }

        int width = SafeWidth();
        int padding = Math.Max(0, Math.Min(width - 1, _prompt.Length + Buffer.Length));
        Console.Write('\r');
        Console.Write(new string(' ', padding));
        Console.Write('\r');
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 80; }
    }

    public static string Colorize(string message, string ansiCode) =>
        ColorsEnabled ? Esc + "[" + ansiCode + "m" + message + Esc + "[0m" : message;

    // --- Windows virtual terminal enablement ----------------------------------

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool TryEnableVirtualTerminal()
    {
        try
        {
            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || !GetConsoleMode(handle, out uint mode)) return false;
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
