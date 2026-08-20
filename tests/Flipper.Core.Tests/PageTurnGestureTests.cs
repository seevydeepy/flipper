using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class PageTurnGestureTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(499.9)]
    public void LeftHalfTap_TurnsBack(double x)
    {
        Assert.Equal(PageTurnCommand.Back, PageTurnGesture.FromTap(x, 1000));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(999)]
    public void RightHalfTap_TurnsForward(double x)
    {
        Assert.Equal(PageTurnCommand.Next, PageTurnGesture.FromTap(x, 1000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TapWithNoWidth_IsIgnored(double width)
    {
        Assert.Equal(PageTurnCommand.None, PageTurnGesture.FromTap(10, width));
    }

    [Fact]
    public void SwipeLeft_TurnsForward()
    {
        Assert.Equal(PageTurnCommand.Next, PageTurnGesture.FromSwipe(-81));
    }

    [Fact]
    public void SwipeRight_TurnsBack()
    {
        Assert.Equal(PageTurnCommand.Back, PageTurnGesture.FromSwipe(81));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(-80)]
    public void ShortSwipe_IsIgnored(double translationX)
    {
        Assert.Equal(PageTurnCommand.None, PageTurnGesture.FromSwipe(translationX));
    }
}
