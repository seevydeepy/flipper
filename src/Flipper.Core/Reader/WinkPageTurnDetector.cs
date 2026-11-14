namespace Flipper.Core.Reader;

public enum EyeState
{
    Unknown,
    Open,
    Closed
}

public sealed class WinkPageTurnDetector
{
    private static readonly TimeSpan MinimumOpenTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MinimumWinkTime = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan MaximumWinkTime = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TurnCooldown = TimeSpan.FromMilliseconds(700);

    private TimeSpan? _lastSampleAt;
    private TimeSpan? _bothOpenSince;
    private TimeSpan? _lastTurnAt;
    private TimeSpan _winkStartedAt;
    private int _winkDirection;

    public int Observe(EyeState leftEye, EyeState rightEye, TimeSpan timestamp)
    {
        var previousSampleAt = _lastSampleAt;
        _lastSampleAt = timestamp;
        if (previousSampleAt is { } previous
            && (timestamp <= previous || timestamp - previous > MaximumSampleGap))
        {
            _bothOpenSince = null;
            _winkDirection = 0;
            if (_lastTurnAt > timestamp)
            {
                _lastTurnAt = timestamp;
            }
        }

        if (leftEye == EyeState.Open && rightEye == EyeState.Open)
        {
            _bothOpenSince ??= timestamp;
            if (_winkDirection == 0)
            {
                return 0;
            }

            var duration = timestamp - _winkStartedAt;
            // An active candidate means every sample since the start showed the same eye closed.
            var confirmed = previousSampleAt > _winkStartedAt;
            var turn = confirmed && duration >= MinimumWinkTime && duration <= MaximumWinkTime ? _winkDirection : 0;
            _winkDirection = 0;
            if (turn != 0)
            {
                _lastTurnAt = timestamp;
            }

            return turn;
        }

        var armed = _bothOpenSince is { } openSince
            && previousSampleAt is { } lastOpenSample
            && lastOpenSample - openSince >= MinimumOpenTime
            && (_lastTurnAt is not { } lastTurn || timestamp - lastTurn >= TurnCooldown);
        _bothOpenSince = null;

        var direction = (leftEye, rightEye) switch
        {
            (EyeState.Open, EyeState.Closed) => 1,
            (EyeState.Closed, EyeState.Open) => -1,
            _ => 0
        };
        if (_winkDirection != 0)
        {
            if (direction != _winkDirection || timestamp - _winkStartedAt > MaximumWinkTime)
            {
                _winkDirection = 0;
            }
        }
        else if (armed && direction != 0)
        {
            _winkDirection = direction;
            _winkStartedAt = timestamp;
        }

        return 0;
    }

    public void Reset()
    {
        _lastSampleAt = null;
        _bothOpenSince = null;
        _lastTurnAt = null;
        _winkDirection = 0;
    }
}
