using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class PendingScoreDeletesTests
{
    [Fact]
    public void Arm_ContainsPath_AndSecondArmDoesNotCreate()
    {
        var pending = new PendingScoreDeletes();
        var entry = Entry("air");

        var first = pending.Arm(entry, out var created);
        var second = pending.Arm(entry, out var createdAgain);

        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(first.Id, second.Id);
        Assert.True(pending.Contains(entry.CanonicalPath));
        Assert.True(pending.TryGet(first.Id, out var found));
        Assert.Equal(first.Id, found.Id);
    }

    [Fact]
    public void Undo_LeavesFile()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "air.pdf");
        File.WriteAllText(path, "pdf");
        var pending = new PendingScoreDeletes();
        var item = pending.Arm(Entry("air", path), out _);

        Assert.True(pending.TryUndo(item.Id));
        Assert.False(pending.Contains(item.CanonicalPath));
        Assert.True(File.Exists(path));
        Assert.False(pending.TryUndo(item.Id));
    }

    [Fact]
    public void Commit_DeletesFileThenClearsContains()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "air.pdf");
        File.WriteAllText(path, "pdf");
        var pending = new PendingScoreDeletes();
        var item = pending.Arm(Entry("air", path), out _);

        var result = pending.Commit(item.Id);

        Assert.NotNull(result);
        Assert.False(result.Failed);
        Assert.False(File.Exists(path));
        Assert.False(pending.Contains(item.CanonicalPath));
    }

    [Fact]
    public void Commit_ThrownDelete_LeavesContainsTrue()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "air.pdf");
        File.WriteAllText(path, "pdf");
        var pending = new PendingScoreDeletes();
        var item = pending.Arm(Entry("air", path), out _);

        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = pending.Commit(item.Id);

        Assert.NotNull(result);
        Assert.True(result.Failed);
        Assert.True(pending.Contains(item.CanonicalPath));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TwoItems_CommitAndUndoIndependently()
    {
        using var dir = new TempDir();
        var keep = Path.Combine(dir.Path, "keep.pdf");
        var drop = Path.Combine(dir.Path, "drop.pdf");
        File.WriteAllText(keep, "keep");
        File.WriteAllText(drop, "drop");
        var pending = new PendingScoreDeletes();
        var keepItem = pending.Arm(Entry("keep", keep), out _);
        var dropItem = pending.Arm(Entry("drop", drop), out _);

        Assert.True(pending.TryUndo(keepItem.Id));
        var result = pending.Commit(dropItem.Id);

        Assert.NotNull(result);
        Assert.False(result.Failed);
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(drop));
        Assert.False(pending.Contains(keepItem.CanonicalPath));
        Assert.False(pending.Contains(dropItem.CanonicalPath));
    }

    [Fact]
    public void Commit_MissingFile_Succeeds()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "gone.pdf");
        var pending = new PendingScoreDeletes();
        var item = pending.Arm(Entry("gone", path), out _);

        var result = pending.Commit(item.Id);

        Assert.NotNull(result);
        Assert.False(result.Failed);
        Assert.False(pending.Contains(item.CanonicalPath));
    }

    private static ScoreEntry Entry(string name, string? path = null)
    {
        var full = path ?? $@"C:\lib\{name}.pdf";
        return new ScoreEntry(name, string.Empty, full, name, 1, DateTime.UnixEpoch);
    }
}
