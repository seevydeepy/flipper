using Flipper.Core.Update;

namespace Flipper.Core.Tests;

public sealed class FirstInstallPathsTests
{
    [Fact]
    public void TryResolveRid_ReadsSetupFileName()
    {
        Assert.True(FirstInstallPaths.TryResolveRid(@"D:\Downloads\Carousel.Setup-1.0.35-win-x64.exe", "win-arm64", out var rid));
        Assert.Equal("win-x64", rid);
        Assert.True(FirstInstallPaths.TryResolveRid(@"D:\Downloads\Carousel.Setup-1.0.35-win-arm64.exe", null, out rid));
        Assert.Equal("win-arm64", rid);
    }

    [Fact]
    public void TryResolveRid_FallsBackToRuntimeRid()
    {
        Assert.True(FirstInstallPaths.TryResolveRid(@"D:\Downloads\Carousel.Setup.exe", "win-x64", out var rid));
        Assert.Equal("win-x64", rid);
        Assert.False(FirstInstallPaths.TryResolveRid(@"D:\Downloads\Carousel.Setup.exe", null, out _));
    }

    [Fact]
    public void DefaultTarget_IsLocalAppDataProgramsCarousel()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Carousel");
        Assert.Equal(expected, FirstInstallPaths.DefaultTarget());
    }

    [Fact]
    public void StartMenuShortcutPath_IsUnderStartMenuPrograms()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Carousel.lnk");
        Assert.Equal(expected, FirstInstallPaths.StartMenuShortcutPath());
        Assert.False(
            FirstInstallPaths.StartMenuShortcutPath()
                .StartsWith(FirstInstallPaths.DefaultTarget(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryResolveSiblingZip_FindsZipNextToSetup()
    {
        var setup = Path.Combine(Path.GetTempPath(), "flipper-tests", "Carousel.Setup-1.0.35-win-x64.exe");
        Assert.True(FirstInstallPaths.TryResolveSiblingZip(setup, "win-x64", out var zip));
        var directory = Path.GetDirectoryName(Path.GetFullPath(setup));
        Assert.Equal(Path.Combine(directory!, "Carousel-win-x64.zip"), zip);
    }
}
