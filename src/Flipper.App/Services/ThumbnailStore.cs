using System.Security.Cryptography;
using System.Text;

namespace Flipper.App.Services;

public static class ThumbnailStore
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Flipper",
        "thumbs");

    public static string PathFor(string canonicalPath, long length, DateTime lastWriteUtc)
    {
        var key = $"{canonicalPath}|{length}|{lastWriteUtc.Ticks}|title-band-v1";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(DirectoryPath, hash + ".png");
    }
}
