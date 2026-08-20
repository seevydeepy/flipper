namespace Flipper.Core.Library;

internal static class CatalogWriteLock
{
    public const string FileName = ".flipper-catalog.lock";
    private const int Attempts = 20;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(25);

    public static FileStream? TryAcquire(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt + 1 < Attempts)
            {
                if (cancellationToken.WaitHandle.WaitOne(Delay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }
}
