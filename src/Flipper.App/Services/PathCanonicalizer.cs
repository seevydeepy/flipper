using System.Runtime.InteropServices;
using System.Text;

namespace Flipper.App.Services;

public static class PathCanonicalizer
{
    private const int NoError = 0;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    public static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception)
        {
            full = path;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return full;
        }

        if (full.Length >= 2 && char.IsLetter(full[0]) && full[1] == ':')
        {
            var drive = full[..2];
            var buffer = new StringBuilder(512);
            var length = buffer.Capacity;
            if (WNetGetConnection(drive, buffer, ref length) == NoError)
            {
                var share = buffer.ToString().TrimEnd('\\');
                var rest = full.Length > 3 ? full[3..] : string.Empty;
                return string.IsNullOrEmpty(rest) ? share : share + "\\" + rest;
            }
        }

        return full;
    }

    public static bool NeedsFastPoll(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var info = new DriveInfo(root);
            return info.DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
