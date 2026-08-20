using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Flipper.Core.Update;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
internal static class StartMenuShortcut
{
    private const uint ShcneAssocChanged = 0x08000000;
    private const int SwShowNormal = 1;

    public static bool TryCreate(string targetExe, string workingDir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(targetExe) || !File.Exists(targetExe))
        {
            error = "Could not create the Start Menu shortcut.";
            return false;
        }

        var path = FirstInstallPaths.StartMenuShortcutPath();
        var folder = Path.GetDirectoryName(path);
        object? com = null;
        try
        {
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            com = new ShellLink();
            var link = (IShellLinkW)com;
            link.SetPath(targetExe);
            link.SetWorkingDirectory(string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(targetExe) ?? "" : workingDir);
            link.SetDescription("Carousel");
            link.SetIconLocation(targetExe, 0);
            link.SetShowCmd(SwShowNormal);
            var file = (IPersistFile)com;
            file.Save(path, true);
            NotifyShell();
            return true;
        }
        catch (COMException)
        {
            error = "Could not create the Start Menu shortcut.";
            return false;
        }
        catch (IOException)
        {
            error = "Could not create the Start Menu shortcut.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Could not create the Start Menu shortcut.";
            return false;
        }
        finally
        {
            if (com is not null)
            {
                Marshal.FinalReleaseComObject(com);
            }
        }
    }

    public static bool TryDelete()
    {
        var path = FirstInstallPaths.StartMenuShortcutPath();
        if (!File.Exists(path))
        {
            return true;
        }

        if (!InstallManifest.TryDeleteFile(path))
        {
            return false;
        }

        NotifyShell();
        return true;
    }

    private static void NotifyShell()
    {
        SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
