using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Flipper.Core.Update;

[SupportedOSPlatform("windows")]
public static class PerUserUninstall
{
    public const string ProductKey = "Carousel";
    public const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static bool LocationsMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizeLocation(left), NormalizeLocation(right), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeLocation(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static void Write(RegistryKey parent, string productKey, UninstallInfo info)
    {
        using var key = parent.CreateSubKey(productKey, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not write uninstall key.");
        }

        key.SetValue("DisplayName", info.DisplayName);
        key.SetValue("Publisher", info.Publisher);
        key.SetValue("DisplayVersion", info.DisplayVersion);
        key.SetValue("InstallLocation", info.InstallLocation);
        key.SetValue("UninstallString", info.UninstallString);
        key.SetValue("DisplayIcon", info.DisplayIcon);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (info.EstimatedSizeKb > 0)
        {
            key.SetValue("EstimatedSize", info.EstimatedSizeKb, RegistryValueKind.DWord);
        }
    }

    public static bool TryRead(RegistryKey parent, string productKey, out UninstallInfo info)
    {
        info = null!;
        using var key = parent.OpenSubKey(productKey);
        if (key is null)
        {
            return false;
        }

        var displayName = key.GetValue("DisplayName") as string;
        var publisher = key.GetValue("Publisher") as string ?? "";
        var displayVersion = key.GetValue("DisplayVersion") as string ?? "";
        var installLocation = key.GetValue("InstallLocation") as string;
        var uninstallString = key.GetValue("UninstallString") as string ?? "";
        var displayIcon = key.GetValue("DisplayIcon") as string ?? "";
        var estimated = key.GetValue("EstimatedSize") is int size ? size : 0;
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(installLocation))
        {
            return false;
        }

        info = new UninstallInfo
        {
            DisplayName = displayName,
            Publisher = publisher,
            DisplayVersion = displayVersion,
            InstallLocation = installLocation,
            UninstallString = uninstallString,
            DisplayIcon = displayIcon,
            EstimatedSizeKb = estimated
        };
        return true;
    }

    public static void Remove(RegistryKey parent, string productKey)
    {
        parent.DeleteSubKeyTree(productKey, throwOnMissingSubKey: false);
    }
}
