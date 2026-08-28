using ThanosClient.Client;
using ThanosClient.Config;

namespace ThanosClient.Bots;

/// <summary>Appends every chat line to a file, with colour codes stripped.</summary>
public sealed class ChatLogBot : ChatBot
{
    private readonly ChatLogSettings _settings;
    private readonly object _fileLock = new();
    private bool _warned;

    public override string Name => "chatlog";

    public ChatLogBot(ChatLogSettings settings) => _settings = settings;

    public override void OnChat(string text, string rawJson, ChatPosition position)
    {
        if (position == ChatPosition.ActionBar) return;

        string line = _settings.IncludeTimestamps
            ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}"
            : text;

        lock (_fileLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(_settings.File));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_settings.File, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // Report once: a broken log path should not spam every chat line.
                if (!_warned)
                {
                    _warned = true;
                    LogError($"Could not write {_settings.File}: {ex.Message}");
                }
            }
        }
    }
}
