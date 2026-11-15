using System.Text.Json;
using Flipper.Core.Settings;

namespace Flipper.Core.Library;

public sealed record PlaylistLibraryChange(string Path, long Length, DateTime LastWriteUtc, List<Playlist> Playlists);

public sealed class PlaylistLibraryCache
{
    private string? _path;
    private long _length;
    private DateTime _lastWriteUtc;

    public bool TryRefresh(AppSettings settings)
    {
        var change = LoadIfChanged(settings.LibraryPath);
        if (change is null) return false;
        Apply(settings, change);
        return true;
    }

    // Returns a detached value so callers can read on a worker and apply on the UI thread.
    public PlaylistLibraryChange? LoadIfChanged(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var path = Path.Combine(root, PlaylistLibrary.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (_path is not null
            && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)
            && _length == info.Length
            && _lastWriteUtc == info.LastWriteTimeUtc)
        {
            return null;
        }

        var playlists = PlaylistLibrary.Load(root);
        return new PlaylistLibraryChange(info.FullName, info.Length, info.LastWriteTimeUtc, playlists);
    }

    public void Apply(AppSettings settings, PlaylistLibraryChange change)
    {
        settings.Playlists = change.Playlists;
        settings.Normalize();
        _path = change.Path;
        _length = change.Length;
        _lastWriteUtc = change.LastWriteUtc;
    }

    public void Remember(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            Clear();
            return;
        }

        var path = Path.Combine(root, PlaylistLibrary.FileName);
        if (!File.Exists(path))
        {
            Clear();
            return;
        }

        Remember(new FileInfo(path));
    }

    private void Remember(FileInfo info)
    {
        _path = info.FullName;
        _length = info.Length;
        _lastWriteUtc = info.LastWriteTimeUtc;
    }

    private void Clear()
    {
        _path = null;
        _length = 0;
        _lastWriteUtc = default;
    }
}

public static class PlaylistLibrary
{
    public const string FileName = ".flipper-playlists.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static List<Playlist> Load(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }

        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<Playlist>>(json, JsonOptions);
            return PlaylistBook.Sanitize(loaded);
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static bool Save(string? root, IEnumerable<Playlist> playlists)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, FileName);
            var json = JsonSerializer.Serialize(PlaylistBook.Sanitize(playlists), JsonOptions);
            SidecarReplace.Write(path, json);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Hydrate(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LibraryPath))
        {
            return false;
        }

        var fromDisk = Load(settings.LibraryPath);
        var fromSettings = PlaylistBook.Sanitize(settings.Playlists);
        if (fromDisk.Count == 0 && fromSettings.Count == 0)
        {
            settings.Playlists = [];
            return false;
        }

        var merged = Merge(fromDisk, fromSettings);
        settings.Playlists = merged;
        settings.Normalize();
        if (merged.Count == 0)
        {
            settings.SelectedPlaylistId = null;
        }

        return fromSettings.Count > 0 && Save(settings.LibraryPath, merged);
    }

    public static bool BindToRoot(AppSettings settings, string path)
    {
        settings.LibraryPath = path;
        settings.SelectedPlaylistId = null;
        settings.Playlists = [];
        return Hydrate(settings);
    }

    private static List<Playlist> Merge(List<Playlist> library, List<Playlist> local)
    {
        var result = library.ToList();
        foreach (var playlist in local)
        {
            var existing = result.FirstOrDefault(item =>
                string.Equals(item.Id, playlist.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Name, playlist.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                result.Add(playlist);
                continue;
            }

            foreach (var path in playlist.CanonicalPaths)
            {
                PlaylistBook.AddScore(existing, path);
            }
        }

        return result;
    }
}
