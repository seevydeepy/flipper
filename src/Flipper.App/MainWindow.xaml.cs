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
        if (System.IO.File.Exists("Assets/AppIcon.ico"))
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
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
}

public sealed record ReaderOpenArgs(ScoreEntry Score, string CachePath);
