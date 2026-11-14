using Flipper.App.Services;
using Flipper.Core.Reader;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Flipper.App.Views;

internal sealed class VoiceCalibrationPanel : UserControl
{
    private readonly Window _window;
    private readonly string? _selectedDevice;
    private readonly Action<VoiceCalibrationProfile> _save;
    private readonly VoiceAudioCapture _capture = new();
    private readonly TextBlock _microphone = Text();
    private readonly TextBlock _stage = Text();
    private readonly TextBlock _instruction = Text("Start when you are ready.");
    private readonly TextBlock _result = Text();
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
    private readonly ProgressBar _level = new() { Minimum = 0, Maximum = 100 };
    private readonly Button _primary = Button("Start calibration");
    private readonly Button _restart = Button("Restart");
    private readonly StackPanel _wordChoices = new() { Spacing = 8 };
    private readonly string[] _words = ["next", "back", "restart", "finish"];
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly object _recordingLock = new();
    private readonly List<(string? Word, float[] Audio)> _samples = [];
    private List<float>? _recording;
    private float[] _quiet = [];
    private CancellationTokenSource? _operation;
    private VoiceCalibrationProfile? _candidate;
    private string? _deviceId;
    private int _step;
    private bool _checking;
    private bool _readyToSave;
    private bool _stopped;
    private bool _active;
    private bool _busy;

    public VoiceCalibrationPanel(Window window, string? selectedDevice, Action<VoiceCalibrationProfile> save, Action close)
    {
        _window = window;
        _selectedDevice = selectedDevice;
        _save = save;
        _instruction.FontSize = 24;
        _instruction.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_instruction, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, "Recording progress");
        AutomationProperties.SetName(_level, "Microphone level");
        AddWordChoice("Next page", 0, ["flip", "turn", "next", "page"]);
        AddWordChoice("Previous page", 1, ["back", "previous"]);
        AddWordChoice("First page", 2, ["restart", "beginning"]);
        _wordChoices.Children.Add(Text("Back to library: finish"));
        var cancel = Button("Cancel");
        cancel.Click += (_, _) => close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(_primary);
        actions.Children.Add(_restart);
        actions.Children.Add(cancel);
        Content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                Text("Use your normal reading position and speaking voice. Keep the microphone where you use it when playing."),
                _wordChoices, _microphone, _level, _stage, _instruction, _progress, _result, actions
            }
        };
        _primary.Click += Primary_Click;
        _restart.Click += (_, _) => Reset();
        _restart.Visibility = Visibility.Collapsed;
        _meter.Tick += (_, _) =>
        {
            _level.Value = Math.Clamp(_capture.LastRms * 500, 0, 100);
            if (_capture.Failure is { } failure) { Reset(); _result.Text = failure; }
        };
        _window.Activated += Window_Activated;
        Unloaded += (_, _) => Stop();
        _stage.Text = "Choose your command words. Collect two samples of each, then check new samples.";
    }

    private void AddWordChoice(string action, int index, string[] options)
    {
        var picker = new ComboBox { ItemsSource = options, SelectedItem = _words[index], HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(picker, action + " command");
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is string word) _words[index] = word; };
        _wordChoices.Children.Add(Text(action));
        _wordChoices.Children.Add(picker);
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
        _active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_active && !_readyToSave && (_deviceId is not null || _busy))
        {
            Reset();
            _result.Text = "Calibration paused. Start again when you are ready.";
        }
    }

    public void Stop()
    {
        _stopped = true;
        _window.Activated -= Window_Activated;
        Reset();
    }

    private void Reset()
    {
        _operation?.Cancel();
        _operation = null;
        _meter.Stop();
        _capture.Stop();
        lock (_recordingLock) _recording = null;
        _samples.Clear();
        _quiet = [];
        _candidate = null;
        _deviceId = null;
        _checking = _readyToSave = _busy = false;
        _step = 0;
        _progress.IsIndeterminate = false;
        _progress.Value = _level.Value = 0;
        _microphone.Text = _result.Text = string.Empty;
        _wordChoices.Visibility = Visibility.Visible;
        _stage.Text = "Choose your command words. Collect two samples of each, then check new samples.";
        _instruction.Text = "Start when you are ready.";
        SetPrimary("Start calibration");
        _primary.IsEnabled = !_stopped;
        _restart.Visibility = Visibility.Collapsed;
    }

    private void SetPrimary(string text)
    {
        _primary.Content = text;
        AutomationProperties.SetName(_primary, text);
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _stopped || !_active) return;
        if (_readyToSave && _candidate is not null)
        {
            try { _save(_candidate); _window.Close(); }
            catch { _result.Text = "Could not save calibration. Try saving again."; }
            return;
        }
        using var cts = new CancellationTokenSource();
        _operation = cts;
        _busy = true;
        _primary.IsEnabled = false;
        _restart.Visibility = Visibility.Visible;
        try
        {
            if (_deviceId is null)
            {
                _result.Text = "Starting microphone…";
                var failure = await _capture.StartAsync(_selectedDevice, OnSamples, requireSelectedDevice: true);
                cts.Token.ThrowIfCancellationRequested();
                if (failure is not null) { _result.Text = failure; return; }
                _deviceId = _capture.DeviceId;
                _microphone.Text = _capture.DeviceName;
                _wordChoices.Visibility = Visibility.Collapsed;
                _meter.Start();
                ShowPrompt();
                return;
            }
            var word = _step < 2 ? null : _words[(_step - 2) % _words.Length];
            var seconds = _step == 0 ? 5 : _step == 1 ? 8 : 3;
            _result.Text = "Get ready…";
            await Task.Delay(1000, cts.Token);
            lock (_recordingLock) _recording = new List<float>(seconds * VoiceCalibration.SampleRate);
            _result.Text = "Recording…";
            for (var tick = 0; tick < seconds * 10; tick++)
            {
                await Task.Delay(100, cts.Token);
                _progress.Value = (tick + 1) * 100.0 / (seconds * 10);
            }
            float[] audio;
            lock (_recordingLock) { audio = _recording!.ToArray(); _recording = null; }
            if (audio.Length < seconds * VoiceCalibration.SampleRate * .8)
            {
                _result.Text = "Too little microphone audio was received. Record this sample again.";
                return;
            }
            if (word is not null && !VoiceCalibration.HasUsableSpeech(audio, _quiet))
            {
                _result.Text = "The command was too quiet, too short or distorted. Adjust your microphone position and record it again.";
                return;
            }
            if (_step == 0) _quiet = audio;
            if (_checking)
            {
                var candidate = _candidate!;
                _result.Text = "Checking sample…";
                var heard = await Task.Run(() =>
                {
                    using var recognizer = new VoiceRecognizer(candidate.PresetIndex);
                    return recognizer.Replay(audio, candidate.Gain, cts.Token);
                }, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                if (!VoiceCalibration.MatchesPrompt(word, heard))
                {
                    Reset();
                    _result.Text = heard.Count == 0 ? "The command was not detected. Start again to collect new samples."
                        : $"The check detected: {string.Join(", ", heard).ToLowerInvariant()}. Start again to collect new samples.";
                    return;
                }
            }
            else _samples.Add((word, audio));
            _step++;
            var required = 2 + _words.Length * (_checking ? 1 : 2);
            if (_step == required)
            {
                if (_checking)
                {
                    _readyToSave = true;
                    _capture.Stop();
                    _meter.Stop();
                    _quiet = [];
                    _stage.Text = "Checks passed";
                    _instruction.Text = "Your voice calibration is ready to save.";
                    _result.Text = string.Empty;
                    SetPrimary("Save calibration");
                }
                else await FitAsync(cts.Token);
            }
            else ShowPrompt();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_stopped && !cts.IsCancellationRequested)
            {
                Reset();
                _result.Text = "Calibration could not complete: " + ex.GetBaseException().Message;
            }
        }
        finally
        {
            if (_operation == cts)
            {
                _operation = null;
                _busy = false;
                _primary.IsEnabled = !_stopped;
                _progress.IsIndeterminate = false;
            }
        }
    }

    private void OnSamples(float[] audio)
    {
        lock (_recordingLock)
        {
            // Bound temporary audio even if the UI is stalled.
            if (_recording is { Count: < VoiceCalibration.SampleRate * 10 }) _recording.AddRange(audio);
        }
    }

    private void ShowPrompt()
    {
        var required = 2 + _words.Length * (_checking ? 1 : 2);
        _stage.Text = $"{(_checking ? "Check" : "Collection")} · sample {_step + 1} of {required}"
            + (!_checking && _step >= 2 ? $" · round {(_step - 2) / _words.Length + 1} of 2" : "");
        _instruction.Text = _step switch
        {
            0 => "Keep quiet while the microphone measures the room.",
            1 => "Play a short passage or make your usual background sound. Do not say a command.",
            _ => $"Say “{_words[(_step - 2) % _words.Length]}” once when recording starts."
        };
        _result.Text = string.Empty;
        _progress.Value = 0;
        SetPrimary("Record sample");
    }

    private async Task FitAsync(CancellationToken token)
    {
        _stage.Text = "Finding voice settings";
        _instruction.Text = "Checking your commands and background sound…";
        _progress.IsIndeterminate = true;
        var samples = _samples.ToArray();
        var deviceId = _deviceId!;
        var gain = VoiceCalibration.MeasureGain(samples.Where(s => s.Word is not null).Select(s => s.Audio).ToArray());
        var fitted = await Task.Run(() =>
        {
            for (var preset = 0; preset < VoiceCalibration.PresetCount; preset++)
            {
                token.ThrowIfCancellationRequested();
                using var recognizer = new VoiceRecognizer(preset);
                foreach (var candidateGain in new[] { 1f, gain }.Distinct())
                {
                    var allPassed = samples.All(sample => VoiceCalibration.MatchesPrompt(sample.Word,
                        recognizer.Replay(sample.Audio, candidateGain, token)));
                    if (allPassed) return new VoiceCalibrationProfile
                    {
                        MicrophoneDeviceId = deviceId, CreatedUtc = DateTimeOffset.UtcNow,
                        PresetIndex = preset, Gain = candidateGain
                    };
                }
            }
            return null;
        }, token);
        token.ThrowIfCancellationRequested();
        _samples.Clear();
        _quiet = [];
        _progress.IsIndeterminate = false;
        if (fitted is null)
        {
            Reset();
            _result.Text = "No setting detected every chosen command without extra commands. Try another command word or microphone position, then start again.";
            return;
        }
        _candidate = fitted;
        _checking = true;
        _step = 0;
        ShowPrompt();
    }
}
