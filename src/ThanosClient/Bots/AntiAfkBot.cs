using ThanosClient.Client;
using ThanosClient.Config;

namespace ThanosClient.Bots;

/// <summary>
/// Keeps the connection from looking idle: swings the arm on a timer and, optionally,
/// nudges the view angle so servers that watch for head movement stay satisfied.
/// </summary>
public sealed class AntiAfkBot : ChatBot
{
    private readonly AntiAfkSettings _settings;
    private DateTime _next = DateTime.MinValue;
    private float _yawStep = 5f;

    public override string Name => "antiafk";

    public AntiAfkBot(AntiAfkSettings settings) => _settings = settings;

    public override void OnJoinedGame() => _next = DateTime.UtcNow.AddSeconds(_settings.IntervalSeconds);

    public override void OnUpdate()
    {
        if (DateTime.UtcNow < _next) return;
        _next = DateTime.UtcNow.AddSeconds(Math.Max(1, _settings.IntervalSeconds));

        Client.SendSwingArm();

        if (!_settings.MoveSlightly || Client.CurrentLocation is not Location location) return;

        // Rock the view back and forth rather than drifting in one direction forever.
        location.Yaw += _yawStep;
        _yawStep = -_yawStep;
        Client.SendPositionAndLook(location, onGround: true);
    }
}
