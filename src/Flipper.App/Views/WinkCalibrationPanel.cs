using Flipper.App.Services;
using Flipper.Core.Reader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Flipper.App.Views;

internal sealed class WinkCalibrationPanel : UserControl
{
    private readonly Window _window;
    private readonly Action<WinkCalibrationProfile> _save;
    private readonly Action _close;
    private readonly WinkFacePreview _preview = new() { Height = 360, MaxWidth = 480 };
    private readonly TextBlock _step = Text();
    private readonly TextBlock _instruction = Text();
    private readonly TextBlock _tracking = Text();
    private readonly TextBlock _result = Text();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
    private readonly Button _primary = Button("Start calibration");
    private readonly Button _restart = Button("Restart");
    private readonly Button _cancel = Button("Cancel");
    private readonly object _sessionLock = new();
    private WinkCalibrationSession? _session;
    private string? _sessionCameraId;
    private string? _cameraId;
    private CancellationTokenSource? _cameraCts;
    private Task _cameraTask = Task.CompletedTask;
    private int _cameraEpoch;
    private int _displayQueued;
    private long _displayAt;
    private bool _loaded;
    private bool _active;
    private bool _cameraReady;
    private bool _faceTracked;
    private bool _saved;
    private bool _saveFailed;

    private sealed record DisplayState(WinkCalibrationPhase Phase, int Count, int Required, double StillProgress,
        bool IsSettling, bool NeedsMoreSamples, WinkCalibrationFailure Failure);

    public WinkCalibrationPanel(Window window, Action<WinkCalibrationProfile> save, Action close)
    {
        _window = window;
        _save = save;
        _close = close;
        _instruction.FontSize = 24;
        _instruction.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_instruction, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_result, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, "Calibration progress");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(_primary);
        actions.Children.Add(_restart);
        actions.Children.Add(_cancel);
        Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Text("Use your normal reading position and lighting. Keep the tablet in the position you use for reading."),
                _preview, _tracking, _step, _instruction, _progress, _result, actions
            }
        };
        _primary.Click += Primary_Click;
        _restart.Click += (_, _) => StartSession();
        _cancel.Click += (_, _) => _close();
        _window.Activated += Window_Activated;
        Loaded += (_, _) => { _loaded = true; UpdateCamera(); };
        Unloaded += (_, _) => Stop();
        Render(null);
    }

    public void Stop()
    {
        _loaded = false;
        _window.Activated -= Window_Activated;
        lock (_sessionLock) _session = null;
        UpdateCamera();
    }

    private static TextBlock Text(string value = "") => new()
    {
        Text = value, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["InkBrush"]
    };

    private static Button Button(string value)
    {
        var button = new Button { Content = value, MinHeight = 44, Padding = new Thickness(16, 10, 16, 10) };
        AutomationProperties.SetName(button, value);
        return button;
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
        lock (_sessionLock) _session?.Pause();
        RefreshButtons();
        await _cameraTask;
        if (epoch != _cameraEpoch) return;
        _cameraCts?.Dispose();
        _cameraCts = null;
        if (!_loaded || _saved || !_active)
        {
            if (_loaded && !_saved) _tracking.Text = "Camera paused";
            return;
        }
        _tracking.Text = "Starting camera…";
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
                _tracking.Text = _cameraReady ? "Finding your face…" : status;
                RefreshButtons();
            }), token, frame => OnFrame(frame, epoch, token));
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (epoch != _cameraEpoch) return;
                _cameraReady = false;
                RefreshButtons();
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
            session?.Observe(new(frame.Timestamp.TotalMilliseconds, frame.Eyes.LeftOpenProbability,
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
                _tracking.Text = frame.Eyes.TrackingStatus switch
                {
                    "tracked" => "Face found",
                    "multiple_faces" => "Keep only your face in the camera view.",
                    "eyes_too_small" => "Move closer to the camera.",
                    "eye_crop_outside_frame" => "Keep your whole face in the camera view.",
                    _ => "Finding your face…"
                };
                Render(display);
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
            session.IsSettling, session.NeedsMoreSamples, session.LastFailure);

    private void Render(DisplayState? state)
    {
        if (_saved) return;
        if (state is null)
        {
            _step.Text = string.Empty;
            _instruction.Text = "Start when you are ready.";
            _result.Text = "Repeat each gesture when prompted. Samples are collected automatically.";
            _progress.Value = 0;
            _progress.IsIndeterminate = false;
            _primary.Content = "Start calibration";
            _primary.Visibility = Visibility.Visible;
            _restart.Visibility = Visibility.Collapsed;
            RefreshButtons();
            return;
        }
        var phase = state.Phase;
        var checking = phase is WinkCalibrationPhase.RightCheck or WinkCalibrationPhase.LeftCheck
            or WinkCalibrationPhase.BlinkCheck or WinkCalibrationPhase.OpenCheck;
        _step.Text = checking ? "Checking your calibration" : "Collecting your calibration";
        _instruction.Text = phase switch
        {
            WinkCalibrationPhase.Baseline or WinkCalibrationPhase.OpenCheck => "Keep both eyes comfortably open.",
            WinkCalibrationPhase.RightWinks or WinkCalibrationPhase.RightCheck => "Wink your right eye repeatedly.",
            WinkCalibrationPhase.LeftWinks or WinkCalibrationPhase.LeftCheck => "Wink your left eye repeatedly.",
            WinkCalibrationPhase.Blinks or WinkCalibrationPhase.BlinkCheck => "Blink both eyes repeatedly.",
            WinkCalibrationPhase.Fitting => "Checking your samples…",
            _ => "Your calibration is ready to save."
        };
        _progress.IsIndeterminate = phase == WinkCalibrationPhase.Fitting;
        _progress.Value = state.Required > 0 ? 100d * state.Count / state.Required : 100 * state.StillProgress;
        _result.Text = phase switch
        {
            WinkCalibrationPhase.Ready => _saveFailed ? "Could not save calibration. Try again." : "All checks passed.",
            WinkCalibrationPhase.Fitting => string.Empty,
            _ when state.IsSettling => "Get ready…",
            WinkCalibrationPhase.Baseline or WinkCalibrationPhase.OpenCheck => "Keep your normal reading position.",
            _ => $"{state.Count} samples collected. Relax both eyes between gestures."
        };
        if (state.NeedsMoreSamples && phase is WinkCalibrationPhase.RightWinks or WinkCalibrationPhase.LeftWinks or WinkCalibrationPhase.Blinks)
        {
            _step.Text = state.Failure == WinkCalibrationFailure.InsufficientEyeRange
                ? "Open and closed samples are too similar. More samples are needed."
                : "More samples are needed to separate winks from blinks.";
        }
        _primary.Content = "Save calibration";
        _primary.Visibility = phase == WinkCalibrationPhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        _restart.Visibility = Visibility.Visible;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool ready;
        lock (_sessionLock) ready = _session?.Phase == WinkCalibrationPhase.Ready;
        _primary.IsEnabled = _saved || ready || (_cameraReady && _faceTracked);
        _restart.IsEnabled = _cameraReady && _faceTracked;
        AutomationProperties.SetName(_primary, _primary.Content.ToString());
    }

    private void StartSession()
    {
        if (!_cameraReady || !_faceTracked || _cameraId is null) return;
        _saveFailed = false;
        lock (_sessionLock)
        {
            _sessionCameraId = _cameraId;
            _session = new WinkCalibrationSession(_cameraId);
            Render(Snapshot(_session));
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_saved) { _close(); return; }
        WinkCalibrationProfile? profile;
        lock (_sessionLock) profile = _session?.Phase == WinkCalibrationPhase.Ready ? _session.Profile : null;
        if (profile is null) { StartSession(); return; }
        try
        {
            _save(profile);
            _saved = true;
            _instruction.Text = "Calibration saved";
            _result.Text = string.Empty;
            _step.Text = string.Empty;
            _progress.Value = 100;
            _primary.Content = "Done";
            _restart.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            UpdateCamera();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _saveFailed = true;
            _result.Text = "Could not save calibration. Try again.";
        }
    }
}
