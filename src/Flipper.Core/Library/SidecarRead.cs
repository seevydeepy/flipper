namespace Flipper.Core.Library;

internal static class SidecarRead
{
    public static FileStream? OpenOptional(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            // A failed open alone does not establish that the share is reachable and the sidecar is absent.
            if (Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!, Path.GetFileName(path)).Any())
            {
                throw;
            }

            return null;
        }
    }
}
