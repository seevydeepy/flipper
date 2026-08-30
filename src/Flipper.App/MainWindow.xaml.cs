using Flipper.App.Views;
using Flipper.Core.Library;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Flipper.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<Guid, LiveToast> _toasts = new();
    private MediaPlayer? _cuePlayer;
    private bool _readerUnscaled;

    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        var icon = EnsureIconFile();
        if (System.IO.File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        ApplyUiScale();
    }

    public void ApplyUiScale()
    {
        var scale = _readerUnscaled ? 1.0 : App.Current.Settings.UiScalePercent / 100.0;
        var width = WindowRoot.ActualWidth;
        var height = WindowRoot.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        WindowRoot.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, height)
        };

        if (Math.Abs(scale - 1.0) < 0.001)
        {
            ScaleHost.RenderTransform = null;
            ScaleHost.Width = double.NaN;
            ScaleHost.Height = double.NaN;
            ScaleHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            ScaleHost.VerticalAlignment = VerticalAlignment.Stretch;
            return;
        }

        ScaleHost.HorizontalAlignment = HorizontalAlignment.Left;
        ScaleHost.VerticalAlignment = VerticalAlignment.Top;
        ScaleHost.Width = width / scale;
        ScaleHost.Height = height / scale;
        ScaleHost.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        ScaleHost.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
    }

    private void WindowRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyUiScale();
    }

    private void WindowRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (RootFrame.Content is ReaderPage reader)
        {
            reader.TryHandleTurnKey(e);
        }
    }

    public void ShowLibrary()
    {
        _readerUnscaled = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        ApplyUiScale();
        RootFrame.Navigate(typeof(LibraryPage));
    }

    public void ShowReader(ScoreEntry score, string cachePath)
    {
        _readerUnscaled = true;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        ApplyUiScale();
        RootFrame.Navigate(typeof(ReaderPage), new ReaderOpenArgs(score, cachePath));
    }

    public void NotifyAddedToPlaylist(string playlistName)
    {
        ShowInfoToast($"Added to playlist {playlistName}");
        PlayCue("playlist-add.wav");
    }

    public void PlayDeleteCue() => PlayCue("delete.wav");

    public void ShowDeleteToast(PendingScoreDelete item)
    {
        if (_toasts.ContainsKey(item.Id))
        {
            return;
        }

        var root = BuildToast(item.Id);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => Expire(item.Id);
        _toasts[item.Id] = new LiveToast(item.Id, root, timer);
        ToastHost.Children.Add(root);
        timer.Start();
    }

    private void ShowInfoToast(string message)
    {
        var id = Guid.NewGuid();
        var root = BuildInfoToast(message);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => Dismiss(id);
        _toasts[id] = new LiveToast(id, root, timer);
        ToastHost.Children.Add(root);
        timer.Start();
    }

    private UIElement BuildToast(Guid id)
    {
        var ink = (Brush)Application.Current.Resources["InkBrush"];
        var paper = (Brush)Application.Current.Resources["CardBrush"];
        var gold = (Brush)Application.Current.Resources["GoldBrush"];
        var text = new TextBlock
        {
            Text = "Item Deleted.",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        var undo = new Button
        {
            Content = "Undo",
            MinHeight = 40,
            MinWidth = 96,
            FontSize = 16,
            Padding = new Thickness(16, 6, 16, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Background = gold,
            Foreground = paper
        };
        undo.Click += (_, _) => Undo(id);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(text);
        row.Children.Add(undo);

        return ToastChrome(row);
    }

    private static UIElement BuildInfoToast(string message)
    {
        var ink = (Brush)Application.Current.Resources["InkBrush"];
        var text = new TextBlock
        {
            Text = message,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center
        };
        return ToastChrome(text);
    }

    private static Border ToastChrome(UIElement child)
    {
        var paper = (Brush)Application.Current.Resources["CardBrush"];
        var gold = (Brush)Application.Current.Resources["GoldBrush"];
        return new Border
        {
            Background = paper,
            BorderBrush = gold,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = child
        };
    }

    private void Undo(Guid id)
    {
        App.Current.PendingDeletes.TryUndo(id);
        if (RootFrame.Content is LibraryPage page)
        {
            page.RefreshFilter();
        }

        Dismiss(id);
    }

    private void Expire(Guid id)
    {
        if (!_toasts.ContainsKey(id))
        {
            return;
        }

        var result = App.Current.PendingDeletes.Commit(id);
        if (result is null || result.Failed)
        {
            if (result is { Failed: true })
            {
                App.Current.PendingDeletes.TryUndo(id);
                if (RootFrame.Content is LibraryPage page)
                {
                    page.RefreshFilter();
                    page.ShowCannotDelete();
                }
            }

            Dismiss(id);
            return;
        }

        if (App.Current.LastSnapshot is { } snapshot)
        {
            App.Current.LastSnapshot = snapshot.Without(result.CanonicalPath);
        }

        if (RootFrame.Content is LibraryPage library)
        {
            library.ForgetCommitted(result.CanonicalPath);
        }

        App.Current.ForgetDeletedScore(result);
        Dismiss(id);
    }

    private void Dismiss(Guid id)
    {
        if (!_toasts.Remove(id, out var toast))
        {
            return;
        }

        toast.Timer.Stop();
        ToastHost.Children.Remove(toast.Root);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        foreach (var toast in _toasts.Values)
        {
            toast.Timer.Stop();
        }

        _toasts.Clear();
        ToastHost.Children.Clear();
        _cuePlayer?.Dispose();
        _cuePlayer = null;
    }

    private void PlayCue(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        _cuePlayer ??= new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.SoundEffects
        };
        _cuePlayer.CommandManager.IsEnabled = false;
        _cuePlayer.Source = MediaSource.CreateFromUri(new Uri(path));
        _cuePlayer.Play();
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

    private sealed record LiveToast(Guid Id, UIElement Root, DispatcherTimer Timer);
}

public sealed record ReaderOpenArgs(ScoreEntry Score, string CachePath);
