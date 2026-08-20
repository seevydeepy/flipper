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
    private readonly DispatcherTimer _heardTimer = new() { Interval = TimeSpan.FromSeconds(1.4) };
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly VoiceKeywordListener _voice = new();
    private int _voiceEpoch;
    private int _lowestVisible;
    private bool _swiped;
    private bool _ready;
    private bool _voiceOn;
    private bool _showingHeard;

    public ReaderPage()
    {
        InitializeComponent();
        _overlayTimer.Tick += (_, _) =>
        {
            _overlayTimer.Stop();
            Overlay.Visibility = Visibility.Collapsed;
        };
        _heardTimer.Tick += (_, _) =>
        {
            _heardTimer.Stop();
            _showingHeard = false;
            if (_voiceOn)
            {
                VoiceLabel.Text = LevelText();
            }
        };
        _levelTimer.Tick += (_, _) =>
        {
            if (_voiceOn && !_showingHeard)
            {
                VoiceLabel.Text = LevelText();
            }
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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        ReaderRoot.Focus(FocusState.Programmatic);
        _displayRequest = new DisplayRequest();
        _displayRequest.RequestActive();
        Draw();
        if (!App.Current.Settings.VoiceTurningEnabled)
        {
            VoiceLabel.Visibility = Visibility.Collapsed;
            VoiceLabel.Text = string.Empty;
            return;
        }

        VoiceLabel.Visibility = Visibility.Visible;
        var epoch = ++_voiceEpoch;
        var failure = await _voice.StartAsync(App.Current.Settings.MicrophoneDeviceId, OnVoiceKeyword);
        if (epoch != _voiceEpoch)
        {
            _voice.Stop();
            return;
        }

        _voiceOn = failure is null;
        VoiceLabel.Text = failure ?? LevelText();
        if (failure is not null)
        {
            _voice.Stop();
        }
        else
        {
            _levelTimer.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _ready = false;
        _voiceOn = false;
        _voiceEpoch++;
        _voice.Stop();
        _heardTimer.Stop();
        _levelTimer.Stop();
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

    private void OnVoiceKeyword(string keyword)
    {
        DispatcherQueue.TryEnqueue(() => ApplyVoice(keyword));
    }

    private void ApplyVoice(string keyword)
    {
        if (!_ready)
        {
            return;
        }

        var heard = keyword.Replace('_', ' ').ToLowerInvariant();
        switch (VoiceCommandParser.Parse(keyword))
        {
            case VoiceCommand.Next:
                ShowHeard(heard);
                Turn(1);
                break;
            case VoiceCommand.Back:
                ShowHeard(heard);
                if (_lowestVisible <= 0)
                {
                    App.Current.Window?.ShowLibrary();
                }
                else
                {
                    Turn(-1);
                }

                break;
            case VoiceCommand.Restart:
                ShowHeard(heard);
                if (_lowestVisible == 0)
                {
                    return;
                }

                _lowestVisible = 0;
                PersistPage();
                Draw();
                break;
            case VoiceCommand.Finish:
                ShowHeard(heard);
                App.Current.Window?.ShowLibrary();
                break;
        }
    }

    private void ShowHeard(string word)
    {
        _showingHeard = true;
        VoiceLabel.Text = word;
        _heardTimer.Stop();
        _heardTimer.Start();
    }

    private string LevelText()
    {
        var rms = _voice.LastRms;
        var bars = 0;
        if (rms > 0.002)
        {
            bars = Math.Clamp((int)MathF.Log10(rms * 400) + 1, 1, 6);
        }

        return "Listening " + new string('|', bars);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => App.Current.Window?.ShowLibrary();

    private void BackButton_PointerReleased(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void ReaderPage_KeyDown(object sender, KeyRoutedEventArgs e) => TryHandleTurnKey(e);

    private void ReaderRoot_KeyDown(object sender, KeyRoutedEventArgs e) => TryHandleTurnKey(e);

    public void TryHandleTurnKey(KeyRoutedEventArgs e)
    {
        if (e.Handled || !_ready)
        {
            return;
        }

        var command = PageTurnKeys.FromVirtualKey((int)e.OriginalKey, e.KeyStatus.WasKeyDown);
        if (command == PageTurnCommand.None && e.Key != e.OriginalKey)
        {
            command = PageTurnKeys.FromVirtualKey((int)e.Key, e.KeyStatus.WasKeyDown);
        }

        switch (command)
        {
            case PageTurnCommand.Next:
                Turn(1);
                break;
            case PageTurnCommand.Back:
                Turn(-1);
                break;
            case PageTurnCommand.Close:
                App.Current.Window?.ShowLibrary();
                break;
            default:
                return;
        }

        e.Handled = true;
        ReaderRoot.Focus(FocusState.Programmatic);
    }

    private void ReaderRoot_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (Math.Abs(e.Cumulative.Translation.X) <= 80)
        {
            return;
        }

        _swiped = true;
        Turn(e.Cumulative.Translation.X < 0 ? 1 : -1);
        ReaderRoot.Focus(FocusState.Programmatic);
    }

    private void ReaderRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_swiped)
        {
            _swiped = false;
            ReaderRoot.Focus(FocusState.Programmatic);
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
            ReaderRoot.Focus(FocusState.Programmatic);
            return;
        }

        if (point.X >= width * 0.80)
        {
            Turn(1);
            ReaderRoot.Focus(FocusState.Programmatic);
            return;
        }

        ToggleOverlay();
        ReaderRoot.Focus(FocusState.Programmatic);
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
        var scale = (XamlRoot?.RasterizationScale ?? 1) * App.Current.Settings.UiScalePercent / 100.0;
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
