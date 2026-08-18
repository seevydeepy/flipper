using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class PageLayoutTests
{
    [Fact]
    public void Portrait_ShowsOnePageAndStepsByOne()
    {
        var pages = PageLayout.For(pageCount: 5, lowestVisible: 2, portrait: true);
        Assert.Equal(2, pages.FirstIndex);
        Assert.Null(pages.SecondIndex);
        Assert.Equal(1, pages.Step);
        Assert.Equal(3, PageLayout.Turn(2, 5, portrait: true, direction: 1));
        Assert.Equal(1, PageLayout.Turn(2, 5, portrait: true, direction: -1));
    }

    [Fact]
    public void Landscape_PairsPages()
    {
        var pages = PageLayout.For(pageCount: 5, lowestVisible: 0, portrait: false);
        Assert.Equal(0, pages.FirstIndex);
        Assert.Equal(1, pages.SecondIndex);
        Assert.Equal(2, pages.Step);
        Assert.Equal(2, PageLayout.Turn(0, 5, portrait: false, direction: 1));
    }

    [Fact]
    public void Landscape_LastOddPageStandsAlone()
    {
        var pages = PageLayout.For(pageCount: 5, lowestVisible: 4, portrait: false);
        Assert.Equal(4, pages.FirstIndex);
        Assert.Null(pages.SecondIndex);
        Assert.Equal(4, PageLayout.Turn(4, 5, portrait: false, direction: 1));
    }

    [Fact]
    public void Rotate_KeepsLowestVisiblePage()
    {
        var portrait = PageLayout.For(5, 3, portrait: true);
        var landscape = PageLayout.For(5, portrait.FirstIndex, portrait: false);
        Assert.Equal(2, landscape.FirstIndex);
        Assert.Equal(3, landscape.SecondIndex);
    }
}
