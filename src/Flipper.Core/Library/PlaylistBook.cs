namespace Flipper.Core.Library;

public sealed class Playlist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> CanonicalPaths { get; set; } = new();
}

public static class PlaylistBook
{
    public static bool TryCreate(IList<Playlist> list, string name, out Playlist playlist)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || list.Any(item => string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            playlist = new Playlist();
            return false;
        }

        playlist = new Playlist
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = trimmed
        };
        list.Add(playlist);
        return true;
    }

    public static Playlist? Find(IList<Playlist> list, string id)
    {
        return list.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool AddScore(Playlist playlist, string? canonicalPath)
    {
        if (string.IsNullOrEmpty(canonicalPath))
        {
            return false;
        }

        if (playlist.CanonicalPaths.Any(path => string.Equals(path, canonicalPath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        playlist.CanonicalPaths.Add(canonicalPath);
        return true;
    }

    public static bool RemoveScore(Playlist playlist, string? canonicalPath)
    {
        if (string.IsNullOrEmpty(canonicalPath))
        {
            return false;
        }

        var index = playlist.CanonicalPaths.FindIndex(path =>
            string.Equals(path, canonicalPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        playlist.CanonicalPaths.RemoveAt(index);
        return true;
    }

    public static void RemovePath(IList<Playlist> list, string canonicalPath)
    {
        foreach (var playlist in list)
        {
            RemoveScore(playlist, canonicalPath);
        }
    }

    public static bool Delete(IList<Playlist> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (!string.Equals(list[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.RemoveAt(i);
            return true;
        }

        return false;
    }

    public static List<Playlist> Sanitize(IEnumerable<Playlist>? list)
    {
        return (list ?? [])
            .Where(playlist => !string.IsNullOrWhiteSpace(playlist.Id) && !string.IsNullOrWhiteSpace(playlist.Name))
            .Select(DistinctPaths)
            .ToList();
    }

    private static Playlist DistinctPaths(Playlist playlist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        playlist.CanonicalPaths ??= new List<string>();
        playlist.CanonicalPaths = playlist.CanonicalPaths
            .Where(path => !string.IsNullOrEmpty(path) && seen.Add(path))
            .ToList();
        return playlist;
    }
}
