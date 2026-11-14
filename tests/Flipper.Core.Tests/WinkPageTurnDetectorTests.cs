using Flipper.Core.Reader;
using static Flipper.Core.Reader.EyeState;

namespace Flipper.Core.Tests;

public sealed class WinkPageTurnDetectorTests
{
    private readonly WinkPageTurnDetector _detector = new();

    [Theory]
    [InlineData(Open, Closed, 1)]
    [InlineData(Closed, Open, -1)]
    public void Wink_TurnsOnceOnReopening(EyeState left, EyeState right, int direction)
    {
        ObserveOpenForArming();
        Observe(250, left, right);
        Observe(400, left, right);
        Observe(430, Open, Open, direction);
        Observe(450, Open, Open);
    }

    [Theory]
    [InlineData(Open, Closed, 1)]
    [InlineData(Closed, Open, -1)]
    public void TwoClosedFramesAtCaptureCadence_TurnOnReopening(EyeState left, EyeState right, int direction)
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, left, right);
        Observe(385, left, right);
        Observe(462, Open, Open, direction);
        Observe(539, Open, Open);
    }

    [Theory]
    [InlineData(Unknown, Closed, Closed)]
    [InlineData(Closed, Unknown, Closed)]
    [InlineData(Closed, Closed, Unknown)]
    public void BriefUncertaintyOnWinkingEye_StillTurnsOnReopening(EyeState first, EyeState second, EyeState third)
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, Open, first);
        Observe(385, Open, second);
        Observe(462, Open, third);
        Observe(539, Open, Open, 1);
    }

    [Fact]
    public void UnknownFrames_DoNotConfirmClosure()
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, Open, Unknown);
        Observe(385, Open, Closed);
        Observe(462, Open, Unknown);
        Observe(539, Open, Open);
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(121, 0)]
    public void UncertaintyAtReopening_HasALimit(int uncertainMilliseconds, int expectedTurn)
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, Open, Closed);
        Observe(385, Open, Closed);
        Observe(462, Open, Unknown);
        Observe(462 + uncertainMilliseconds, Open, Open, expectedTurn);
    }

    [Fact]
    public void ProlongedUncertainty_DoesNotResumeWhenClosureIsDetectedAgain()
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, Open, Closed);
        Observe(385, Open, Unknown);
        Observe(462, Open, Unknown);
        Observe(539, Open, Closed);
        Observe(616, Open, Closed);
        Observe(693, Open, Open);
    }

    [Theory]
    [InlineData(Unknown, Closed)]
    [InlineData(Unknown, Unknown)]
    public void UncertaintyOnOppositeEye_CancelsWink(EyeState left, EyeState right)
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, Open, Closed);
        Observe(385, Open, Closed);
        Observe(462, left, right);
        Observe(539, Open, Open);
    }

    [Fact]
    public void ShortClosure_IsIgnored()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(350, Open, Closed);
        Observe(369, Open, Open);
    }

    [Fact]
    public void SingleClosedSample_IsIgnoredWhenNextFrameIsDelayed()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(450, Open, Open);
    }

    [Theory]
    [InlineData(Closed, Closed)]
    [InlineData(Open, Closed)]
    public void Blink_IsIgnored(EyeState firstLeft, EyeState firstRight)
    {
        ObserveOpenForArming();
        Observe(250, firstLeft, firstRight);
        Observe(350, firstLeft, firstRight);
        Observe(450, Closed, Closed);
        Observe(500, Open, Closed);
        Observe(600, Open, Closed);
        Observe(700, Open, Open);
    }

    [Fact]
    public void SwitchingClosedEye_CancelsTheWink()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(350, Open, Closed);
        Observe(450, Closed, Open);
        Observe(550, Closed, Open);
        Observe(650, Open, Open);
    }

    [Theory]
    [InlineData(1250, 1)]
    [InlineData(1251, 0)]
    [InlineData(1501, 0)]
    public void LongClosure_IsLimitedAndNeverRepeats(int reopenAt, int expectedTurn)
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(450, Open, Closed);
        Observe(650, Open, Closed);
        Observe(850, Open, Closed);
        Observe(1050, Open, Closed);
        if (reopenAt > 1251)
        {
            Observe(1251, Open, Closed);
        }

        Observe(reopenAt, Open, Open, expectedTurn);
        Observe(reopenAt + 200, Open, Open);
    }

    [Fact]
    public void NextWink_RequiresOpenEyesAndCooldownBeforeClosure()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(350, Open, Closed);
        Observe(450, Open, Open, 1);
        Observe(650, Open, Open);
        Observe(850, Open, Open);
        Observe(1050, Open, Open);
        Observe(1149, Closed, Open);
        Observe(1249, Closed, Open);
        Observe(1329, Open, Open);
        Observe(1529, Open, Open);
        Observe(1530, Closed, Open);
        Observe(1600, Closed, Open);
        Observe(1710, Open, Open, -1);
    }

    [Theory]
    [InlineData(500, Unknown, Open)]
    [InlineData(701, Open, Open)]
    [InlineData(450, Open, Closed)]
    [InlineData(449, Open, Closed)]
    public void InterruptedTracking_CancelsTheWink(int at, EyeState left, EyeState right)
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(450, Open, Closed);
        Observe(at, left, right);
        Observe(at + 200, Open, Open);
        Observe(at + 400, Open, Open);
        Observe(at + 450, Closed, Open);
        Observe(at + 550, Closed, Open);
        Observe(at + 650, Open, Open, -1);
    }

    [Fact]
    public void ClosedAtStartup_RequiresANewClosureAfterArming()
    {
        Observe(0, Closed, Open);
        Observe(200, Closed, Open);
        Observe(400, Open, Open);
        Observe(599, Open, Open);
        Observe(600, Closed, Open);
        Observe(700, Closed, Open);
        Observe(800, Open, Open);
        Observe(1000, Open, Open);
        Observe(1050, Closed, Open);
        Observe(1150, Closed, Open);
        Observe(1250, Open, Open, -1);
    }

    [Fact]
    public void Reset_CancelsAnInProgressWink()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(450, Open, Closed);

        _detector.Reset();

        Observe(500, Open, Open);
    }

    private void ObserveOpenForArming()
    {
        Observe(0, Open, Open);
        Observe(200, Open, Open);
    }

    private void ObserveOpenAtCaptureCadence()
    {
        Observe(0, Open, Open);
        Observe(77, Open, Open);
        Observe(154, Open, Open);
        Observe(231, Open, Open);
    }

    private void Observe(int milliseconds, EyeState left, EyeState right, int expectedTurn = 0)
    {
        Assert.Equal(expectedTurn, _detector.Observe(left, right, TimeSpan.FromMilliseconds(milliseconds)));
    }
}
