using Flipper.Core.Reader;
using static Flipper.Core.Reader.WinkGestureKind;

namespace Flipper.Core.Tests;

public sealed class WinkEvaluationTests
{
    [Fact]
    public void Compare_MatchesOneTurnPerWinkAndReportsErrorsByDirection()
    {
        var right = new WinkAnnotation(RightWink, 1000, 1200);
        var left = new WinkAnnotation(LeftWink, 2000, 2200);
        var missed = new WinkAnnotation(RightWink, 3000, 3200);
        var blink = new WinkAnnotation(Blink, 4000, 4100);
        var ignore = new WinkAnnotation(Ignore, 5000, 5200);
        var rightTurn = new WinkTurn(1000, 1);
        var leftTurn = new WinkTurn(2600, -1);
        var duplicate = new WinkTurn(1240, 1);
        var wrongDirection = new WinkTurn(3100, -1);
        var blinkTurn = new WinkTurn(4050, -1);
        var neutralTurn = new WinkTurn(4500, 1);
        var ignoredTurn = new WinkTurn(5100, 1);

        var result = WinkEvaluation.Compare(
            [blink, left, missed, ignore, right],
            [leftTurn, ignoredTurn, duplicate, wrongDirection, neutralTurn, blinkTurn, rightTurn],
            6000);

        Assert.Equal([new WinkMatch(right, rightTurn), new WinkMatch(left, leftTurn)], result.Matches);
        Assert.Equal([missed], result.MissedWinks);
        Assert.Equal([duplicate, wrongDirection, blinkTurn, neutralTurn], result.UnexpectedTurns);
        Assert.Equal([ignoredTurn], result.IgnoredTurns);
        Assert.Equal(new WinkDirectionTotals(1, 1, 2), result.Right);
        Assert.Equal(new WinkDirectionTotals(1, 0, 2), result.Left);
        Assert.Equal(3, result.Expected);
        Assert.Equal(6, result.ScoredTurns);
        Assert.Equal(1.0 / 3, result.Precision);
        Assert.Equal(2.0 / 3, result.Recall);
    }

    [Fact]
    public void Compare_GivesActiveLabelsPriorityOverEarlierResponseWindows()
    {
        var right = new WinkAnnotation(RightWink, 1000, 1100);
        var left = new WinkAnnotation(LeftWink, 1350, 1450);
        var rightDuringIgnore = new WinkTurn(1170, 1);
        var rightDuringBlink = new WinkTurn(1270, 1);
        var rightDuringLeftWink = new WinkTurn(1360, 1);
        var leftTurn = new WinkTurn(1500, -1);

        var result = WinkEvaluation.Compare(
            [right, new(Ignore, 1150, 1200), new(Blink, 1250, 1300), left],
            [rightDuringIgnore, rightDuringBlink, rightDuringLeftWink, leftTurn],
            2000);

        Assert.Equal(new WinkMatch(left, leftTurn), Assert.Single(result.Matches));
        Assert.Equal(right, Assert.Single(result.MissedWinks));
        Assert.Equal([rightDuringBlink, rightDuringLeftWink], result.UnexpectedTurns);
        Assert.Equal(rightDuringIgnore, Assert.Single(result.IgnoredTurns));
    }

    [Fact]
    public void Compare_TurnOutsideChosenAllowanceIsUnexpected()
    {
        var label = new WinkAnnotation(RightWink, 1000, 1200);
        var turn = new WinkTurn(1301, 1);

        var result = WinkEvaluation.Compare([label], [turn], 2000, responseAllowanceMs: 100);

        Assert.Empty(result.Matches);
        Assert.Equal(label, Assert.Single(result.MissedWinks));
        Assert.Equal(turn, Assert.Single(result.UnexpectedTurns));
    }

    [Fact]
    public void Compare_NoScoredTurnsOrWinksHasUndefinedPrecisionAndRecall()
    {
        var result = WinkEvaluation.Compare([new(Blink, 200, 300), new(Ignore, 400, 500)], [new(400, 1)], 1000);

        Assert.Null(result.Precision);
        Assert.Null(result.Recall);
        Assert.Single(result.IgnoredTurns);
    }

    [Theory]
    [InlineData(200, 300, Blink)] // Overlapping labels.
    [InlineData(350, 450, RightWink)] // Overlapping windows for the same direction.
    [InlineData(250, 400, LeftWink)] // Shared endpoint.
    [InlineData(-100, 0, Blink)]
    [InlineData(500, 500, Ignore)]
    [InlineData(950, 1001, LeftWink)]
    [InlineData(800, double.NaN, Ignore)]
    [InlineData(800, 900, (WinkGestureKind)999)]
    public void Compare_RejectsInvalidOrAmbiguousLabels(double startMs, double endMs, WinkGestureKind kind)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            WinkEvaluation.Compare([new(RightWink, 100, 250), new(kind, startMs, endMs)], [], 1000));

        Assert.Equal("annotations", error.ParamName);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1001, 1)]
    [InlineData(double.PositiveInfinity, -1)]
    [InlineData(500, 0)]
    public void Compare_RejectsInvalidTurns(double timeMs, int direction)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            WinkEvaluation.Compare([], [new(timeMs, direction)], 1000));

        Assert.Equal("turns", error.ParamName);
    }

    [Theory]
    [InlineData(0, 400, "durationMs")]
    [InlineData(double.NaN, 400, "durationMs")]
    [InlineData(1000, -1, "responseAllowanceMs")]
    [InlineData(1000, double.PositiveInfinity, "responseAllowanceMs")]
    public void Compare_RejectsInvalidDurationOrAllowance(double durationMs, double allowanceMs, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            WinkEvaluation.Compare([], [], durationMs, allowanceMs));

        Assert.Equal(parameter, error.ParamName);
    }
}
