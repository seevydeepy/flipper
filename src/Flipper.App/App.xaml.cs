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
    public string? OpenCanonicalPath { get; set; }

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
}
