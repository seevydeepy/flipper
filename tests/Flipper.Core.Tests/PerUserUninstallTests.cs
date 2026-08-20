using Flipper.Core.Update;
using Microsoft.Win32;

namespace Flipper.Core.Tests;

public sealed class PerUserUninstallTests
{
    [Fact]
    public void LocationsMatch_IgnoresTrailingSlash()
    {
        Assert.True(PerUserUninstall.LocationsMatch(@"C:\Apps\Carousel", @"C:\Apps\Carousel\"));
        Assert.False(PerUserUninstall.LocationsMatch(@"C:\Apps\Carousel", @"C:\Apps\Other"));
    }

    [Fact]
    public void WriteReadRemove_UsesInjectedHive()
    {
        var guid = Guid.NewGuid().ToString("N");
        var parentPath = @"Software\FlipperTests\" + guid;
        using var parent = Registry.CurrentUser.CreateSubKey(parentPath);
        Assert.NotNull(parent);
        try
        {
            var info = new UninstallInfo
            {
                DisplayName = "Carousel",
                Publisher = "seevydeepy",
                DisplayVersion = "1.2.3",
                InstallLocation = @"C:\Users\x\AppData\Local\Programs\Carousel",
                UninstallString = @"""C:\Users\x\AppData\Local\Programs\Carousel\Carousel.Setup.exe"" --uninstall",
                DisplayIcon = @"C:\Users\x\AppData\Local\Programs\Carousel\Carousel.exe",
                EstimatedSizeKb = 2048
            };

            PerUserUninstall.Write(parent, "Carousel", info);
            Assert.True(PerUserUninstall.TryRead(parent, "Carousel", out var read));
            Assert.Equal("Carousel", read.DisplayName);
            Assert.Equal("seevydeepy", read.Publisher);
            Assert.Equal("1.2.3", read.DisplayVersion);
            Assert.Equal(info.InstallLocation, read.InstallLocation);
            Assert.Equal(info.UninstallString, read.UninstallString);
            Assert.Equal(info.DisplayIcon, read.DisplayIcon);
            Assert.Equal(2048, read.EstimatedSizeKb);

            PerUserUninstall.Remove(parent, "Carousel");
            Assert.False(PerUserUninstall.TryRead(parent, "Carousel", out _));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(parentPath, throwOnMissingSubKey: false);
        }
    }
}
