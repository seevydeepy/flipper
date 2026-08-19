using System.IO.Compression;
using Flipper.Core.Update;

namespace Flipper.Core.Tests;

public sealed class InPlaceInstallerTests
{
    [Fact]
    public void Extract_OverwritesFileAndKeepsSibling()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var zipPath = Path.Combine(root, "payload.zip");
        try
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "Carousel.txt"), "old");
            File.WriteAllText(Path.Combine(target, "keep.txt"), "stay");

            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "Carousel.txt"), "new");
            ZipFile.CreateFromDirectory(stage, zipPath);

            Assert.True(InPlaceInstaller.Extract(zipPath, target));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Carousel.txt")));
            Assert.Equal("stay", File.ReadAllText(Path.Combine(target, "keep.txt")));
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
    public void Extract_MissingZip_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.False(InPlaceInstaller.Extract(Path.Combine(root, "missing.zip"), root));
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
