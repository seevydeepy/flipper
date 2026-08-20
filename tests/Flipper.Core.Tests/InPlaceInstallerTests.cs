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
    public void Extract_ReportsProgressForEachFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var zipPath = Path.Combine(root, "payload.zip");
        try
        {
            var stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "one.txt"), "1");
            File.WriteAllText(Path.Combine(stage, "two.txt"), "2");
            ZipFile.CreateFromDirectory(stage, zipPath);

            var reports = new List<InstallProgress>();
            Assert.True(InPlaceInstaller.Extract(zipPath, target, progress: new CollectingProgress(reports)));
            Assert.Equal(2, reports.Count);
            Assert.All(reports, item => Assert.Equal(2, item.Total));
            Assert.Equal(1, reports[0].Current);
            Assert.Equal(2, reports[1].Current);
            Assert.Contains(reports, item => item.Message == "one.txt");
            Assert.Contains(reports, item => item.Message == "two.txt");
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
    public void TryExtract_MissingZip_SetsError()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.False(InPlaceInstaller.TryExtract(Path.Combine(root, "missing.zip"), root, out var error));
            Assert.Equal("Could not find the package.", error);
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

    [Fact]
    public void TryParseArgs_EmptyOrUnknown_Fails()
    {
        Assert.False(InPlaceInstaller.TryParseArgs(Array.Empty<string>(), out _, out _, out _, out _, out _));
        Assert.False(InPlaceInstaller.TryParseArgs(["--help"], out _, out _, out _, out _, out _));
        Assert.False(InPlaceInstaller.TryParseArgs(["--uninstall"], out _, out _, out _, out _, out _));
        Assert.False(InPlaceInstaller.TryParseArgs(["--uninstall", "--quiet", "--wait-pid", "1"], out _, out _, out _, out _, out _));
    }

    [Fact]
    public void TryParseArgs_RootedTargetAndZip_Succeeds()
    {
        var target = Path.GetTempPath();
        var zip = Path.Combine(Path.GetTempPath(), "payload.zip");
        Assert.True(InPlaceInstaller.TryParseArgs(["--target", target, "--zip", zip], out var parsedTarget, out var parsedZip, out var pid, out var timeout, out var relaunch));
        Assert.Equal(target, parsedTarget);
        Assert.Equal(zip, parsedZip);
        Assert.Null(pid);
        Assert.Equal(InPlaceInstaller.DefaultTimeoutSec, timeout);
        Assert.False(relaunch);
    }

    [Fact]
    public void TryParseArgs_RelaunchFlag_Succeeds()
    {
        var target = Path.GetTempPath();
        var zip = Path.Combine(Path.GetTempPath(), "payload.zip");
        Assert.True(InPlaceInstaller.TryParseArgs(
            ["--target", target, "--zip", zip, "--wait-pid", "12", "--relaunch"],
            out var parsedTarget,
            out var parsedZip,
            out var pid,
            out var timeout,
            out var relaunch));
        Assert.Equal(target, parsedTarget);
        Assert.Equal(zip, parsedZip);
        Assert.Equal(12, pid);
        Assert.Equal(InPlaceInstaller.DefaultTimeoutSec, timeout);
        Assert.True(relaunch);
    }

    [Fact]
    public void TryStartApp_MissingExe_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "flipper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Assert.False(InPlaceInstaller.TryStartApp(root));
            Assert.False(InPlaceInstaller.TryStartApp(""));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class CollectingProgress : IProgress<InstallProgress>
    {
        private readonly List<InstallProgress> _items;

        public CollectingProgress(List<InstallProgress> items)
        {
            _items = items;
        }

        public void Report(InstallProgress value)
        {
            _items.Add(value);
        }
    }
}
