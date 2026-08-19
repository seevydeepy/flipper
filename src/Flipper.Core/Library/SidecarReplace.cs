namespace Flipper.Core.Library;

internal static class SidecarReplace
{
    public static void Write(string path, string contents)
    {
        var previous = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : DateTime.MinValue;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);

        var current = File.GetLastWriteTimeUtc(path);
        if (current > previous)
        {
            return;
        }

        var stamp = DateTime.UtcNow;
        if (stamp <= previous)
        {
            stamp = previous.AddSeconds(2);
        }

        try
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
