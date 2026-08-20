using System.Reflection;
using System.Runtime.Versioning;
using Flipper.Core.Update;
using Microsoft.Win32;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
internal static class RegisteredInstall
{
    public static bool TryWrite(string targetDir, string zipPath, string setupPath, out string error)
    {
        error = "";
        try
        {
            using var parent = Registry.CurrentUser.CreateSubKey(PerUserUninstall.UninstallSubKey, writable: true);
            if (parent is null)
            {
                error = "Could not register Carousel.";
                return false;
            }

            PerUserUninstall.Write(parent, PerUserUninstall.ProductKey, BuildInfo(targetDir, zipPath, setupPath));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Could not register Carousel.";
            return false;
        }
        catch (IOException)
        {
            error = "Could not register Carousel.";
            return false;
        }
    }

    public static bool TryRefresh(string targetDir, string zipPath)
    {
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(PerUserUninstall.UninstallSubKey, writable: true);
            if (parent is null || !PerUserUninstall.TryRead(parent, PerUserUninstall.ProductKey, out var existing))
            {
                return true;
            }

            if (!PerUserUninstall.LocationsMatch(existing.InstallLocation, targetDir))
            {
                return true;
            }

            if (!InPlaceInstaller.TryListRelativeFiles(zipPath, out var files)
                || !InstallManifest.TryRefreshOwned(targetDir, files))
            {
                return false;
            }

            var setupPath = Path.Combine(targetDir, "Carousel.Setup.exe");
            var running = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(running))
            {
                File.Copy(running, setupPath, overwrite: true);
            }

            PerUserUninstall.Write(parent, PerUserUninstall.ProductKey, BuildInfo(targetDir, zipPath, setupPath));
            TryRefreshStartMenu(targetDir);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static UninstallInfo BuildInfo(string targetDir, string zipPath, string setupPath)
    {
        var location = PerUserUninstall.NormalizeLocation(targetDir);
        var version = CurrentVersion();
        var size = 0;
        try
        {
            size = (int)Math.Min(int.MaxValue, new FileInfo(zipPath).Length / 1024);
        }
        catch (IOException)
        {
        }

        return new UninstallInfo
        {
            DisplayName = "Carousel",
            Publisher = "seevydeepy",
            DisplayVersion = $"{version.Major}.{version.Minor}.{version.Build}",
            InstallLocation = location,
            UninstallString = $"\"{setupPath}\" --uninstall",
            DisplayIcon = Path.Combine(location, "Carousel.exe"),
            EstimatedSizeKb = size
        };
    }

    private static Version CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var file = assembly.GetName().Version?.ToString();
        return AppVersion.Running(info, file);
    }

    private static void TryRefreshStartMenu(string targetDir)
    {
        var app = Path.Combine(PerUserUninstall.NormalizeLocation(targetDir), "Carousel.exe");
        StartMenuShortcut.TryCreate(app, targetDir, out _);
    }
}
