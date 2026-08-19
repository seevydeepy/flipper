using Flipper.App.Views;
using Flipper.Core.Library;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Flipper.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<Guid, LiveToast> _toasts = new();

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
        var scale = App.Current.Settings.UiScalePercent / 100.0;
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

        return new Border
        {
            Background = paper,
            BorderBrush = gold,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = row
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
