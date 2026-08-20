using System.Diagnostics;
using System.Runtime.Versioning;
using Flipper.Core.Update;
using Microsoft.Win32;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
internal static class Uninstall
{
    public static int Run(bool quiet, int? waitPid)
    {
        using var parent = Registry.CurrentUser.OpenSubKey(PerUserUninstall.UninstallSubKey, writable: true);
        if (parent is null || !PerUserUninstall.TryRead(parent, PerUserUninstall.ProductKey, out var info))
        {
            return Fail("Carousel is not installed.", quiet);
        }

        if (!quiet && !NativeDialog.YesNo($"Remove Carousel from {info.InstallLocation}?"))
        {
            return 0;
        }

        var processPath = Environment.ProcessPath;
        var processDir = string.IsNullOrWhiteSpace(processPath) ? "" : Path.GetDirectoryName(processPath) ?? "";
        if (!quiet && PerUserUninstall.LocationsMatch(processDir, info.InstallLocation))
        {
            return StartTempCopy(processPath!);
        }

        if (waitPid is int pid && !InPlaceInstaller.WaitForProcess(pid, TimeSpan.FromSeconds(InPlaceInstaller.DefaultTimeoutSec)))
        {
            return 2;
        }

        return DeleteOwned(parent, info);
    }

    private static int StartTempCopy(string processPath)
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "CarouselUninstall", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var tempExe = Path.Combine(tempDir, "Carousel.Setup.exe");
            File.Copy(processPath, tempExe, overwrite: true);
            var start = new ProcessStartInfo
            {
                FileName = tempExe,
                Arguments = $"--uninstall --quiet --wait-pid {Environment.ProcessId}",
                UseShellExecute = false,
                WorkingDirectory = tempDir
            };
            if (Process.Start(start) is null)
            {
                return Fail("Could not start uninstall.", quiet: false);
            }

            return 0;
        }
        catch (IOException)
        {
            return Fail("Could not start uninstall.", quiet: false);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail("Could not start uninstall.", quiet: false);
        }
        catch (InvalidOperationException)
        {
            return Fail("Could not start uninstall.", quiet: false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Fail("Could not start uninstall.", quiet: false);
        }
    }

    private static int DeleteOwned(RegistryKey parent, UninstallInfo info)
    {
        var target = info.InstallLocation;
        var owned = InstallManifest.Read(target);
        var setup = Path.Combine(target, "Carousel.Setup.exe");
        if (!InstallManifest.TryDeleteListed(target, owned))
        {
            RewriteRemaining(target, RemainingOwned(target, owned));
            return 3;
        }

        var remaining = RemainingOwned(target, owned);
        if (remaining.Count > 0)
        {
            RewriteRemaining(target, remaining);
            return 3;
        }

        if (!InstallManifest.TryDeleteFile(InstallManifest.FilePath(target)))
        {
            return 3;
        }

        if (!TryRemoveEmptyDirectories(target) || !InstallManifest.TryDeleteFile(setup))
        {
            return 3;
        }

        PerUserUninstall.Remove(parent, PerUserUninstall.ProductKey);
        TryRemoveEmptyDirectories(target);
        return 0;
    }

    private static List<string> RemainingOwned(string target, IReadOnlyList<string> owned)
    {
        var remaining = new List<string>();
        foreach (var relative in owned)
        {
            if (InstallManifest.TryResolveOwned(target, relative, out var dest) && File.Exists(dest))
            {
                remaining.Add(relative);
            }
        }

        return remaining;
    }

    private static void RewriteRemaining(string target, IReadOnlyList<string> remaining)
    {
        if (Directory.Exists(target))
        {
            InstallManifest.Write(target, remaining);
        }
    }

    private static bool TryRemoveEmptyDirectories(string target)
    {
        try
        {
            InstallManifest.RemoveEmptyDirectories(target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int Fail(string message, bool quiet)
    {
        Console.WriteLine(message);
        if (!quiet && ExplorerHost.OpenedFromExplorer())
        {
            NativeDialog.Error(message);
        }

        return 4;
    }
}
