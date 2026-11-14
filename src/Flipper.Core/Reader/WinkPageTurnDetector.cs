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
    private static readonly TimeSpan MinimumWinkTime = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaximumWinkTime = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan MaximumEyeTransition = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ReopenConfirmationTime = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TurnCooldown = TimeSpan.FromMilliseconds(700);

    private TimeSpan? _lastSampleAt;
    private TimeSpan? _bothOpenSince;
    private TimeSpan? _lastTurnAt;
    private TimeSpan _winkStartedAt;
    private int _winkDirection;
    private int _closedSamples;
    private TimeSpan? _uncertainSince;
    private TimeSpan? _winkReopenedAt;

    public int Observe(EyeState leftEye, EyeState rightEye, TimeSpan timestamp)
    {
        var previousSampleAt = _lastSampleAt;
        _lastSampleAt = timestamp;
        if (previousSampleAt is { } previous
            && (timestamp <= previous || timestamp - previous > MaximumSampleGap))
        {
            _bothOpenSince = null;
            ClearCandidate();
            if (_lastTurnAt > timestamp)
            {
                _lastTurnAt = timestamp;
            }
        }

        if (_uncertainSince is { } uncertain && timestamp - uncertain > MaximumEyeTransition)
        {
            ClearCandidate();
        }

        if (leftEye == EyeState.Open && rightEye == EyeState.Open)
        {
            _bothOpenSince ??= timestamp;
            if (_winkDirection == 0)
            {
                return 0;
            }

            _uncertainSince = null;
            _winkReopenedAt ??= timestamp;
            if (timestamp - _winkReopenedAt.Value < ReopenConfirmationTime)
            {
                return 0;
            }

            var duration = _winkReopenedAt.Value - _winkStartedAt;
            // Transition frames can preserve a candidate, but only clear closures confirm it.
            var turn = _closedSamples >= 2 && duration >= MinimumWinkTime && duration <= MaximumWinkTime ? _winkDirection : 0;
            ClearCandidate();
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
        _winkReopenedAt = null;

        var direction = (leftEye, rightEye) switch
        {
            (EyeState.Open, EyeState.Closed or EyeState.Unknown) => 1,
            (EyeState.Closed or EyeState.Unknown, EyeState.Open) => -1,
            _ => 0
        };
        if (_winkDirection != 0)
        {
            if (direction != _winkDirection || timestamp - _winkStartedAt > MaximumWinkTime)
            {
                ClearCandidate();
            }
        }
        else if (armed && direction != 0)
        {
            _winkDirection = direction;
            _winkStartedAt = timestamp;
        }

        if (_winkDirection != 0)
        {
            var winkEye = _winkDirection == 1 ? rightEye : leftEye;
            if (winkEye == EyeState.Closed)
            {
                _closedSamples++;
                _uncertainSince = null;
            }
            else
            {
                _uncertainSince ??= timestamp;
            }
        }

        return 0;
    }

    public void Reset()
    {
        _lastSampleAt = null;
        _bothOpenSince = null;
        _lastTurnAt = null;
        ClearCandidate();
    }

    private void ClearCandidate()
    {
        _winkDirection = 0;
        _closedSamples = 0;
        _uncertainSince = null;
        _winkReopenedAt = null;
    }
}
