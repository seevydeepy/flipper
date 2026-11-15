using Flipper.App.Services;
using Flipper.Core.Reader;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using static Flipper.Core.Reader.WinkCalibrationPhase;

namespace Flipper.App.Views;

internal sealed class WinkCalibrationPanel : UserControl
{
    private const string PauseGlyph = "\uE769";
    private const string PlayGlyph = "\uE768";
    private const string CameraGlyph = "\uE714";
    private const string EyeGlyph = "\uE890";
    private const string WorkingGlyph = "\uE895";
    private const string DoneGlyph = "\uE930";
    private const string TickGlyph = "\uE73E";
    private const string WarningGlyph = "\uE7BA";
    private const string InfoGlyph = "\uE946";
    private const string FaceFound = "Face found";
    private const string InactiveMessage = "Camera paused because the window was not active. Your progress is kept.";
    private static readonly string[] StageNames = ["Open eyes", "Collect samples", "Final checks"];
    private static readonly string[] Waiting = ["Starting camera…", "Finding your face…", "Camera paused"];
    private readonly SolidColorBrush _pauseBrush = new(Color.FromArgb(255, 0xFF, 0xC2, 0x1A));
    private readonly SolidColorBrush _resumeBrush = new(Color.FromArgb(255, 0x3F, 0xD4, 0x7A));
    private readonly Window _window;
    private readonly Action<WinkCalibrationProfile> _save;
    private readonly Action _close;
    private readonly bool _recalibrating;
    private readonly WinkFacePreview _preview = new() { Height = 300, MaxWidth = 480 };
    private readonly Border _trackingPill;
    private readonly FrameworkElement _feedbackRow;
    private readonly WinkCalibrationMeter _meter = new();
    private readonly CalibrationStages _stages = new(StageNames);
    private readonly Border _iconBubble;
    private readonly TextBlock _icon = Ui.Icon(CameraGlyph, 30);
    private readonly TextBlock _instruction = Ui.Body();
    private readonly TextBlock _detail = Ui.Body();
    private readonly StackPanel _gestures = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _status = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _statusIcon = Ui.Icon(WarningGlyph, 18);
    private readonly TextBlock _statusText = Ui.Label(string.Empty);
    private readonly ProgressBar _timer = new() { Minimum = 0, Maximum = 100, MinHeight = 8 };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _count = Ui.Caption();
    private readonly CalibrationBanner _banner = new();
    private readonly Button _primary = Ui.Button("Start calibration", primary: true);
    private readonly Button _restart = Ui.Button("Restart");
    private readonly Button _cancel = Ui.Button("Cancel");
    private readonly Button _pause;
    private readonly TextBlock _pauseIcon = Ui.Icon(PauseGlyph, 36);
    private readonly TextBlock _pauseLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _footer;
    private readonly object _sessionLock = new();
    private readonly List<bool> _outcomes = [];
    private WinkCalibrationSession? _session;
    private string? _sessionCameraId;
    private string? _cameraId;
    private CancellationTokenSource? _cameraCts;
    private Task _cameraTask = Task.CompletedTask;
    private DisplayState? _last;
    private (BannerTone Tone, string Text)? _message;
    private string _tracking = "Starting camera…";
    private string? _shownInstruction;
    private int _correctAtStepStart;
    private int _cameraEpoch;
    private int _displayQueued;
    private long _displayAt;
    private bool _loaded;
    private bool _active;
    private bool _cameraReady;
    private bool _faceTracked;
    private bool _saved;
    private bool _pausedByUser;

    private sealed record DisplayState(WinkCalibrationPhase Phase, int Count, int Required, double StillProgress,
        bool IsSettling, bool NeedsMoreSamples, WinkCalibrationFailure Failure, int CollectionRound, int CheckRound,
        WinkCalibrationAssessment? Assessment, int FreshAttemptsCompleted, int FreshAttemptsRequired, int FreshCorrect);

    public WinkCalibrationPanel(Window window, Action<WinkCalibrationProfile> save, Action close)
    {
        _window = window;
        _save = save;
        _close = close;
        _recalibrating = App.Current.Settings.WinkCalibration is { IsValid: true };
        _instruction.FontSize = 24;
        _instruction.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_instruction, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_timer, "Open-eye measurement");

        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Title("Calibrate wink turning", level: AutomationHeadingLevel.Level1),
                Ui.Caption("Right wink: next page · Left wink: previous page")
            }
        };

        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _iconBubble = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _icon
        };
        var text = new StackPanel { Spacing = 6, Children = { _instruction, _detail } };
        Grid.SetColumn(text, 1);
        var prompt = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            Children = { _iconBubble, text }
        };
        _statusIcon.VerticalAlignment = VerticalAlignment.Center;
        _status.Children.Add(_statusIcon);
        _status.Children.Add(_statusText);
        var card = Ui.Card(new StackPanel
        {
            Spacing = 16,
            Children = { prompt, _gestures, _status, _timer, new StackPanel { Spacing = 6, Children = { _dots, _count } } }
        });

        _trackingPill = new Border
        {
            Background = Ui.Brush("CardBrush"),
            BorderBrush = Ui.Brush("GoldSoftBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var camera = new Grid { Children = { _preview, _trackingPill } };
        var feedback = new Grid
        {
            ColumnSpacing = 16,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(196) }
            },
            RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } }
        };
        feedback.Children.Add(camera);
        Grid.SetColumn(_meter, 1);
        feedback.Children.Add(_meter);
        feedback.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < 480;
            feedback.ColumnSpacing = stacked ? 0 : 16;
            feedback.RowSpacing = stacked ? 12 : 0;
            feedback.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 196);
            Grid.SetColumn(_meter, stacked ? 0 : 1);
            Grid.SetRow(_meter, stacked ? 1 : 0);
        };
        _feedbackRow = feedback;
        Content = new StackPanel { Spacing = 16, Children = { header, _stages, card, _banner, feedback } };

        _pause = new Button
        {
            Style = Ui.Style("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 96,
            FontSize = 28,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2),
            BorderBrush = Ui.Brush("InkBrush"),
            Foreground = Ui.Brush("InkBrush"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                Children = { _pauseIcon, _pauseLabel }
            }
        };
        _primary.MinHeight = _restart.MinHeight = _cancel.MinHeight = 64;
        _primary.FontSize = 20;
        _primary.HorizontalAlignment = HorizontalAlignment.Stretch;
        var main = new Grid { Children = { _primary, _pause } };
        var others = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _restart, _cancel } };
        _footer = new Border { Padding = new Thickness(24, 12, 24, 24), MaxWidth = 760, Child = Ui.Split(main, others, 520) };

        _primary.Click += Primary_Click;
        _restart.Click += (_, _) => StartSession();
        _cancel.Click += (_, _) => _close();
        _pause.Click += Pause_Click;
        _window.Activated += Window_Activated;
        Loaded += (_, _) => { _loaded = true; UpdateCamera(); };
        Unloaded += (_, _) => Stop();
        Render(null);
    }

    /// <summary>The buttons. The window keeps them below the scrolled content, so they are always visible.</summary>
    public FrameworkElement Footer => _footer;

    public void Stop()
    {
        _loaded = false;
        _window.Activated -= Window_Activated;
        lock (_sessionLock) _session = null;
        UpdateCamera();
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (active == _active) return;
        _active = active;
        UpdateCamera();
    }

    private async void UpdateCamera()
    {
        var epoch = ++_cameraEpoch;
        _cameraReady = false;
        _faceTracked = false;
        _cameraCts?.Cancel();
        _preview.Reset();
        bool running;
        lock (_sessionLock)
        {
            _session?.Pause();
            running = _session is { Phase: not Ready };
        }
        if (_active && _message?.Text == InactiveMessage) _message = null;
        else if (_loaded && !_saved && !_active && running) _message = (BannerTone.Info, InactiveMessage);
        Render(_last);
        await _cameraTask;
        if (epoch != _cameraEpoch) return;
        _cameraCts?.Dispose();
        _cameraCts = null;
        if (!_loaded || _saved || !_active)
        {
            if (_loaded && !_saved) SetTracking("Camera paused");
            return;
        }
        SetTracking("Starting camera…");
        _cameraCts = new CancellationTokenSource();
        _cameraTask = RunCameraAsync(epoch, _cameraCts.Token);
    }

    private async Task RunCameraAsync(int epoch, CancellationToken token)
    {
        try
        {
            await WinkCameraListener.RunAsync(_ => { }, status => DispatcherQueue.TryEnqueue(() =>
            {
                if (epoch != _cameraEpoch || token.IsCancellationRequested) return;
                _cameraReady = status == "Wink turning on";
                SetTracking(_cameraReady ? "Finding your face…" : CameraProblem(status));
            }), token, frame => OnFrame(frame, epoch, token));
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (epoch != _cameraEpoch) return;
                _cameraReady = false;
                Render(_last);
            });
        }
    }

    private void OnFrame(WinkCameraFrame frame, int epoch, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var tracked = frame.Eyes.TrackingStatus == "tracked";
        WinkCalibrationSession? session;
        WinkCalibrationFitRequest? fit;
        WinkCalibrationProfile? profile;
        DisplayState? display;
        lock (_sessionLock)
        {
            if (_session is not null && _sessionCameraId != frame.CameraId) _session = null;
            session = _session;
            if (!_pausedByUser) session?.Observe(new(frame.Timestamp.TotalMilliseconds, frame.Eyes.LeftOpenProbability,
                frame.Eyes.RightOpenProbability, tracked, frame.Eyes.RotationQuarterTurns));
            fit = session?.TakeFitRequest();
            profile = session?.Profile ?? App.Current.Settings.WinkCalibration;
            display = Snapshot(session);
        }
        var states = profile is { IsValid: true } && profile.CameraId == frame.CameraId
            ? WinkProbabilityClassifier.Classify(profile, frame.Eyes.LeftOpenProbability, frame.Eyes.RightOpenProbability)
            : (frame.Eyes.Left, frame.Eyes.Right);
        _preview.SetFrame(frame, states.Item1, states.Item2, token);
        if (fit is not null) _ = FitAsync(session!, fit);

        var now = Environment.TickCount64;
        if (now - _displayAt < 100 || Interlocked.Exchange(ref _displayQueued, 1) != 0) return;
        _displayAt = now;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (epoch != _cameraEpoch || token.IsCancellationRequested || !_loaded) return;
                _cameraId = frame.CameraId;
                _faceTracked = tracked;
                _tracking = frame.Eyes.TrackingStatus switch
                {
                    "tracked" => FaceFound,
                    "multiple_faces" => "Keep only your face in the camera view.",
                    "eyes_too_small" => "Move closer to the camera.",
                    "eye_crop_outside_frame" => "Keep your whole face in the camera view.",
                    _ => "Finding your face…"
                };
                // A snapshot taken before Start or Restart belongs to a session that is no longer shown.
                bool current;
                lock (_sessionLock) current = _session == session;
                Render(current ? display : _last);
            }
            finally { Interlocked.Exchange(ref _displayQueued, 0); }
        })) Interlocked.Exchange(ref _displayQueued, 0);
    }

    private async Task FitAsync(WinkCalibrationSession session, WinkCalibrationFitRequest request)
    {
        var result = await Task.Run(() => WinkCalibration.FitRepeated(request.CameraId, request.OpenSamples, request.Trials));
        lock (_sessionLock)
        {
            if (_session == session) session.ApplyFit(result);
        }
    }

    private static DisplayState? Snapshot(WinkCalibrationSession? session) => session is null ? null
        : new(session.Phase, session.PhaseSamples, session.RequiredSamples, session.StillProgress,
            session.IsSettling, session.NeedsMoreSamples, session.LastFailure, session.CollectionRound, session.CheckRound,
            session.Assessment, session.FreshAttemptsCompleted, session.FreshAttemptsRequired, session.FreshCorrectCount);

    private void SetTracking(string text)
    {
        _tracking = text;
        Render(_last);
    }

    private static string CameraProblem(string status)
    {
        var problem = status.Replace("Wink turning off — ", string.Empty);
        return problem.Length == 0 ? status : char.ToUpperInvariant(problem[0]) + problem[1..];
    }

    private static bool IsCheck(WinkCalibrationPhase phase) => phase is RightCheck or LeftCheck or BlinkCheck or OpenCheck;

    private static bool IsGesture(WinkCalibrationPhase phase) =>
        phase is RightWinks or LeftWinks or Blinks or RightCheck or LeftCheck or BlinkCheck;

    private static (string Name, string Plural) Gesture(WinkCalibrationPhase phase) => phase switch
    {
        RightWinks or RightCheck => ("Right wink", "right winks"),
        LeftWinks or LeftCheck => ("Left wink", "left winks"),
        _ => ("Blink", "blinks")
    };

    /// <summary>Compares two snapshots and describes what the user just achieved, or why the calibration changed course.</summary>
    private static (BannerTone Tone, string Text)? Describe(DisplayState last, DisplayState state)
    {
        var failedCheck = IsCheck(last.Phase) && !IsCheck(state.Phase) && state.Phase != Ready;
        if (state.CheckRound > last.CheckRound)
        {
            return state.CheckRound == 1
                ? (BannerTone.Success, "Your samples are good. Now the final checks.")
                : (BannerTone.Success, "Calibration updated. The final checks start again.");
        }
        if (state.CollectionRound > last.CollectionRound && state.CollectionRound > 1)
        {
            var reason = state.Failure == WinkCalibrationFailure.InsufficientEyeRange
                ? "Open and closed samples are too similar. More samples are needed."
                : "More samples are needed to separate winks from blinks.";
            return (BannerTone.Warning, (failedCheck ? "A check did not pass. " : string.Empty)
                + $"{reason} Round {state.CollectionRound} collects {state.Required} more of each gesture.");
        }
        if (state.Phase == Fitting && last.Phase != Fitting)
        {
            return failedCheck
                ? (BannerTone.Warning, "A check did not pass, so Carousel is updating your calibration.")
                : (BannerTone.Success, "Blinks collected.");
        }
        if (state.Phase != last.Phase)
        {
            return (last.Phase, state.Phase) switch
            {
                (Baseline, _) => (BannerTone.Success, "Open eyes measured."),
                (RightWinks, LeftWinks) => (BannerTone.Success, "Right winks collected."),
                (LeftWinks, Blinks) => (BannerTone.Success, "Left winks collected."),
                (RightCheck, LeftCheck) => (BannerTone.Success, "Right wink checks passed."),
                (LeftCheck, BlinkCheck) => (BannerTone.Success, "Left wink checks passed."),
                (BlinkCheck, OpenCheck) => (BannerTone.Success, "Blink checks passed."),
                (_, Ready) => (BannerTone.Success, "All checks passed."),
                _ => null
            };
        }
        if (state.Count <= last.Count || !IsGesture(state.Phase)) return null;
        var (name, _) = Gesture(state.Phase);
        if (!IsCheck(state.Phase)) return (BannerTone.Success, $"{name} {state.Count} of {state.Required} counted.");
        return state.FreshCorrect > last.FreshCorrect
            ? (BannerTone.Success, $"{name} check {state.Count} of {state.Required} passed.")
            : (BannerTone.Warning, $"That {name.ToLowerInvariant()} was not recognised. One miss is allowed, so the check continues.");
    }

    private void Render(DisplayState? state)
    {
        if (!_saved)
        {
            if (_last is { } last && state is not null)
            {
                if (Describe(last, state) is { } message) _message = message;
                if (last.Phase != state.Phase || last.CollectionRound != state.CollectionRound || last.CheckRound != state.CheckRound)
                {
                    _outcomes.Clear();
                    _correctAtStepStart = state.FreshCorrect;
                }
            }
            else if (_last is not null && state is null && _loaded)
            {
                _message = (BannerTone.Warning, "The camera changed. Start calibration again.");
                _outcomes.Clear();
            }
        }
        _last = state;
        if (state is not null)
        {
            // Each new gesture is a pass, or for a check, a tolerated miss.
            var newPasses = state.FreshCorrect - _correctAtStepStart - _outcomes.Count(outcome => outcome);
            while (_outcomes.Count < state.Count) _outcomes.Add(!IsCheck(state.Phase) || newPasses-- > 0);
        }

        _meter.Update(state?.Assessment, state?.Phase, state?.Failure ?? WinkCalibrationFailure.None,
            state?.FreshAttemptsCompleted ?? 0, state?.FreshAttemptsRequired ?? 0,
            state?.StillProgress ?? 0, state?.Count ?? 0, state?.Required ?? 0);
        RenderStages(state);
        RenderPrompt(state);
        RenderActivity(state);
        _banner.Show(_message?.Tone ?? BannerTone.Info, _message?.Text);
        _feedbackRow.Visibility = _saved ? Visibility.Collapsed : Visibility.Visible;
        RenderTracking();
        RenderButtons(state);
    }

    private void RenderStages(DisplayState? state)
    {
        var stage = _saved || state?.Phase == Ready ? StageNames.Length
            : state is null ? -1
            : state.Phase == Baseline ? 0
            : IsCheck(state.Phase) ? 2 : 1;
        for (var index = 0; index < StageNames.Length; index++)
        {
            var current = index == stage;
            _stages.Update(index, index < stage ? StageState.Done : current ? StageState.Current : StageState.Todo,
                index < stage ? "Done"
                : !current ? string.Empty
                : index == 0 ? "Now"
                : state!.Phase == Fitting ? "Checking"
                : $"Round {(index == 1 ? state.CollectionRound : state.CheckRound)}");
        }
    }

    private void RenderPrompt(DisplayState? state)
    {
        var (glyph, instruction, detail) = _saved
            ? (DoneGlyph, "Calibration saved",
                "In the reader, wink your right eye for the next page and your left eye for the previous page.")
            : state?.Phase switch
            {
                null => (CameraGlyph, "Before you start", IntroText()),
                Baseline => (EyeGlyph, "Keep both eyes comfortably open.",
                    "Look at the screen in your normal reading position. This takes a few seconds."),
                RightWinks => (EyeGlyph, "Wink your right eye repeatedly.",
                    "Close your right eye, then open both eyes. Relax both eyes between gestures."),
                LeftWinks => (EyeGlyph, "Wink your left eye repeatedly.",
                    "Close your left eye, then open both eyes. Relax both eyes between gestures."),
                Blinks => (EyeGlyph, "Blink both eyes repeatedly.",
                    "Close both eyes briefly, then open them. Relax both eyes between gestures."),
                RightCheck => (EyeGlyph, "Wink your right eye repeatedly.",
                    "This checks that right winks turn to the next page. Relax both eyes between gestures."),
                LeftCheck => (EyeGlyph, "Wink your left eye repeatedly.",
                    "This checks that left winks turn to the previous page. Relax both eyes between gestures."),
                BlinkCheck => (EyeGlyph, "Blink both eyes repeatedly.",
                    "This checks that blinks do not turn the page."),
                OpenCheck => (EyeGlyph, "Keep both eyes comfortably open.",
                    "This checks that open eyes do not turn the page."),
                Fitting => (WorkingGlyph, "Checking your samples…", state.NeedsMoreSamples
                    ? "Using the new samples to refine your calibration."
                    : "Assessing this round of samples."),
                _ => (DoneGlyph, "Your calibration is ready to save.", "Calibration checks complete.")
            };
        var finished = _saved || state?.Phase == Ready;
        _icon.Text = glyph;
        _icon.Foreground = Ui.Brush(finished ? "SuccessBrush" : "InkBrush");
        _iconBubble.Background = Ui.Brush(finished ? "SuccessSoftBrush" : "SurfaceBrush");
        _instruction.Text = instruction;
        _detail.Text = detail;
        if (_shownInstruction != instruction)
        {
            _shownInstruction = instruction;
            if (_instruction.IsLoaded)
            {
                FrameworkElementAutomationPeer.CreatePeerForElement(_instruction)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }

        // The gestures of this stage, in order: done, current or still to come.
        _gestures.Children.Clear();
        _gestures.Visibility = Visibility.Collapsed;
        if (_saved || state is null || state.Phase is Baseline or Ready) return;
        WinkCalibrationPhase[] steps = IsCheck(state.Phase)
            ? [RightCheck, LeftCheck, BlinkCheck, OpenCheck]
            : [RightWinks, LeftWinks, Blinks];
        var current = state.Phase == Fitting ? steps.Length : Array.IndexOf(steps, state.Phase);
        for (var index = 0; index < steps.Length; index++)
        {
            var name = steps[index] == OpenCheck ? "Open eyes" : Gesture(steps[index]).Name;
            _gestures.Children.Add(GesturePill(name, index < current ? StageState.Done
                : index == current ? StageState.Current : StageState.Todo));
        }
        _gestures.Visibility = Visibility.Visible;
    }

    private void RenderActivity(DisplayState? state)
    {
        var running = state is not null && state.Phase != Ready && !_saved;
        var problem = !Waiting.Contains(_tracking) && _tracking != FaceFound;
        string? status = null;
        var warning = false;
        if (!running)
        {
            // Before the start, say why Start calibration is not available yet.
            if (state is null && !_saved && _tracking != FaceFound) (status, warning) = (_tracking, problem);
        }
        else if (_pausedByUser) status = "Paused";
        else if (!_faceTracked) (status, warning) = (_tracking, problem);
        else if (state!.Phase == Fitting) status = null;
        else if (state.IsSettling) status = "Get ready…";
        else if (state.Phase is Baseline or OpenCheck) status = "Keep your normal reading position.";

        _status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        _statusText.Text = status ?? string.Empty;
        _statusIcon.Text = warning ? WarningGlyph : InfoGlyph;
        _statusIcon.Foreground = Ui.Brush(warning ? "WarningBrush" : "InkBrush");

        var still = running && state!.Phase is Baseline or OpenCheck or Fitting;
        _timer.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
        _timer.IsIndeterminate = state?.Phase == Fitting;
        _timer.Value = 100 * (state?.StillProgress ?? 0);

        var counting = running && IsGesture(state!.Phase);
        _dots.Visibility = _count.Visibility = counting ? Visibility.Visible : Visibility.Collapsed;
        _dots.Children.Clear();
        if (!counting) return;
        var (_, plural) = Gesture(state!.Phase);
        _count.Text = $"{state.Count} of {state.Required} {plural} {(IsCheck(state.Phase) ? "checked" : "collected")}";
        AutomationProperties.SetName(_dots, _count.Text);
        for (var index = 0; index < state.Required; index++)
        {
            var filled = index < _outcomes.Count;
            var brush = Ui.Brush(!filled ? "GoldBrush" : _outcomes[index] ? "SuccessBrush" : "WarningBrush");
            _dots.Children.Add(new Ellipse
            {
                Width = 18,
                Height = 18,
                StrokeThickness = 2,
                Stroke = brush,
                Fill = filled ? brush : null
            });
        }
    }

    private void RenderTracking()
    {
        var found = _tracking == FaceFound;
        var waiting = Waiting.Contains(_tracking);
        _trackingPill.Visibility = _saved ? Visibility.Collapsed : Visibility.Visible;
        _trackingPill.Child = Ui.Status(found ? DoneGlyph : waiting ? InfoGlyph : WarningGlyph, _tracking,
            found ? "SuccessBrush" : waiting ? "InkBrush" : "WarningBrush");
    }

    private void RenderButtons(DisplayState? state)
    {
        var running = state is not null && state.Phase != Ready && !_saved;
        _pause.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _primary.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _pauseIcon.Text = _pausedByUser ? PlayGlyph : PauseGlyph;
        _pauseLabel.Text = _pausedByUser ? "Resume" : "Pause";
        _pause.Background = _pausedByUser ? _resumeBrush : _pauseBrush;
        AutomationProperties.SetName(_pause, _pauseLabel.Text);

        var label = _saved ? "Done" : state?.Phase == Ready ? "Save calibration" : "Start calibration";
        _primary.Content = label;
        AutomationProperties.SetName(_primary, label);
        _primary.IsEnabled = _saved || state?.Phase == Ready || (_cameraReady && _faceTracked);
        _restart.Visibility = state is null || _saved ? Visibility.Collapsed : Visibility.Visible;
        _restart.IsEnabled = _cameraReady && _faceTracked;
        _cancel.Visibility = _saved ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Border GesturePill(string name, StageState state)
    {
        var done = state == StageState.Done;
        var current = state == StageState.Current;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (done)
        {
            var tick = Ui.Icon(TickGlyph, 14);
            tick.Foreground = Ui.Brush("SuccessBrush");
            content.Children.Add(tick);
        }
        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = Ui.CaptionSize,
            FontWeight = current || done ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Ui.Brush(done ? "SuccessBrush" : current ? "CardBrush" : "MuteBrush")
        });
        var pill = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 5, 12, 5),
            BorderThickness = new Thickness(1),
            Background = Ui.Brush(done ? "SuccessSoftBrush" : current ? "InkBrush" : "CardBrush"),
            BorderBrush = Ui.Brush(done ? "SuccessSoftBrush" : current ? "InkBrush" : "GoldSoftBrush"),
            Child = content
        };
        AutomationProperties.SetName(pill, $"{name}: {(done ? "done" : current ? "now" : "next")}");
        return pill;
    }

    private string IntroText()
    {
        List<string> lines =
        [
            "Use your normal reading position and lighting. Keep the tablet in the position you use for reading.",
            "Samples are collected automatically. You do not need to press anything for each wink.",
            "Extra rounds may be needed before the final checks pass.",
            "Press Pause at any time to take a break."
        ];
        if (_recalibrating) lines.Add("Your current calibration stays until you save a new one.");
        return string.Join("\n", lines.Select(line => "• " + line));
    }

    private void StartSession()
    {
        if (!_cameraReady || !_faceTracked || _cameraId is null) return;
        lock (_sessionLock)
        {
            _pausedByUser = false;
            _sessionCameraId = _cameraId;
            _session = new WinkCalibrationSession(_cameraId);
            _last = null;
            _message = null;
            _outcomes.Clear();
            _correctAtStepStart = 0;
            Render(Snapshot(_session));
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        lock (_sessionLock)
        {
            if (_session is null || _session.Phase == Ready) return;
            _pausedByUser = !_pausedByUser;
            // Drop a half-finished gesture; resuming starts with the usual short settle.
            if (_pausedByUser) _session.Pause();
            _message = _pausedByUser ? (BannerTone.Info, "Paused. Your progress is kept. Press Resume to continue.") : null;
            Render(Snapshot(_session));
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_saved) { _close(); return; }
        WinkCalibrationProfile? profile;
        lock (_sessionLock) profile = _session?.Phase == Ready ? _session.Profile : null;
        if (profile is null) { StartSession(); return; }
        try
        {
            _save(profile);
            _saved = true;
            _message = null;
            Render(_last);
            UpdateCamera();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _message = (BannerTone.Warning, "Could not save calibration. Try again.");
            Render(_last);
        }
    }
}
