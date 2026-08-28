using System.Text;
using System.Text.Json;
using ThanosClient.Terminal;

namespace ThanosClient.Client;

/// <summary>
/// Turns a 1.8 chat component (or a legacy section-sign string) into console text.
/// Components nest and inherit formatting from their parent, so styles are tracked on
/// a stack and re-emitted as ANSI whenever they change.
/// </summary>
public static class ChatParser
{
    private const char Section = '\u00a7';

    private static readonly Dictionary<string, string> ColorToAnsi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "30", ["dark_blue"] = "34", ["dark_green"] = "32", ["dark_aqua"] = "36",
        ["dark_red"] = "31", ["dark_purple"] = "35", ["gold"] = "33", ["gray"] = "37",
        ["dark_gray"] = "90", ["blue"] = "94", ["green"] = "92", ["aqua"] = "96",
        ["red"] = "91", ["light_purple"] = "95", ["yellow"] = "93", ["white"] = "97",
        ["reset"] = "0",
    };

    private static readonly Dictionary<char, string> LegacyToAnsi = new()
    {
        ['0'] = "30", ['1'] = "34", ['2'] = "32", ['3'] = "36",
        ['4'] = "31", ['5'] = "35", ['6'] = "33", ['7'] = "37",
        ['8'] = "90", ['9'] = "94", ['a'] = "92", ['b'] = "96",
        ['c'] = "91", ['d'] = "95", ['e'] = "93", ['f'] = "97",
        ['l'] = "1", ['n'] = "4", ['m'] = "9", ['o'] = "3", ['r'] = "0",
    };

    /// <summary>The handful of 1.8 translation keys a chat-only client actually sees.</summary>
    private static readonly Dictionary<string, string> Translations = new()
    {
        ["chat.type.text"] = "<%1$s> %2$s",
        ["chat.type.emote"] = "* %1$s %2$s",
        ["chat.type.announcement"] = "[%1$s] %2$s",
        ["chat.type.admin"] = "[%1$s: %2$s]",
        ["chat.type.achievement"] = "%1$s has just earned the achievement %2$s",
        ["commands.message.display.incoming"] = "%1$s whispers to you: %2$s",
        ["commands.message.display.outgoing"] = "You whisper to %1$s: %2$s",
        ["multiplayer.player.joined"] = "%1$s joined the game",
        ["multiplayer.player.left"] = "%1$s left the game",
        ["death.attack.player"] = "%1$s was slain by %2$s",
        ["death.attack.mob"] = "%1$s was slain by %2$s",
        ["death.attack.arrow"] = "%1$s was shot by %2$s",
        ["death.attack.fall"] = "%1$s hit the ground too hard",
        ["death.attack.lava"] = "%1$s tried to swim in lava",
        ["death.attack.inFire"] = "%1$s went up in flames",
        ["death.attack.explosion"] = "%1$s blew up",
        ["death.attack.generic"] = "%1$s died",
        ["disconnect.genericReason"] = "%1$s",
        ["disconnect.spam"] = "Kicked for spamming",
        ["disconnect.timeout"] = "Timed out",
    };

    /// <summary>Parses a chat JSON payload. Falls back to the raw string if it is not JSON.</summary>
    public static string Parse(string json, bool withColor)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            Render(doc.RootElement, sb, new Style(), withColor);
            if (withColor) sb.Append(ConsoleIO.Esc).Append("[0m");
            return sb.ToString();
        }
        catch (JsonException)
        {
            return FromLegacy(json, withColor);
        }
    }

    /// <summary>Same text with all formatting stripped, for log files.</summary>
    public static string ParsePlain(string json) => Parse(json, withColor: false);

    private readonly record struct Style(string? Color = null, bool Bold = false, bool Italic = false,
                                         bool Underlined = false, bool Strikethrough = false)
    {
        public Style Inherit(JsonElement element)
        {
            Style style = this;

            if (element.TryGetProperty("color", out JsonElement color) && color.ValueKind == JsonValueKind.String)
                style = style with { Color = color.GetString() };

            if (element.TryGetProperty("bold", out JsonElement bold) && bold.ValueKind != JsonValueKind.Null)
                style = style with { Bold = AsBool(bold) };
            if (element.TryGetProperty("italic", out JsonElement italic) && italic.ValueKind != JsonValueKind.Null)
                style = style with { Italic = AsBool(italic) };
            if (element.TryGetProperty("underlined", out JsonElement under) && under.ValueKind != JsonValueKind.Null)
                style = style with { Underlined = AsBool(under) };
            if (element.TryGetProperty("strikethrough", out JsonElement strike) && strike.ValueKind != JsonValueKind.Null)
                style = style with { Strikethrough = AsBool(strike) };

            return style;
        }

        private static bool AsBool(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out bool b) && b,
            _ => false,
        };

        public string ToAnsi()
        {
            var codes = new List<string> { "0" };
            if (Color is not null && ColorToAnsi.TryGetValue(Color, out string? ansi)) codes.Add(ansi);
            if (Bold) codes.Add("1");
            if (Italic) codes.Add("3");
            if (Underlined) codes.Add("4");
            if (Strikethrough) codes.Add("9");
            return ConsoleIO.Esc + "[" + string.Join(';', codes) + "m";
        }
    }

    private static void Render(JsonElement element, StringBuilder sb, Style style, bool withColor)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                Append(sb, element.GetString() ?? "", style, withColor);
                return;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                Append(sb, element.GetRawText(), style, withColor);
                return;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                    Render(child, sb, style, withColor);
                return;

            case JsonValueKind.Object:
                break;

            default:
                return;
        }

        Style current = style.Inherit(element);

        if (element.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
        {
            Append(sb, text.GetString() ?? "", current, withColor);
        }
        else if (element.TryGetProperty("translate", out JsonElement key) && key.ValueKind == JsonValueKind.String)
        {
            var args = new List<string>();
            if (element.TryGetProperty("with", out JsonElement with) && with.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement arg in with.EnumerateArray())
                {
                    var argBuilder = new StringBuilder();
                    Render(arg, argBuilder, current, withColor);
                    args.Add(argBuilder.ToString());
                }
            }

            Append(sb, Translate(key.GetString() ?? "", args), current, withColor, alreadyStyled: withColor);
        }

        if (element.TryGetProperty("extra", out JsonElement extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in extra.EnumerateArray())
                Render(child, sb, current, withColor);
        }
    }

    /// <summary>Applies a translation template, supporting both %s and %1$s placeholders.</summary>
    private static string Translate(string key, List<string> args)
    {
        if (!Translations.TryGetValue(key, out string? template))
            return args.Count > 0 ? string.Join(' ', args) : key;

        var sb = new StringBuilder();
        int nextArg = 0;

        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '%')
            {
                sb.Append(template[i]);
                continue;
            }

            if (i + 1 < template.Length && template[i + 1] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }

            // %1$s style: an explicit, one-based argument index
            int j = i + 1;
            int index = 0;
            while (j < template.Length && char.IsDigit(template[j]))
            {
                index = index * 10 + (template[j] - '0');
                j++;
            }

            if (j + 1 < template.Length && index > 0 && template[j] == '$' && template[j + 1] == 's')
            {
                sb.Append(index <= args.Count ? args[index - 1] : "");
                i = j + 1;
                continue;
            }

            // plain %s: consume the next argument in order
            if (i + 1 < template.Length && template[i + 1] == 's')
            {
                sb.Append(nextArg < args.Count ? args[nextArg++] : "");
                i++;
                continue;
            }

            sb.Append('%');
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string text, Style style, bool withColor, bool alreadyStyled = false)
    {
        if (text.Length == 0) return;

        if (withColor && !alreadyStyled)
            sb.Append(style.ToAnsi());

        sb.Append(TranslateLegacyCodes(text, withColor, style));
    }

    /// <summary>Converts a legacy section-sign string into ANSI, or strips the codes entirely.</summary>
    public static string FromLegacy(string text, bool withColor)
    {
        string body = TranslateLegacyCodes(text, withColor, new Style());
        return withColor ? body + ConsoleIO.Esc + "[0m" : body;
    }

    private static string TranslateLegacyCodes(string text, bool withColor, Style resetTo)
    {
        if (text.IndexOf(Section) < 0) return text;

        var sb = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != Section || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }

            char code = char.ToLowerInvariant(text[++i]);

            if (!withColor) continue;                       // strip the code and its argument

            if (code == 'r')
                sb.Append(resetTo.ToAnsi());
            else if (LegacyToAnsi.TryGetValue(code, out string? ansi))
                sb.Append(ConsoleIO.Esc).Append('[').Append(ansi).Append('m');
        }

        return sb.ToString();
    }
}
