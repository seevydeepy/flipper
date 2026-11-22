using Flipper.Core.Cache;
using Flipper.Core.Library;
using Flipper.Core.Settings;
using Microsoft.UI.Xaml;

namespace Flipper.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public MainWindow? Window { get; private set; }
    public SettingsStore SettingsStore { get; } = SettingsStore.ForAppData();
    public AppSettings Settings { get; }
    public ScoreCache Cache { get; } = ScoreCache.ForAppData();
    public PendingScoreDeletes PendingDeletes { get; } = new();
    public string? OpenCanonicalPath { get; set; }
    public int PlaylistRevision { get; private set; }
    internal List<Playlist> LegacyPlaylists { get; private set; }

    public App()
    {
        InitializeComponent();
        Settings = SettingsStore.Load();
        LegacyPlaylists = PlaylistBook.Copy(Settings.Playlists);
        Settings.SearchQuery = string.Empty;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
        Window.ShowLibrary();
    }

    internal static bool PrepareCollectionRename(string root)
    {
        var legacy = Path.Combine(root, LibraryFolders.LegacyCollectionName);
        var current = Path.Combine(root, LibraryFolders.CollectionName);
        if (Directory.Exists(legacy) || !Directory.Exists(current))
        {
            return false;
        }

        ScoreCatalog.TryRewriteRootFolder(root, LibraryFolders.LegacyCollectionName, LibraryFolders.CollectionName);
        return true;
    }

    internal void ApplyCollectionRename()
    {
        if (LibraryPathRewrite.RewriteRootFolder(Settings, LibraryFolders.LegacyCollectionName, LibraryFolders.CollectionName))
        {
            PersistSettings();
            SavePlaylists(Settings.Playlists);
        }
    }

    public void PersistSettings()
    {
        SettingsStore.Save(Settings, LegacyPlaylists);
    }

    internal void FinishPlaylistMigration()
    {
        if (LegacyPlaylists.Count == 0) return;
        LegacyPlaylists = [];
        PersistSettings();
    }

    private bool SavePlaylists(List<Playlist> playlists)
    {
        if (!PlaylistLibrary.Save(Settings.LibraryPath, playlists)) return false;
        Settings.Playlists = playlists;
        PlaylistRevision++;
        return true;
    }

    public void UseLibraryPath(string path)
    {
        Settings.LibraryPath = path;
        LegacyPlaylists = [];
        Settings.Playlists = [];
        Settings.SelectedPlaylistId = null;
        Settings.SelectedFolder = null;
        PersistSettings();
    }

    public ScoreEntry ApplyCanonical(ScoreEntry entry)
    {
        var canonical = Services.PathCanonicalizer.Canonicalize(entry.DisplayFullPath);
        return entry with { CanonicalPath = canonical };
    }

    public IReadOnlyList<ScoreEntry> ApplyCanonical(IReadOnlyList<ScoreEntry> scores, string displayRoot)
    {
        var root = Services.PathCanonicalizer.Canonicalize(displayRoot);
        var result = new ScoreEntry[scores.Count];
        for (var i = 0; i < scores.Count; i++)
        {
            var entry = scores[i];
            var name = Path.GetFileName(entry.DisplayFullPath);
            var canonical = string.IsNullOrEmpty(entry.RelativeFolder)
                ? Path.Combine(root, name)
                : Path.Combine(root, entry.RelativeFolder, name);
            result[i] = entry with { CanonicalPath = canonical };
        }

        return result;
    }

    public void RecordPlay(string canonicalPath)
    {
        var stats = Settings.StatsFor(canonicalPath);
        stats.PlayCount += 1;
        stats.LastPlayedUtc = DateTime.UtcNow;
        Settings.LastScoreCanonicalPath = canonicalPath;
        PersistSettings();
    }

    public void ToggleFavourite(string canonicalPath)
    {
        var stats = Settings.StatsFor(canonicalPath);
        stats.Favourite = !stats.Favourite;
        PersistSettings();
    }

    public bool AddFavourite(string canonicalPath)
    {
        var stats = Settings.StatsFor(canonicalPath);
        if (stats.Favourite)
        {
            return false;
        }

        stats.Favourite = true;
        PersistSettings();
        return true;
    }

    public Playlist? TryCreatePlaylist(string name)
    {
        var next = PlaylistBook.Copy(Settings.Playlists);
        if (!PlaylistBook.TryCreate(next, name, out var playlist) || !SavePlaylists(next))
        {
            return null;
        }

        PersistSettings();
        return playlist;
    }

    public bool AddToPlaylist(string playlistId, string canonicalPath)
    {
        var next = PlaylistBook.Copy(Settings.Playlists);
        var playlist = PlaylistBook.Find(next, playlistId);
        return playlist is not null && PlaylistBook.AddScore(playlist, canonicalPath) && SavePlaylists(next);
    }

    public bool RemoveFromPlaylist(string playlistId, string canonicalPath)
    {
        var next = PlaylistBook.Copy(Settings.Playlists);
        var playlist = PlaylistBook.Find(next, playlistId);
        return playlist is not null && PlaylistBook.RemoveScore(playlist, canonicalPath) && SavePlaylists(next);
    }

    public bool DeletePlaylist(string playlistId)
    {
        var next = PlaylistBook.Copy(Settings.Playlists);
        if (!PlaylistBook.Delete(next, playlistId) || !SavePlaylists(next))
        {
            return false;
        }

        if (string.Equals(Settings.SelectedPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase))
        {
            Settings.SelectedPlaylistId = null;
        }

        PersistSettings();
        return true;
    }

    public IReadOnlyList<string> TakePlaylistMembership(string canonicalPath)
    {
        var ids = PlaylistBook.IdsContaining(Settings.Playlists, canonicalPath);
        if (ids.Count == 0)
        {
            return ids;
        }

        var next = PlaylistBook.Copy(Settings.Playlists);
        PlaylistBook.RemovePath(next, canonicalPath);
        return SavePlaylists(next) ? ids : [];
    }

    public void RestorePlaylistMembership(IEnumerable<string> playlistIds, string canonicalPath)
    {
        var next = PlaylistBook.Copy(Settings.Playlists);
        var changed = false;
        foreach (var id in playlistIds)
        {
            var playlist = PlaylistBook.Find(next, id);
            if (playlist is null || !PlaylistBook.AddScore(playlist, canonicalPath))
            {
                continue;
            }

            changed = true;
        }

        if (changed)
        {
            SavePlaylists(next);
        }
    }

    public void ForgetDeletedScore(PendingDeleteCommit commit)
    {
        Cache.Remove(commit.CanonicalPath);
        Settings.Scores.Remove(commit.CanonicalPath);
        TakePlaylistMembership(commit.CanonicalPath);
        if (string.Equals(Settings.LastScoreCanonicalPath, commit.CanonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            Settings.LastScoreCanonicalPath = null;
        }

        PersistSettings();
        var thumb = Services.ThumbnailStore.PathFor(commit.CanonicalPath, commit.Length, commit.LastWriteUtc);
        if (!File.Exists(thumb))
        {
            return;
        }

        try
        {
            File.Delete(thumb);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
