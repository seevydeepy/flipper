using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class PageTurnKeysTests
{
    [Theory]
    [InlineData(PageTurnKeys.Right)]
    [InlineData(PageTurnKeys.Down)]
    [InlineData(PageTurnKeys.PageDown)]
    [InlineData(PageTurnKeys.Space)]
    [InlineData(PageTurnKeys.Enter)]
    public void CommonPedalNextKeys_TurnForward(int virtualKey)
    {
        Assert.Equal(PageTurnCommand.Next, PageTurnKeys.FromVirtualKey(virtualKey, isRepeat: false));
    }

    [Theory]
    [InlineData(PageTurnKeys.Left)]
    [InlineData(PageTurnKeys.Up)]
    [InlineData(PageTurnKeys.PageUp)]
    [InlineData(PageTurnKeys.Backspace)]
    public void CommonPedalBackKeys_TurnBack(int virtualKey)
    {
        Assert.Equal(PageTurnCommand.Back, PageTurnKeys.FromVirtualKey(virtualKey, isRepeat: false));
    }

    [Fact]
    public void Escape_ClosesReader()
    {
        Assert.Equal(PageTurnCommand.Close, PageTurnKeys.FromVirtualKey(PageTurnKeys.Escape, isRepeat: false));
    }

    [Theory]
    [InlineData(PageTurnKeys.Right)]
    [InlineData(PageTurnKeys.Down)]
    [InlineData(PageTurnKeys.PageDown)]
    [InlineData(PageTurnKeys.Left)]
    [InlineData(PageTurnKeys.Up)]
    [InlineData(PageTurnKeys.Space)]
    public void HeldKeyRepeat_IsIgnored(int virtualKey)
    {
        Assert.Equal(PageTurnCommand.None, PageTurnKeys.FromVirtualKey(virtualKey, isRepeat: true));
    }

    [Fact]
    public void UnknownKey_IsIgnored()
    {
        Assert.Equal(PageTurnCommand.None, PageTurnKeys.FromVirtualKey(0x41, isRepeat: false));
    }
}
