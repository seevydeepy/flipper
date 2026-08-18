using Flipper.App.Views;
using Flipper.Core.Library;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Flipper.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var icon = EnsureIconFile();
        if (System.IO.File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }
    }

    public void ShowLibrary()
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        RootFrame.Navigate(typeof(LibraryPage));
    }

    public void ShowReader(ScoreEntry score, string cachePath)
    {
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        RootFrame.Navigate(typeof(ReaderPage), new ReaderOpenArgs(score, cachePath));
    }

    private static string EnsureIconFile()
    {
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(beside))
        {
            return beside;
        }

        var fallback = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flipper",
            "AppIcon.ico");
        if (System.IO.File.Exists(fallback))
        {
            return fallback;
        }

        var names = typeof(MainWindow).Assembly.GetManifestResourceNames();
        var name = names.FirstOrDefault(item => item.EndsWith("AppIcon.ico", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return beside;
        }

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fallback)!);
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return beside;
        }

        using var file = System.IO.File.Create(fallback);
        stream.CopyTo(file);
        return fallback;
    }
}

public sealed record ReaderOpenArgs(ScoreEntry Score, string CachePath);
