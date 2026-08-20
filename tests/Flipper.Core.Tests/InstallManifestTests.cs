using Flipper.Core.Update;

namespace Flipper.Core.Tests;

public sealed class InstallManifestTests
{
    [Fact]
    public void DeleteListed_RemovesOwnedFilesAndKeepsExtra()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "owned.txt"), "gone");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "stay");
            InstallManifest.Write(root, ["owned.txt"]);

            Assert.True(InstallManifest.TryDeleteListed(root, InstallManifest.Read(root)));
            Assert.False(File.Exists(Path.Combine(root, "owned.txt")));
            Assert.Equal("stay", File.ReadAllText(Path.Combine(root, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void DeleteListed_RefusesPathOutsideTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.False(InstallManifest.TryDeleteListed(root, [@"..\escape.txt"]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void RefreshOwned_DeletesObsoleteKeepsExtraAndRewritesManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");
            File.WriteAllText(Path.Combine(root, "c.txt"), "c");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "stay");
            InstallManifest.Write(root, ["a.txt", "b.txt"]);

            Assert.True(InstallManifest.TryRefreshOwned(root, ["a.txt", "c.txt"]));
            Assert.True(File.Exists(Path.Combine(root, "a.txt")));
            Assert.False(File.Exists(Path.Combine(root, "b.txt")));
            Assert.True(File.Exists(Path.Combine(root, "c.txt")));
            Assert.Equal("stay", File.ReadAllText(Path.Combine(root, "keep.txt")));
            Assert.Equal(["a.txt", "c.txt"], InstallManifest.Read(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
