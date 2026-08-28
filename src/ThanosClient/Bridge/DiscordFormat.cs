using System.Text;

namespace ThanosClient.Bridge;

/// <summary>
/// Text hygiene for the Discord side. Players choose their own names and chat, so
/// anything relayed out of the game is untrusted input: it must not be able to produce
/// markdown formatting or - more importantly - ping the whole server.
/// </summary>
public static class DiscordFormat
{
    private const char ZeroWidthSpace = (char)0x200B;
    private static readonly char[] MarkdownCharacters = { '\\', '*', '_', '~', '`', '|', '>', '#', '-' };

    /// <summary>
    /// Escapes markdown and defuses mentions. AllowedMentions.None already stops pings
    /// from firing, but neutralising the text too means a copied or quoted line cannot
    /// ping either.
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);

        foreach (char c in text)
        {
            if (c == '@')
            {
                // "@everyone" becomes "@<zero width>everyone": visually identical, inert.
                sb.Append('@').Append(ZeroWidthSpace);
                continue;
            }

            if (System.Array.IndexOf(MarkdownCharacters, c) >= 0)
                sb.Append('\\');

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Truncates to Discord's message limit, leaving room for a marker.</summary>
    public static string Truncate(string text, int limit = 1900)
    {
        if (text.Length <= limit) return text;
        return text[..limit] + "\n... (truncated)";
    }
}
