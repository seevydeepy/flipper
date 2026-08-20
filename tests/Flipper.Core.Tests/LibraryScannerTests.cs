using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class LibraryScannerTests
{
    [Fact]
    public void Scan_FindsNestedPdfs_AndIgnoresOtherFiles()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Root.pdf"), "a");
        File.WriteAllText(Path.Combine(root.Path, "notes.txt"), "no");
        Directory.CreateDirectory(Path.Combine(root.Path, "Bach"));
        File.WriteAllText(Path.Combine(root.Path, "Bach", "Suite.PDF"), "b");

        var snapshot = LibraryScanner.Scan(root.Path);

        Assert.True(snapshot.RootReachable);
        Assert.Equal(2, snapshot.Scores.Count);
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Root" && score.RelativeFolder == string.Empty);
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Suite" && score.RelativeFolder == "Bach");
    }

    [Fact]
    public void Scan_AppliesCatalogSubtitle()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "List.pdf"), "a");
        File.WriteAllText(
            Path.Combine(root.Path, ScoreCatalog.FileName),
            """{"List.pdf":{"title":"Schindler's List","subtitle":"Main Theme","composer":"John Williams"}}""");

        var snapshot = LibraryScanner.Scan(root.Path);
        var score = Assert.Single(snapshot.Scores);
        Assert.Equal("Schindler's List", score.CardTitle);
        Assert.Equal("Main Theme", score.CardSubtitle);
        Assert.Equal("John Williams", score.CardComposer);
    }

    [Fact]
    public void Scan_IncludesTrashScores_AndOmitsTrashFolder()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Visible.pdf"), "a");
        var trash = Path.Combine(root.Path, ScoreTrash.FolderName);
        Directory.CreateDirectory(trash);
        File.WriteAllText(Path.Combine(trash, "Hidden.pdf"), "b");

        var snapshot = LibraryScanner.Scan(root.Path);

        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Visible");
        Assert.Contains(snapshot.Scores, score => score.DisplayName == "Hidden" && score.RelativeFolder == ScoreTrash.FolderName);
        Assert.DoesNotContain(snapshot.Folders, folder => ScoreTrash.IsHiddenFolder(folder));
    }

    [Fact]
    public void Scan_ContinuesAfterUnreadableSubdirectory()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "Visible.pdf"), "a");
        var locked = Path.Combine(root.Path, "Locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "Hidden.pdf"), "b");

        var user = Environment.UserName;
        var deny = RunIcacls($"\"{locked}\" /deny {user}:(OI)(CI)(R)");
        try
        {
            if (deny != 0)
            {
                return;
            }

            var snapshot = LibraryScanner.Scan(root.Path);
            Assert.True(snapshot.RootReachable);
            Assert.Contains(snapshot.Scores, score => score.DisplayName == "Visible");
            Assert.DoesNotContain(snapshot.Scores, score => score.DisplayName == "Hidden");
        }
        finally
        {
            RunIcacls($"\"{locked}\" /remove:d {user}");
        }
    }

    [Fact]
    public void Scan_MissingRoot_IsUnreachable()
    {
        var snapshot = LibraryScanner.Scan(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.False(snapshot.RootReachable);
        Assert.Empty(snapshot.Scores);
    }

    private static int RunIcacls(string args)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return -1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
