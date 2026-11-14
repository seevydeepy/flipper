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
        Observe(430, Open, Open);
        Observe(529, Open, Open);
        Observe(530, Open, Open, direction);
        Observe(550, Open, Open);
    }

    [Theory]
    [InlineData(Open, Closed, 1)]
    [InlineData(Closed, Open, -1)]
    public void TwoClosedFramesAtCaptureCadence_TurnOnReopening(EyeState left, EyeState right, int direction)
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, left, right);
        Observe(385, left, right);
        Observe(462, Open, Open);
        Observe(539, Open, Open);
        Observe(616, Open, Open, direction);
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
        Reopen(539, 1);
    }

    [Fact]
    public void UnknownFrames_DoNotConfirmClosure()
    {
        ObserveOpenAtCaptureCadence();
        Observe(308, Open, Unknown);
        Observe(385, Open, Closed);
        Observe(462, Open, Unknown);
        Reopen(539);
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
        Reopen(462 + uncertainMilliseconds, expectedTurn);
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
        Reopen(693);
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
        Reopen(539);
    }

    [Theory]
    [InlineData(329, 0)]
    [InlineData(330, 1)]
    public void MinimumClosureTime_IsRequired(int reopenAt, int expectedTurn)
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(300, Open, Closed);
        Reopen(reopenAt, expectedTurn);
    }

    [Theory]
    [InlineData(Open, Closed, 1)]
    [InlineData(Closed, Open, -1)]
    public void FastWinkAtHigherCaptureCadence_WaitsForStableReopening(EyeState left, EyeState right, int direction)
    {
        ObserveOpenForArming();
        Observe(250, left, right);
        Observe(296, left, right);
        Observe(342, Open, Open);
        Observe(441, Open, Open);
        Observe(442, Open, Open, direction);
    }

    [Fact]
    public void ShortStaggeredBlink_IsIgnoredAtHigherCaptureCadence()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(296, Closed, Closed);
        Reopen(342);
    }

    [Fact]
    public void SingleClosedSample_IsIgnoredWhenNextFrameIsDelayed()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Reopen(450);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BriefOpenReadingsDuringWink_DoNotSplitOrTurnEarly(int closedSamplesBeforeInterruption)
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(330, Open, closedSamplesBeforeInterruption == 2 ? Closed : Unknown);
        Observe(410, Open, Open);
        Observe(456, Open, Open);
        Observe(502, Open, Open);
        Observe(548, Open, Closed);
        Reopen(594, 1);
        Observe(750, Open, Open);
    }

    [Fact]
    public void StableReopening_DiscardsAnUnconfirmedClosure()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Reopen(450);
        Observe(575, Open, Closed);
        Reopen(780);

        Observe(980, Open, Open);
        Observe(1000, Open, Closed);
        Observe(1077, Open, Closed);
        Reopen(1154, 1);
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
        Reopen(700);
    }

    [Fact]
    public void SwitchingClosedEye_CancelsTheWink()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(350, Open, Closed);
        Observe(450, Closed, Open);
        Observe(550, Closed, Open);
        Reopen(650);
    }

    [Theory]
    [InlineData(1250, 1)]
    [InlineData(1251, 0)]
    [InlineData(1501, 0)]
    public void LongClosure_LimitUsesFirstOpenSampleAndTurnNeverRepeats(int reopenAt, int expectedTurn)
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

        Reopen(reopenAt, expectedTurn);
        Observe(reopenAt + 200, Open, Open);
    }

    [Theory]
    [InlineData(649, 0)]
    [InlineData(650, -1)]
    public void NextWink_RequiresTwoHundredMillisecondsOfOpenEyesBeforeClosure(int lastOpenAt, int expectedTurn)
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(350, Open, Closed);
        Reopen(450, 1);
        Observe(lastOpenAt, Open, Open);
        Observe(651, Closed, Open);
        Observe(851, Closed, Open);
        Reopen(1051, expectedTurn);
    }

    [Theory]
    [InlineData(Open, Closed, 1)]
    [InlineData(Closed, Open, -1)]
    public void SuccessiveWinksAtReaderCadence_TurnOnceEach(EyeState secondLeft, EyeState secondRight, int secondDirection)
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(296, Open, Closed);
        Observe(342, Open, Open);
        Observe(388, Open, Open);
        Observe(434, Open, Open);
        Observe(480, Open, Open, 1);
        Observe(526, Open, Open);
        Observe(572, Open, Open);
        Observe(618, secondLeft, secondRight);
        Observe(664, secondLeft, secondRight);
        Observe(710, Open, Open);
        Observe(756, Open, Open);
        Observe(802, Open, Open);
        Observe(848, Open, Open, secondDirection);
        Observe(894, Open, Open);
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
        Reopen(at + 650, -1);
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
        Reopen(800);
        Observe(1000, Open, Open);
        Observe(1050, Closed, Open);
        Observe(1150, Closed, Open);
        Reopen(1250, -1);
    }

    [Fact]
    public void Reset_CancelsAnInProgressWink()
    {
        ObserveOpenForArming();
        Observe(250, Open, Closed);
        Observe(450, Open, Closed);

        _detector.Reset();

        Reopen(500);
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

    private void Reopen(int firstOpenAt, int expectedTurn = 0)
    {
        Observe(firstOpenAt, Open, Open);
        Observe(firstOpenAt + 100, Open, Open, expectedTurn);
    }

    private void Observe(int milliseconds, EyeState left, EyeState right, int expectedTurn = 0)
    {
        Assert.Equal(expectedTurn, _detector.Observe(left, right, TimeSpan.FromMilliseconds(milliseconds)));
    }
}
