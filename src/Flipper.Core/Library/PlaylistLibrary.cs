using System.Text.Json;
using Flipper.Core.Settings;

namespace Flipper.Core.Library;

public sealed record PlaylistLibraryChange(string Path, long Length, DateTime LastWriteUtc, List<Playlist> Playlists);

public sealed class PlaylistLibraryCache
{
    private const long MissingLength = -1;
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

        var path = Path.GetFullPath(Path.Combine(root, PlaylistLibrary.FileName));
        using var stream = SidecarRead.OpenOptional(path);
        var length = stream?.Length ?? MissingLength;
        var lastWriteUtc = stream is null ? default : File.GetLastWriteTimeUtc(stream.SafeFileHandle);

        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)
            && _length == length
            && _lastWriteUtc == lastWriteUtc)
        {
            return null;
        }

        using var reader = stream is null ? null : new StreamReader(stream, leaveOpen: true);
        return new PlaylistLibraryChange(path, length, lastWriteUtc,
            reader is null ? [] : PlaylistLibrary.Read(reader, path));
    }

    public void Apply(AppSettings settings, PlaylistLibraryChange change)
    {
        settings.Playlists = change.Playlists;
        settings.Normalize();
        if (settings.Playlists.Count == 0)
        {
            settings.SelectedPlaylistId = null;
        }
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
            using var reader = File.OpenText(path);
            return Read(reader, path);
        }
        catch (IOException)
        {
            return [];
        }
    }

    internal static List<Playlist> Read(TextReader reader, string path)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<List<Playlist>>(reader.ReadToEnd(), JsonOptions);
            if (loaded is null || loaded.Any(playlist => playlist is null))
            {
                throw new JsonException("Expected a list of playlists.");
            }

            return PlaylistBook.Sanitize(loaded);
        }
        catch (JsonException exception)
        {
            throw new IOException($"Cannot read playlists from '{path}'.", exception);
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

    public static PlaylistLibraryChange LoadAndMigrate(string root, List<Playlist> legacyPlaylists, out bool migrated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var current = new PlaylistLibraryCache().LoadIfChanged(root)!;
        var legacy = PlaylistBook.Sanitize(PlaylistBook.Copy(legacyPlaylists));
        migrated = false;
        if (legacy.Count == 0) return current;

        var merged = Merge(PlaylistBook.Copy(current.Playlists), legacy);
        migrated = Save(root, merged);
        // Keep the read fingerprint so the next refresh detects the replacement.
        return migrated ? current with { Playlists = merged } : current;
    }

    public static bool Hydrate(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LibraryPath))
        {
            return false;
        }

        List<Playlist> fromDisk;
        try
        {
            fromDisk = new PlaylistLibraryCache().LoadIfChanged(settings.LibraryPath)!.Playlists;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

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
