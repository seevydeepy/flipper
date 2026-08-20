namespace Flipper.Core.Library;

internal static class SidecarReplace
{
    public static void Write(string path, string contents)
    {
        var previous = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : DateTime.MinValue;
        var tmp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, contents);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

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
