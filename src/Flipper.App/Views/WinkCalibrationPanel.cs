using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Flipper.App.Services;
using Flipper.Core.Reader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Flipper.App.Views;

internal sealed class WinkCalibrationPanel : UserControl
{
    private static readonly WinkCalibrationGesture[] Training =
    [
        WinkCalibrationGesture.Neither,
        WinkCalibrationGesture.RightWink, WinkCalibrationGesture.LeftWink, WinkCalibrationGesture.Blink,
        WinkCalibrationGesture.RightWink, WinkCalibrationGesture.LeftWink, WinkCalibrationGesture.Blink
    ];
    private static readonly WinkCalibrationGesture[] Validation =
    [
        WinkCalibrationGesture.RightWink, WinkCalibrationGesture.LeftWink, WinkCalibrationGesture.Blink, WinkCalibrationGesture.Neither,
        WinkCalibrationGesture.LeftWink, WinkCalibrationGesture.Blink, WinkCalibrationGesture.RightWink, WinkCalibrationGesture.Neither
    ];

    private readonly Action<WinkCalibrationProfile> _save;
    private readonly Action _close;
    private readonly TextBlock _step = Text();
    private readonly TextBlock _instruction = Text();
    private readonly TextBlock _tracking = Text();
    private readonly TextBlock _result = Text();
    private readonly Image _preview = new() { Height = 200, Stretch = Stretch.Uniform };
    private readonly Button _record = Button("Record attempt");
    private readonly Button _use = Button("Use attempt");
    private readonly Button _retry = Button("Try again");
    private readonly Button _cancel = Button("Cancel");
    private readonly object _captureLock = new();
    private readonly List<WinkCalibrationTrial> _training = [];
    private Capture? _capture;
    private WinkCalibrationTrial? _pending;
    private WinkCalibrationProfile? _candidate;
    private CancellationTokenSource? _cameraCts;
    private Task _cameraTask = Task.CompletedTask;
    private WriteableBitmap? _bitmap;
    private string? _cameraId;
    private int _cameraEpoch;
    private int _previewQueued;
    private int _index;
    private long _previewAt;
    private bool _loaded;
    private bool _windowActive;
    private bool _busy;
    private bool _cameraReady;
    private bool _faceTracked;
    private bool _validating;
    private bool _failed;
    private bool _readyToSave;
    private bool _saved;

    public WinkCalibrationPanel(Action<WinkCalibrationProfile> save, Action close)
    {
        _save = save;
        _close = close;
        _instruction.FontSize = 18;
        _instruction.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_instruction, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_result, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _preview.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _preview.RenderTransform = new ScaleTransform { ScaleX = -1 };
        AutomationProperties.SetName(_preview, "Camera preview");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_record);
        actions.Children.Add(_use);
        actions.Children.Add(_retry);
        actions.Children.Add(_cancel);
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Text("Use your normal reading position and lighting. Keep the tablet in the position you use for reading."),
                _preview, _tracking, _step, _instruction, _result, actions
            }
        };
        _record.Click += Record_Click;
        _use.Click += Use_Click;
        _retry.Click += (_, _) => { _pending = null; ShowStep(); };
        _cancel.Click += (_, _) => _close();
        Loaded += (_, _) =>
        {
            _loaded = true;
            _windowActive = App.Current.Window?.IsReaderActive == true;
            if (App.Current.Window is { } window) window.Activated += Window_Activated;
            UpdateCamera();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            if (App.Current.Window is { } window) window.Activated -= Window_Activated;
            UpdateCamera();
        };
        ShowStep();
    }

    private static TextBlock Text(string value = "") => new()
    {
        Text = value,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["InkBrush"]
    };

    private static Button Button(string label)
    {
        var button = new Button { Content = label };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private WinkCalibrationGesture CurrentGesture => (_validating ? Validation : Training)[_index];
    private bool IsBaseline => !_validating && _index == 0;
    private static string GestureInstruction(WinkCalibrationGesture gesture) => gesture switch
    {
        WinkCalibrationGesture.RightWink => "Wink your right eye once, then reopen it.",
        WinkCalibrationGesture.LeftWink => "Wink your left eye once, then reopen it.",
        WinkCalibrationGesture.Blink => "Blink both eyes once, then reopen them.",
        _ => "Keep both eyes comfortably open."
    };

    private void ShowStep()
    {
        _result.Text = string.Empty;
        _step.Text = $"{(_validating ? "Check" : "Calibration")} {_index + 1} of {(_validating ? Validation.Length : Training.Length)}";
        _instruction.Text = GestureInstruction(CurrentGesture);
        _record.Content = "Record attempt";
        _use.Visibility = Visibility.Collapsed;
        _retry.Visibility = Visibility.Collapsed;
        _record.Visibility = Visibility.Visible;
        RefreshRecordButton();
    }

    private void RefreshRecordButton()
    {
        AutomationProperties.SetName(_record, _record.Content.ToString());
        _record.IsEnabled = !_busy && (_failed || _readyToSave || _saved || (_cameraReady && _faceTracked));
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (active == _windowActive) return;
        _windowActive = active;
        UpdateCamera();
    }

    private async void UpdateCamera()
    {
        var epoch = ++_cameraEpoch;
        _cameraReady = false;
        _faceTracked = false;
        _cameraCts?.Cancel();
        CancelCapture();
        RefreshRecordButton();
        await _cameraTask;
        if (epoch != _cameraEpoch) return;
        _cameraCts?.Dispose();
        _cameraCts = null;
        if (!_loaded || _saved || !_windowActive) return;
        _tracking.Text = "Starting camera…";
        _cameraCts = new CancellationTokenSource();
        var token = _cameraCts.Token;
        _cameraTask = RunCameraAsync(epoch, token);
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
                RefreshRecordButton();
            }), token, frame => OnFrame(frame, epoch, token));
        }
        finally
        {
            CancelCapture();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (epoch != _cameraEpoch) return;
                _cameraReady = false;
                RefreshRecordButton();
            });
        }
    }

    private void OnFrame(WinkCameraFrame frame, int epoch, CancellationToken token)
    {
        var tracked = frame.Eyes.TrackingStatus == "tracked";
        lock (_captureLock)
        {
            if (_capture is { } capture)
            {
                capture.Start ??= frame.Timestamp;
                var elapsed = (frame.Timestamp - capture.Start.Value).TotalMilliseconds;
                capture.Samples.Add(new(elapsed, frame.Eyes.LeftOpenProbability, frame.Eyes.RightOpenProbability,
                    tracked, frame.Eyes.RotationQuarterTurns));
                capture.Started.TrySetResult();
                if (elapsed >= capture.DurationMs)
                {
                    _capture = null;
                    capture.Completed.TrySetResult(capture.Samples.ToArray());
                }
            }
        }

        var now = Environment.TickCount64;
        if (now - _previewAt < 100 || Interlocked.Exchange(ref _previewQueued, 1) != 0) return;
        _previewAt = now;
        var (width, height) = WinkFrameRotation.GetSize(frame.Width, frame.Height, frame.Eyes.RotationQuarterTurns);
        var scale = Math.Min(320d / width, 200d / height);
        var previewWidth = Math.Max(1, (int)(width * scale));
        var previewHeight = Math.Max(1, (int)(height * scale));
        var pixels = new byte[previewWidth * previewHeight * 4];
        for (var y = 0; y < previewHeight; y++)
        for (var x = 0; x < previewWidth; x++)
        {
            var source = WinkFrameRotation.ToSource(new Vector2((float)(x / scale), (float)(y / scale)),
                frame.Width, frame.Height, frame.Eyes.RotationQuarterTurns);
            var from = ((int)source.Y * frame.Width + (int)source.X) * 4;
            var to = (y * previewWidth + x) * 4;
            frame.Pixels.AsSpan(from, 4).CopyTo(pixels.AsSpan(to, 4));
        }
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (epoch != _cameraEpoch || token.IsCancellationRequested || !_loaded) return;
                if (_cameraId is not null && _cameraId != frame.CameraId)
                {
                    Fail("The camera changed. Restart calibration for this camera.");
                }
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
                if (_bitmap?.PixelWidth != previewWidth || _bitmap.PixelHeight != previewHeight)
                {
                    _bitmap = new WriteableBitmap(previewWidth, previewHeight);
                    _preview.Source = _bitmap;
                }
                using var stream = _bitmap.PixelBuffer.AsStream();
                stream.Write(pixels);
                _bitmap.Invalidate();
                RefreshRecordButton();
            }
            finally { Interlocked.Exchange(ref _previewQueued, 0); }
        })) Interlocked.Exchange(ref _previewQueued, 0);
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_saved) { _close(); return; }
        if (_failed)
        {
            _failed = false;
            _validating = false;
            _readyToSave = false;
            _candidate = null;
            _pending = null;
            _training.Clear();
            _index = 0;
            ShowStep();
            return;
        }
        if (_readyToSave)
        {
            try
            {
                _save(_candidate!);
                _saved = true;
                _instruction.Text = "Calibration saved";
                _result.Text = string.Empty;
                _record.Content = "Done";
                _cancel.Visibility = Visibility.Collapsed;
                UpdateCamera();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _result.Text = "Could not save calibration. Try again.";
            }
            return;
        }
        if (_cameraCts is not { IsCancellationRequested: false } cameraCts) return;
        var token = cameraCts.Token;
        var capture = new Capture(IsBaseline ? 3000 : 4000);
        _busy = true;
        _result.Text = string.Empty;
        RefreshRecordButton();
        lock (_captureLock) _capture = capture;
        using var cancellation = token.Register(() => CancelCapture(capture));
        try
        {
            _instruction.Text = "Keep both eyes comfortably open…";
            await capture.Started.Task;
            if (!IsBaseline) await Task.Delay(1000, token);
            _instruction.Text = GestureInstruction(CurrentGesture);
            var samples = await capture.Completed.Task;
            token.ThrowIfCancellationRequested();
            _pending = new(CurrentGesture, samples, IsBaseline ? 0 : 1000);
            _instruction.Text = IsBaseline || CurrentGesture == WinkCalibrationGesture.Neither
                ? "Did you keep your eyes comfortably open?" : "Did you make the requested gesture once?";
            _record.Visibility = Visibility.Collapsed;
            _use.Visibility = Visibility.Visible;
            _retry.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            if (_loaded) ShowStep();
        }
        finally
        {
            CancelCapture(capture);
            _busy = false;
            RefreshRecordButton();
        }
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is not { } trial || _busy) return;
        _pending = null;
        if (_validating)
        {
            if (!WinkCalibration.Matches(_candidate!, trial))
            {
                Fail("Calibration did not pass this check. Restart calibration to try again.");
                return;
            }
            if (++_index < Validation.Length) { ShowStep(); return; }
            _readyToSave = true;
            _step.Text = "Checks complete";
            _instruction.Text = "Your calibration is ready to save.";
            _record.Content = "Save calibration";
            _record.Visibility = Visibility.Visible;
            _use.Visibility = Visibility.Collapsed;
            _retry.Visibility = Visibility.Collapsed;
            RefreshRecordButton();
            return;
        }

        _training.Add(trial);
        if (++_index < Training.Length) { ShowStep(); return; }
        _busy = true;
        _use.Visibility = Visibility.Collapsed;
        _retry.Visibility = Visibility.Collapsed;
        _instruction.Text = "Checking your samples…";
        var epoch = _cameraEpoch;
        var result = await Task.Run(() => WinkCalibration.Fit(_cameraId!, _training[0].Samples, _training.ToArray()));
        _busy = false;
        if (!_loaded || epoch != _cameraEpoch)
        {
            _index = Training.Length - 1;
            if (_loaded) Fail("Calibration was interrupted. Restart to try again.");
            return;
        }
        if (result.Profile is null)
        {
            Fail(result.Failure switch
            {
                WinkCalibrationFailure.UnstableTracking => "Your face was not tracked steadily. Check your position and lighting, then restart.",
                WinkCalibrationFailure.InsufficientEyeRange => "The open and closed eye samples were too similar. Check your position and lighting, then restart.",
                WinkCalibrationFailure.InsufficientSamples => "There were not enough clear samples. Restart calibration to try again.",
                _ => "The samples did not separate winks from blinks. Restart calibration to try again."
            });
            return;
        }
        _candidate = result.Profile;
        _validating = true;
        _index = 0;
        ShowStep();
    }

    private void Fail(string message)
    {
        CancelCapture();
        _pending = null;
        _candidate = null;
        _failed = true;
        _readyToSave = false;
        _instruction.Text = message;
        _record.Content = "Restart calibration";
        _record.Visibility = Visibility.Visible;
        _use.Visibility = Visibility.Collapsed;
        _retry.Visibility = Visibility.Collapsed;
        RefreshRecordButton();
    }

    private void CancelCapture(Capture? expected = null)
    {
        lock (_captureLock)
        {
            if (_capture is not { } current || (expected is not null && expected != current)) return;
            _capture = null;
            current.Started.TrySetCanceled();
            current.Completed.TrySetCanceled();
        }
    }

    private sealed class Capture(double durationMs)
    {
        public double DurationMs { get; } = durationMs;
        public TimeSpan? Start { get; set; }
        public List<WinkCalibrationSample> Samples { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<WinkCalibrationSample>> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
