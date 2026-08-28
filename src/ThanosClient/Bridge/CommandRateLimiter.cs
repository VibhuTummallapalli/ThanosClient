namespace ThanosClient.Bridge;

/// <summary>
/// Two limits working together: a per-user cooldown that stops one person hammering the
/// bot, and a global ceiling per minute that stops a whole channel doing it. The second
/// matters because the Minecraft server kicks accounts that chat too fast.
/// </summary>
public sealed class CommandRateLimiter
{
    private readonly TimeSpan _perUserCooldown;
    private readonly int _maxPerMinute;
    private readonly Func<DateTime> _clock;

    private readonly Dictionary<ulong, DateTime> _lastUse = new();
    private readonly Queue<DateTime> _recent = new();
    private readonly object _sync = new();

    public CommandRateLimiter(int perUserCooldownSeconds, int maxPerMinute, Func<DateTime>? clock = null)
    {
        _perUserCooldown = TimeSpan.FromSeconds(Math.Max(0, perUserCooldownSeconds));
        _maxPerMinute = maxPerMinute;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Takes a slot if one is free. On refusal, <paramref name="reason"/> explains which limit was hit.</summary>
    public bool TryAcquire(ulong userId, out string reason)
    {
        DateTime now = _clock();
        reason = "";

        lock (_sync)
        {
            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1))
                _recent.Dequeue();

            if (_maxPerMinute > 0 && _recent.Count >= _maxPerMinute)
            {
                reason = $"The bridge is at its limit of {_maxPerMinute} commands per minute. Try again shortly.";
                return false;
            }

            if (_perUserCooldown > TimeSpan.Zero &&
                _lastUse.TryGetValue(userId, out DateTime last) &&
                now - last < _perUserCooldown)
            {
                double wait = (_perUserCooldown - (now - last)).TotalSeconds;
                reason = $"Slow down - try again in {Math.Ceiling(wait):0} second(s).";
                return false;
            }

            _lastUse[userId] = now;
            _recent.Enqueue(now);
            return true;
        }
    }
}
