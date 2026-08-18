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

    private static ScoreEntry Entry(string name, long length)
    {
        return new ScoreEntry(name, string.Empty, $@"C:\lib\{name}.pdf", name, length, DateTime.UnixEpoch);
    }
}
