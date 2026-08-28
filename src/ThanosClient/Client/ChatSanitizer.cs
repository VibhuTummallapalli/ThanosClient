using System.Text;

namespace ThanosClient.Client;

/// <summary>
/// Filters outbound chat to the characters a vanilla 1.8 server accepts. The server
/// kicks with "Illegal characters in chat" for anything else, so text arriving from
/// outside the client - a Discord message, most obviously - has to be cleaned first.
/// </summary>
public static class ChatSanitizer
{
    /// <summary>The section sign, which servers reject because it drives colour codes.</summary>
    private const char Section = (char)167;

    private const char Delete = (char)127;

    /// <summary>Vanilla's own rule: printable, not DEL, and not the section sign.</summary>
    public static bool IsAllowed(char c) => c >= ' ' && c != Delete && c != Section;

    /// <summary>
    /// Returns chat that is safe to send. Line breaks and tabs become spaces (a newline
    /// would otherwise run two sentences together), disallowed characters are dropped,
    /// and runs of whitespace are collapsed.
    /// </summary>
    public static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        var sb = new StringBuilder(message.Length);
        bool lastWasSpace = false;

        foreach (char c in message)
        {
            char candidate = c is '\n' or '\r' or '\t' ? ' ' : c;

            if (!IsAllowed(candidate)) continue;

            if (candidate == ' ')
            {
                if (lastWasSpace || sb.Length == 0) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            sb.Append(candidate);
        }

        return sb.ToString().TrimEnd();
    }
}
