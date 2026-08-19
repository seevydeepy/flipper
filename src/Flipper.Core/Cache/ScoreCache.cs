using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Flipper.Core.Cache;

public sealed class ScoreCache
{
    public const int MaxEntries = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly string _indexPath;

    public ScoreCache(string directory)
    {
        _directory = directory;
        _indexPath = Path.Combine(directory, "index.json");
        Directory.CreateDirectory(directory);
    }

    public static ScoreCache ForAppData()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flipper",
            "cache");
        return new ScoreCache(dir);
    }

    public IReadOnlyList<CacheIndexEntry> ListRecent()
    {
        return LoadIndex().Entries
            .OrderByDescending(entry => entry.LastOpenedUtc)
            .ToArray();
    }

    public string? TryOpen(
        string canonicalPath,
        string? livePath,
        string displayPath,
        string? currentlyOpenCanonical)
    {
        var index = LoadIndex();
        string? cacheFile = null;

        if (!string.IsNullOrWhiteSpace(livePath) && File.Exists(livePath))
        {
            var fileName = FileNameFor(canonicalPath);
            cacheFile = Path.Combine(_directory, fileName);
            var tmp = cacheFile + ".tmp";
            File.Copy(livePath, tmp, overwrite: true);
            File.Move(tmp, cacheFile, overwrite: true);
            var info = new FileInfo(livePath);
            Upsert(index, canonicalPath, displayPath, info.Length, info.LastWriteTimeUtc, fileName);
        }
        else
        {
            var existing = index.Entries.FirstOrDefault(entry =>
                string.Equals(entry.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return null;
            }

            cacheFile = Path.Combine(_directory, existing.FileName);
            if (!File.Exists(cacheFile))
            {
                return null;
            }

            existing.LastOpenedUtc = DateTime.UtcNow;
            existing.DisplayPath = displayPath;
        }

        Evict(index, currentlyOpenCanonical, canonicalPath);
        SaveIndex(index);
        return cacheFile;
    }

    public bool HasCopy(string canonicalPath)
    {
        var existing = LoadIndex().Entries.FirstOrDefault(entry =>
            string.Equals(entry.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase));
        return existing is not null && File.Exists(Path.Combine(_directory, existing.FileName));
    }

    public void Remove(string canonicalPath)
    {
        var index = LoadIndex();
        var existing = index.Entries.FirstOrDefault(entry =>
            string.Equals(entry.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        index.Entries.Remove(existing);
        var path = Path.Combine(_directory, existing.FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        SaveIndex(index);
    }

    private static void Upsert(
        CacheIndex index,
        string canonicalPath,
        string displayPath,
        long length,
        DateTime lastWriteUtc,
        string fileName)
    {
        var existing = index.Entries.FirstOrDefault(entry =>
            string.Equals(entry.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new CacheIndexEntry { CanonicalPath = canonicalPath };
            index.Entries.Add(existing);
        }

        existing.DisplayPath = displayPath;
        existing.Length = length;
        existing.LastWriteUtc = lastWriteUtc;
        existing.LastOpenedUtc = DateTime.UtcNow;
        existing.FileName = fileName;
    }

    private void Evict(CacheIndex index, string? currentlyOpenCanonical, string newlyOpenCanonical)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            newlyOpenCanonical
        };
        if (!string.IsNullOrWhiteSpace(currentlyOpenCanonical))
        {
            protectedPaths.Add(currentlyOpenCanonical);
        }

        var overflow = index.Entries
            .Where(entry => !protectedPaths.Contains(entry.CanonicalPath))
            .OrderBy(entry => entry.LastOpenedUtc)
            .ToList();

        while (index.Entries.Count > MaxEntries && overflow.Count > 0)
        {
            var victim = overflow[0];
            overflow.RemoveAt(0);
            index.Entries.Remove(victim);
            var path = Path.Combine(_directory, victim.FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private CacheIndex LoadIndex()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                return new CacheIndex();
            }

            var json = File.ReadAllText(_indexPath);
            return JsonSerializer.Deserialize<CacheIndex>(json, JsonOptions) ?? new CacheIndex();
        }
        catch (JsonException)
        {
            return new CacheIndex();
        }
        catch (IOException)
        {
            return new CacheIndex();
        }
    }

    private void SaveIndex(CacheIndex index)
    {
        var json = JsonSerializer.Serialize(index, JsonOptions);
        var tmp = _indexPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _indexPath, overwrite: true);
    }

    private static string FileNameFor(string canonicalPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        return Convert.ToHexString(bytes).ToLowerInvariant() + ".pdf";
    }
}
