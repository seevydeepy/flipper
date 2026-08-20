using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class LibrarySnapshotTests
{
    [Fact]
    public void SameMembership_IgnoresScanOrder()
    {
        var first = Entry("a", 10);
        var second = Entry("b", 20);
        var left = new LibrarySnapshot(@"C:\lib", [first, second], true);
        var right = new LibrarySnapshot(@"C:\lib", [second, first], true);
        Assert.True(left.SameMembership(right));
    }

    [Fact]
    public void SameMembership_FalseWhenFileChanges()
    {
        var left = new LibrarySnapshot(@"C:\lib", [Entry("a", 10)], true);
        var right = new LibrarySnapshot(@"C:\lib", [Entry("a", 11)], true);
        Assert.False(left.SameMembership(right));
    }

    [Fact]
    public void SameMembership_FalseWhenCardTextChanges()
    {
        var left = new LibrarySnapshot(@"C:\lib", [Entry("a", 10) with { Title = "(Main Theme)" }], true);
        var right = new LibrarySnapshot(@"C:\lib", [Entry("a", 10) with { Title = "Schindler's List", Subtitle = "Main Theme" }], true);
        Assert.False(left.SameMembership(right));
    }

    [Fact]
    public void SameMembership_FalseWhenAPathIsMissing()
    {
        var left = new LibrarySnapshot(@"C:\lib", [Entry("a", 10), Entry("b", 20)], true);
        var right = new LibrarySnapshot(@"C:\lib", [Entry("a", 10), Entry("c", 20)], true);
        Assert.False(left.SameMembership(right));
    }

    [Fact]
    public void SameMembership_LargeLibrary_IsOrderIndependent()
    {
        var scores = Enumerable.Range(0, 4000).Select(index => Entry($"s{index}", index)).ToArray();
        var left = new LibrarySnapshot(@"C:\lib", scores, true);
        var right = new LibrarySnapshot(@"C:\lib", scores.Reverse().ToArray(), true);
        Assert.True(left.SameMembership(right));
    }

    [Fact]
    public void Without_RemovesNamedPath_AndKeepsOthers()
    {
        var first = Entry("a", 10);
        var second = Entry("b", 20);
        var snapshot = new LibrarySnapshot(@"C:\lib", [first, second], true);

        var next = snapshot.Without("A");

        Assert.Equal("b", Assert.Single(next.Scores).CanonicalPath);
        Assert.Equal(2, snapshot.Scores.Count);
    }

    private static ScoreEntry Entry(string name, long length)
    {
        return new ScoreEntry(name, string.Empty, $@"C:\lib\{name}.pdf", name, length, DateTime.UnixEpoch);
    }
}
