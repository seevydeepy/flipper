using Flipper.Core.Update;

namespace Flipper.Core.Tests;

public sealed class ProtectedInstallPathTests
{
    [Fact]
    public void IsProtected_ProgramFiles_IsTrue()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(ProtectedInstallPath.IsProtected(Path.Combine(programFiles, "Carousel")));
    }

    [Fact]
    public void IsProtected_LocalAppData_IsFalse()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(ProtectedInstallPath.IsProtected(Path.Combine(local, "Programs", "Carousel")));
    }
}
