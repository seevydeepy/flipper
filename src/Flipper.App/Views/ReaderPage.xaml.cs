using Flipper.App.Services;
using Flipper.Core.Library;
using Flipper.Core.Reader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System.Display;

namespace Flipper.App.Views;

public sealed partial class ReaderPage : Page
{
    private ScoreEntry? _score;
    private PdfPageSource? _pdf;
    private DisplayRequest? _displayRequest;
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _lowestVisible;
    private bool _swiped;
    private bool _ready;

    public ReaderPage()
    {
        InitializeComponent();
        _overlayTimer.Tick += (_, _) =>
        {
            _overlayTimer.Stop();
            Overlay.Visibility = Visibility.Collapsed;
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not ReaderOpenArgs args)
        {
            return;
        }

        _score = args.Score;
        TitleLabel.Text = args.Score.DisplayName;
        try
        {
            _pdf = new PdfPageSource(args.CachePath);
        }
        catch (Exception)
        {
            _pdf = null;
        }

        _lowestVisible = App.Current.Settings.LastScoreCanonicalPath == args.Score.CanonicalPath
            ? App.Current.Settings.LastPageIndex
            : 0;
        if (_pdf is not null)
        {
            _lowestVisible = Math.Clamp(_lowestVisible, 0, Math.Max(0, _pdf.PageCount - 1));
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        ReaderRoot.Focus(FocusState.Programmatic);
        _displayRequest = new DisplayRequest();
        _displayRequest.RequestActive();
        Draw();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _overlayTimer.Stop();
        try
        {
            _displayRequest?.RequestRelease();
        }
        catch (Exception)
        {
        }

        _displayRequest = null;
        _pdf?.Dispose();
        _pdf = null;
        App.Current.OpenCanonicalPath = null;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => App.Current.Window?.ShowLibrary();

    private void ReaderRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            App.Current.Window?.ShowLibrary();
            e.Handled = true;
            return;
        }

        if (e.Key is Windows.System.VirtualKey.Right or Windows.System.VirtualKey.PageDown or Windows.System.VirtualKey.Space)
        {
            Turn(1);
            e.Handled = true;
        }
        else if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.PageUp)
        {
            Turn(-1);
            e.Handled = true;
        }
    }

    private void ReaderRoot_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (Math.Abs(e.Cumulative.Translation.X) <= 80)
        {
            return;
        }

        _swiped = true;
        Turn(e.Cumulative.Translation.X < 0 ? 1 : -1);
    }

    private void ReaderRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_swiped)
        {
            _swiped = false;
            return;
        }

        var point = e.GetCurrentPoint(ReaderRoot).Position;
        var width = ReaderRoot.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        if (point.X <= width * 0.20)
        {
            Turn(-1);
            return;
        }

        if (point.X >= width * 0.80)
        {
            Turn(1);
            return;
        }

        ToggleOverlay();
    }

    private void ReaderRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready)
        {
            Draw();
        }
    }

    private void ToggleOverlay()
    {
        if (Overlay.Visibility == Visibility.Visible)
        {
            Overlay.Visibility = Visibility.Collapsed;
            _overlayTimer.Stop();
            return;
        }

        Overlay.Visibility = Visibility.Visible;
        _overlayTimer.Stop();
        _overlayTimer.Start();
    }

    private void Turn(int direction)
    {
        if (_pdf is null)
        {
            return;
        }

        var portrait = PageLayout.IsPortrait(ReaderRoot.ActualWidth, ReaderRoot.ActualHeight);
        _lowestVisible = PageLayout.Turn(_lowestVisible, _pdf.PageCount, portrait, direction);
        PersistPage();
        Draw();
    }

    private void PersistPage()
    {
        if (_score is null)
        {
            return;
        }

        App.Current.Settings.LastScoreCanonicalPath = _score.CanonicalPath;
        App.Current.Settings.LastPageIndex = _lowestVisible;
        App.Current.PersistSettings();
    }

    private void Draw()
    {
        if (_pdf is null)
        {
            LeftImage.Source = null;
            RightImage.Source = null;
            LeftError.Visibility = Visibility.Visible;
            RightError.Visibility = Visibility.Collapsed;
            RightColumn.Width = new GridLength(0);
            PageLabel.Text = string.Empty;
            Overlay.Visibility = Visibility.Visible;
            _overlayTimer.Stop();
            return;
        }

        var portrait = PageLayout.IsPortrait(ReaderRoot.ActualWidth, ReaderRoot.ActualHeight);
        var pages = PageLayout.For(_pdf.PageCount, _lowestVisible, portrait);
        _lowestVisible = pages.FirstIndex;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var slotWidth = PagesGrid.ActualWidth > 0 ? PagesGrid.ActualWidth : ReaderRoot.ActualWidth;
        if (pages.SecondIndex is not null)
        {
            slotWidth /= 2;
        }

        var pixelWidth = (int)Math.Clamp(slotWidth * scale, 320, 2400);

        LeftImage.Opacity = 0;
        var left = _pdf.Render(pages.FirstIndex, pixelWidth);
        LeftImage.Source = left;
        LeftImage.Opacity = 1;
        LeftError.Visibility = left is null ? Visibility.Visible : Visibility.Collapsed;
        if (left is null)
        {
            Overlay.Visibility = Visibility.Visible;
            _overlayTimer.Stop();
        }

        if (pages.SecondIndex is int second)
        {
            RightColumn.Width = new GridLength(1, GridUnitType.Star);
            RightImage.Opacity = 0;
            var right = _pdf.Render(second, pixelWidth);
            RightImage.Source = right;
            RightImage.Opacity = 1;
            RightError.Visibility = right is null ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            RightColumn.Width = new GridLength(0);
            RightImage.Source = null;
            RightError.Visibility = Visibility.Collapsed;
        }

        var firstDisplay = pages.FirstIndex + 1;
        var lastDisplay = (pages.SecondIndex ?? pages.FirstIndex) + 1;
        PageLabel.Text = firstDisplay == lastDisplay
            ? $"page {firstDisplay} of {_pdf.PageCount}"
            : $"page {firstDisplay}-{lastDisplay} of {_pdf.PageCount}";

        var next = pages.FirstIndex + pages.Step;
        _ = _pdf.PrefetchAsync(next, pixelWidth);
        if (pages.Step == 2)
        {
            _ = _pdf.PrefetchAsync(next + 1, pixelWidth);
        }
    }
}
