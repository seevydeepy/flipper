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
    public LibrarySnapshot? LastSnapshot { get; set; }

    public App()
    {
        InitializeComponent();
        Settings = SettingsStore.Load();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();
        Window.ShowLibrary();
    }

    public void PersistSettings()
    {
        SettingsStore.Save(Settings);
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

    public void ForgetDeletedScore(PendingDeleteCommit commit)
    {
        Cache.Remove(commit.CanonicalPath);
        Settings.Scores.Remove(commit.CanonicalPath);
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
