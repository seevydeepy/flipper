using Flipper.App.Services;
using Flipper.Core.Reader;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Flipper.App.Views;

internal sealed class VoiceCalibrationPanel : UserControl
{
    private const int CountdownSeconds = 3;
    private const int WordStage = 2;
    private const int TestStage = 3;
    private const string MicrophoneGlyph = "\uE720";
    private const string QuietGlyph = "\uE74F";
    private const string MusicGlyph = "\uE8D6";
    private const string DoneGlyph = "\uE930";
    private const string WarningGlyph = "\uE7BA";
    private const string InfoGlyph = "\uE946";
    private const string RecordGlyph = "\uE7C8";

    private enum Sound { Quiet, Playing, Word }
    private enum Phase { Intro, Ready, Countdown, Recording, Checking, Passed, Saved }
    private enum Tone { Success, Warning, Info }

    private sealed record Step(int Stage, Sound Sound, int Number, int Count, int Seconds)
    {
        public bool Test => Stage == TestStage;
    }

    private sealed record Outcome(bool Passed, string? Error, bool RetryStep,
        VoiceSoundProfile? Profile = null, VoiceSoundTemplate? Template = null);

    private static readonly string[] StageNames = ["Quiet room", "Playing", "Your word", "Test"];
    private static readonly Step[] Steps = CreateSteps();
    private static readonly int FirstWordStep = Array.FindIndex(Steps, step => step.Stage == WordStage);

    private readonly Window _window;
    private readonly string? _selectedDevice;
    private readonly VoiceSoundProfile _draft;
    private readonly string _action;
    private readonly bool _recalibrating;
    private readonly IReadOnlyList<VoiceSoundProfile> _existing;
    private readonly Action<VoiceSoundProfile> _save;
    private readonly Action _close;
    private readonly VoiceAudioCapture _capture = new();
    private readonly VoiceCalibrationStages _stages = new(StageNames);
    private readonly TextBlock _stepCount = Ui.Label(string.Empty);
    private readonly ProgressBar _overall = new() { Minimum = 0, Maximum = 100, MinHeight = 8 };
    private readonly StackPanel _progressRow;
    private readonly Border _iconBubble;
    private readonly TextBlock _icon = Ui.Icon(MicrophoneGlyph, 30);
    private readonly TextBlock _instruction = Ui.Body();
    private readonly TextBlock _detail = Ui.Body();
    private readonly StackPanel _activity;
    private readonly TextBlock _activityIcon = Ui.Icon(RecordGlyph, 16);
    private readonly TextBlock _activityText = Ui.Label(string.Empty);
    private readonly ProgressBar _timer = new() { Minimum = 0, Maximum = 100, MinHeight = 8 };
    private readonly StackPanel _microphone;
    private readonly TextBlock _microphoneName = Ui.Caption();
    private readonly ProgressBar _level = new() { Minimum = 0, Maximum = 100, MinHeight = 8 };
    private readonly Border _feedback;
    private readonly TextBlock _feedbackIcon = Ui.Icon(InfoGlyph, 20);
    private readonly TextBlock _feedbackText = Ui.Body();
    private readonly Button _primary = Ui.Button("Start calibration", primary: true);
    private readonly Button _restart = Ui.Button("Restart");
    private readonly Button _cancel = Ui.Button("Cancel");
    private readonly Border _footer;
    private readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly object _recordingLock = new();
    private readonly List<float[]> _samples = [];
    private List<float>? _recording;
    private float[] _quiet = [];
    private float[] _background = [];
    private VoiceSoundProfile? _candidate;
    private CancellationTokenSource? _operation;
    private string? _deviceId;
    private (Tone Tone, string Text)? _message;
    private Phase _phase = Phase.Intro;
    private string? _shownMessage;
    private string? _shownInstruction;
    private int _index;
    private int _countdown;
    private bool _retry;
    private bool _microphoneReady;
    private bool _active;
    private bool _stopped;

    public VoiceCalibrationPanel(Window window, string? selectedDevice, VoiceSoundProfile draft, string action,
        IReadOnlyList<VoiceSoundProfile> existing, Action<VoiceSoundProfile> save, Action close)
    {
        _window = window;
        _selectedDevice = selectedDevice;
        _draft = draft;
        _action = action;
        _existing = existing.ToArray();
        _recalibrating = _existing.Any(profile => profile.Id == draft.Id);
        _save = save;
        _close = close;

        _instruction.FontSize = 24;
        _instruction.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_instruction, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_feedbackText, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_timer, "Recording time");
        AutomationProperties.SetName(_level, "Microphone level");

        var header = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Ui.Title($"Calibrate “{draft.Label}”", level: AutomationHeadingLevel.Level1),
                Ui.Caption($"Action: {action}")
            }
        };
        _progressRow = new StackPanel { Spacing = 8, Children = { _stepCount, _overall } };

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
        _activityIcon.Foreground = Ui.Brush("RecordBrush");
        _activity = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _activityIcon, _activityText } },
                _timer
            }
        };
        _microphone = new StackPanel { Spacing = 8, Children = { Ui.Rule(), _microphoneName, _level } };
        var card = Ui.Card(new StackPanel { Spacing = 16, Children = { prompt, _activity, _microphone } });

        _feedbackIcon.VerticalAlignment = VerticalAlignment.Top;
        _feedbackIcon.Margin = new Thickness(0, 1, 0, 0);
        Grid.SetColumn(_feedbackText, 1);
        _feedback = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children = { _feedbackIcon, _feedbackText }
            }
        };

        Content = new StackPanel { Spacing = 20, Children = { header, _stages, _progressRow, card, _feedback } };

        _primary.MinHeight = _restart.MinHeight = _cancel.MinHeight = 64;
        _primary.FontSize = 20;
        _primary.HorizontalAlignment = HorizontalAlignment.Stretch;
        var others = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _restart, _cancel } };
        _footer = new Border { Padding = new Thickness(24, 12, 24, 24), MaxWidth = 760, Child = Ui.Split(_primary, others, 520) };

        _primary.Click += Primary_Click;
        _restart.Click += Restart_Click;
        _cancel.Click += (_, _) => _close();
        _meter.Tick += Meter_Tick;
        _window.Activated += Window_Activated;
        Unloaded += (_, _) => Stop();
        Render();
    }

    /// <summary>The buttons. The window keeps them below the scrolled content, so they are always visible.</summary>
    public FrameworkElement Footer => _footer;

    public void Stop()
    {
        _stopped = true;
        _window.Activated -= Window_Activated;
        CancelOperation();
        StopMicrophone();
    }

    private static Step[] CreateSteps()
    {
        List<Step> steps = [new(0, Sound.Quiet, 1, 1, 5), new(1, Sound.Playing, 1, 1, 8)];
        var words = VoiceSoundProfile.TrainingRecordingCount;
        for (var number = 1; number <= words; number++)
        {
            steps.Add(new(WordStage, Sound.Word, number, words, 4));
        }
        var tests = 2 + VoiceSoundProfile.VerificationRecordingCount;
        steps.Add(new(TestStage, Sound.Quiet, 1, tests, 5));
        steps.Add(new(TestStage, Sound.Playing, 2, tests, 8));
        for (var number = 3; number <= tests; number++)
        {
            steps.Add(new(TestStage, Sound.Word, number, tests, 4));
        }
        return [.. steps];
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        var active = args.WindowActivationState != WindowActivationState.Deactivated;
        if (active == _active || _stopped) return;
        _active = active;
        if (_phase is Phase.Passed or Phase.Saved) return;
        if (!active) Pause();
        else if (_phase != Phase.Intro && _operation is null && !_microphoneReady) _ = RunAsync(ResumeAsync);
    }

    /// <summary>Stops the microphone while the window is in the background. Finished steps are kept.</summary>
    private void Pause()
    {
        var interrupted = _phase is Phase.Countdown or Phase.Recording or Phase.Checking;
        var starting = _phase == Phase.Intro && _operation is not null;
        CancelOperation();
        StopMicrophone();
        if (interrupted)
        {
            _phase = Phase.Ready;
            _retry = true;
            Show(Tone.Info, "Recording stopped because the window was not active. Your other steps are kept.");
        }
        else if (starting)
        {
            _message = null;
        }
        Render();
    }

    private void Meter_Tick(object? sender, object e)
    {
        _level.Value = Math.Clamp(_capture.LastRms * 500, 0, 100);
        if (_capture.Failure is not { } failure) return;
        var interrupted = _phase is Phase.Countdown or Phase.Recording or Phase.Checking;
        CancelOperation();
        StopMicrophone();
        if (interrupted)
        {
            _phase = Phase.Ready;
            _retry = true;
        }
        Show(Tone.Warning, failure);
        Render();
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _stopped || !_active) return;
        switch (_phase)
        {
            case Phase.Saved:
                _close();
                break;
            case Phase.Passed:
                Save();
                break;
            case Phase.Intro:
                await RunAsync(StartAsync);
                break;
            default:
                await RunAsync(RecordAsync);
                break;
        }
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        CancelOperation();
        ClearProgress();
        if (!_microphoneReady) _deviceId = null;
        _phase = Phase.Ready;
        Show(Tone.Info, "Calibration restarted from step 1.");
        Render();
        FocusPrimary();
        if (!_microphoneReady && _active) _ = RunAsync(ResumeAsync);
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        Render();
        try
        {
            await work(cts.Token);
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && !_stopped)
            {
                lock (_recordingLock) _recording = null;
                if (_phase != Phase.Intro) _phase = Phase.Ready;
                _retry = _phase == Phase.Ready;
                Show(Tone.Warning, "Calibration could not complete: " + ex.GetBaseException().Message);
            }
        }
        finally
        {
            if (_operation == cts)
            {
                _operation = null;
                if (!_stopped)
                {
                    Render();
                    FocusPrimary();
                }
            }
        }
    }

    private async Task StartAsync(CancellationToken token)
    {
        Show(Tone.Info, "Starting microphone…");
        Render();
        if (await StartMicrophoneAsync(token) is { } failure)
        {
            Show(Tone.Warning, failure);
            return;
        }
        ClearProgress();
        _phase = Phase.Ready;
        _message = null;
    }

    private async Task ResumeAsync(CancellationToken token)
    {
        if (await StartMicrophoneAsync(token) is { } failure) Show(Tone.Warning, failure);
    }

    private async Task<string?> StartMicrophoneAsync(CancellationToken token)
    {
        if (_microphoneReady) return null;
        var failure = await _capture.StartAsync(_selectedDevice, OnSamples, requireSelectedDevice: true);
        token.ThrowIfCancellationRequested();
        if (failure is not null) return failure;
        if (_deviceId is not null && _capture.DeviceId != _deviceId)
        {
            // A profile belongs to one microphone, so recordings from another one cannot be mixed in.
            _capture.Stop();
            ClearProgress();
            _deviceId = null;
            _phase = Phase.Intro;
            return "The microphone changed. Start calibration again.";
        }
        _deviceId = _capture.DeviceId;
        _microphoneName.Text = $"Microphone: {_capture.DeviceName}";
        _microphoneReady = true;
        _meter.Start();
        return null;
    }

    private async Task RecordAsync(CancellationToken token)
    {
        var step = Steps[_index];
        if (await StartMicrophoneAsync(token) is { } failure)
        {
            Show(Tone.Warning, failure);
            return;
        }

        _message = null;
        _phase = Phase.Countdown;
        for (_countdown = CountdownSeconds; _countdown > 0; _countdown--)
        {
            Render();
            await Task.Delay(1000, token);
        }

        lock (_recordingLock) _recording = new List<float>(step.Seconds * VoiceSoundCalibration.SampleRate);
        _phase = Phase.Recording;
        _timer.Value = 0;
        Render();
        var ticks = step.Seconds * 10;
        for (var tick = 1; tick <= ticks; tick++)
        {
            await Task.Delay(100, token);
            _timer.Value = 100d * tick / ticks;
        }
        float[] audio;
        lock (_recordingLock)
        {
            audio = _recording?.ToArray() ?? [];
            _recording = null;
        }

        _phase = Phase.Ready;
        if (audio.Length < step.Seconds * VoiceSoundCalibration.SampleRate * .8)
        {
            _retry = true;
            Show(Tone.Warning, "Too little microphone audio was received. Try again.");
            return;
        }

        _phase = Phase.Checking;
        Render();
        var quiet = _quiet;
        var background = _background;
        var samples = _samples.ToArray();
        var candidate = _candidate;
        var deviceId = _deviceId!;
        var outcome = await Task.Run(() => Evaluate(step, audio, quiet, background, samples, candidate, deviceId), token);
        token.ThrowIfCancellationRequested();
        Apply(step, audio, outcome);
    }

    private Outcome Evaluate(Step step, float[] audio, float[] quiet, float[] background, float[][] samples,
        VoiceSoundProfile? candidate, string deviceId)
    {
        if (step.Test)
        {
            var check = VoiceSoundCalibration.Check(candidate!, audio, step.Sound == Sound.Word, _existing);
            return new(check.Passed, check.Error, check.RetryStep, Template: check.Template);
        }
        if (step.Sound != Sound.Word)
        {
            var sample = VoiceSoundCalibration.CheckBackgroundSample(audio);
            return new(sample.Passed, sample.Error, RetryStep: true);
        }

        var word = VoiceSoundCalibration.CheckCommandSample(_draft.Id, deviceId, quiet, background, audio, _existing);
        if (!word.Passed) return new(false, word.Error, RetryStep: true);
        if (step.Number < step.Count) return new(true, null, false);
        var fit = VoiceSoundCalibration.Fit(_draft.Id, _draft.Label, _draft.Command, deviceId,
            quiet, background, [.. samples, audio], _existing);
        return new(fit.Profile is not null, fit.Error, false, fit.Profile);
    }

    private void Apply(Step step, float[] audio, Outcome outcome)
    {
        _phase = Phase.Ready;
        if (!outcome.Passed)
        {
            _retry = outcome.RetryStep;
            if (outcome.RetryStep)
            {
                Show(Tone.Warning, outcome.Error);
                return;
            }
            // A usable recording failed, so the word is learned again. The room and playing sound are kept.
            _samples.Clear();
            _candidate = null;
            _index = FirstWordStep;
            Show(Tone.Warning, $"{outcome.Error}\nRecord “{_draft.Label}” again, from recording 1 of {Steps[_index].Count}.");
            return;
        }

        _retry = false;
        if (!step.Test)
        {
            switch (step.Sound)
            {
                case Sound.Quiet:
                    _quiet = audio;
                    break;
                case Sound.Playing:
                    _background = audio;
                    break;
                default:
                    if (outcome.Profile is null) _samples.Add(audio);
                    else
                    {
                        _samples.Clear();
                        _candidate = outcome.Profile;
                    }
                    break;
            }
        }
        else if (outcome.Template is not null)
        {
            _candidate = _candidate! with { VerificationTemplates = [.. _candidate.VerificationTemplates, outcome.Template] };
        }

        _index++;
        Show(Tone.Success, step switch
        {
            { Test: false, Sound: Sound.Quiet } => "Room sound recorded.",
            { Test: false, Sound: Sound.Playing } => "Playing sound recorded.",
            { Test: false } when outcome.Profile is not null => $"Got it. Carousel has learned “{_draft.Label}”. Now test it.",
            { Test: false } => $"Got it. Recording {step.Number} of {step.Count} done.",
            { Sound: Sound.Quiet } => "Test passed. Silence was ignored.",
            { Sound: Sound.Playing } => "Test passed. Your playing was ignored.",
            _ => $"Test passed. Carousel recognised “{_draft.Label}”."
        });
        if (_index == Steps.Length)
        {
            _phase = Phase.Passed;
            StopMicrophone();
        }
    }

    private void Save()
    {
        if (_candidate is null) return;
        try
        {
            _save(_candidate);
            _phase = Phase.Saved;
            _message = null;
        }
        catch
        {
            Show(Tone.Warning, "Could not save calibration. Try saving again.");
        }
        Render();
        FocusPrimary();
    }

    private void OnSamples(float[] audio)
    {
        lock (_recordingLock)
        {
            // Bound temporary audio even if the UI is stalled.
            if (_recording is { Count: < VoiceSoundCalibration.SampleRate * 10 }) _recording.AddRange(audio);
        }
    }

    private void CancelOperation()
    {
        _operation?.Cancel();
        _operation = null;
        lock (_recordingLock) _recording = null;
    }

    private void StopMicrophone()
    {
        _meter.Stop();
        _capture.Stop();
        _microphoneReady = false;
        _level.Value = 0;
    }

    private void ClearProgress()
    {
        _samples.Clear();
        _quiet = _background = [];
        _candidate = null;
        _index = 0;
        _retry = false;
    }

    private void Show(Tone tone, string? text) => _message = text is null ? null : (tone, text);

    private void FocusPrimary()
    {
        if (_active && !_stopped && _primary.IsEnabled) _primary.Focus(FocusState.Programmatic);
    }

    private void Render()
    {
        var started = _phase != Phase.Intro;
        var step = _index < Steps.Length ? Steps[_index] : null;
        for (var stage = 0; stage < StageNames.Length; stage++)
        {
            var first = Array.FindIndex(Steps, item => item.Stage == stage);
            var last = Array.FindLastIndex(Steps, item => item.Stage == stage);
            var state = !started || _index < first ? StageState.Todo
                : _index > last ? StageState.Done : StageState.Current;
            _stages.Update(stage, state, state switch
            {
                StageState.Done => "Done",
                StageState.Current when step!.Count > 1 => $"{step.Number} of {step.Count}",
                StageState.Current => "Now",
                _ => string.Empty
            });
        }

        _progressRow.Visibility = started ? Visibility.Visible : Visibility.Collapsed;
        _stepCount.Text = step is null ? $"All {Steps.Length} steps done" : $"Step {_index + 1} of {Steps.Length}";
        _overall.Value = 100d * _index / Steps.Length;
        AutomationProperties.SetName(_overall, $"Calibration progress: {_stepCount.Text}");

        var label = _draft.Label;
        var (glyph, instruction, detail) = _phase switch
        {
            Phase.Intro => (MicrophoneGlyph, "Before you start", IntroText()),
            Phase.Passed => (DoneGlyph, "All tests passed", $"“{label}” is ready. Press Save calibration to use it."),
            Phase.Saved => (DoneGlyph, "Calibration saved", $"In the reader, say “{label}” for {_action}."),
            _ => step!.Sound switch
            {
                Sound.Quiet when !step.Test => (QuietGlyph, "Stay quiet",
                    $"Carousel listens to your room. Press Record, then stay quiet for {step.Seconds} seconds."),
                Sound.Playing when !step.Test => (MusicGlyph, "Play some music",
                    $"Carousel listens to your playing. Press Record, then play as you normally do for {step.Seconds} seconds. Do not say your word."),
                Sound.Word when !step.Test => (MicrophoneGlyph, $"Say “{label}”",
                    $"Recording {step.Number} of {step.Count}. Press Record, then say “{label}” once and stay quiet."),
                Sound.Quiet => (QuietGlyph, "Test: stay quiet",
                    $"This checks that silence is ignored. Press Record, then stay quiet for {step.Seconds} seconds."),
                Sound.Playing => (MusicGlyph, "Test: play some music",
                    $"This checks that your playing is ignored. Press Record, then play for {step.Seconds} seconds. Do not say your word."),
                _ => (MicrophoneGlyph, $"Test: say “{label}”",
                    $"This checks that Carousel recognises “{label}”. Press Record, then say it once and stay quiet.")
            }
        };
        var finished = _phase is Phase.Passed or Phase.Saved;
        _icon.Text = glyph;
        _icon.Foreground = Ui.Brush(finished ? "SuccessBrush" : "InkBrush");
        _iconBubble.Background = Ui.Brush(finished ? "SuccessSoftBrush" : "SurfaceBrush");
        _instruction.Text = instruction;
        _detail.Text = detail;
        if (_shownInstruction != instruction)
        {
            _shownInstruction = instruction;
            Announce(_instruction);
        }

        _activity.Visibility = _phase is Phase.Countdown or Phase.Recording or Phase.Checking
            ? Visibility.Visible : Visibility.Collapsed;
        _activityIcon.Visibility = _phase == Phase.Recording ? Visibility.Visible : Visibility.Collapsed;
        _activityText.Foreground = Ui.Brush(_phase == Phase.Recording ? "RecordBrush" : "InkBrush");
        _activityText.Text = _phase switch
        {
            Phase.Countdown => $"Get ready… {_countdown}",
            Phase.Recording => step!.Sound switch
            {
                Sound.Quiet => "Recording: stay quiet",
                Sound.Playing => "Recording: keep playing",
                _ => $"Recording: say “{label}” now"
            },
            Phase.Checking when step is { Test: false, Sound: Sound.Word } && step.Number == step.Count => "Learning your word…",
            Phase.Checking => "Checking…",
            _ => string.Empty
        };
        _timer.Visibility = _phase == Phase.Countdown ? Visibility.Collapsed : Visibility.Visible;
        _timer.IsIndeterminate = _phase == Phase.Checking;
        _microphone.Visibility = _microphoneReady ? Visibility.Visible : Visibility.Collapsed;

        _feedback.Visibility = _message is null ? Visibility.Collapsed : Visibility.Visible;
        if (_message is { } message)
        {
            var (strong, soft, icon) = message.Tone switch
            {
                Tone.Success => ("SuccessBrush", "SuccessSoftBrush", DoneGlyph),
                Tone.Warning => ("WarningBrush", "WarningSoftBrush", WarningGlyph),
                _ => ("InkBrush", "SurfaceBrush", InfoGlyph)
            };
            _feedback.Background = Ui.Brush(soft);
            _feedback.BorderBrush = Ui.Brush(strong);
            _feedbackIcon.Text = icon;
            _feedbackIcon.Foreground = Ui.Brush(strong);
            _feedbackText.Text = message.Text;
        }
        if (_shownMessage != _message?.Text)
        {
            _shownMessage = _message?.Text;
            if (_shownMessage is not null) Announce(_feedbackText);
        }

        var primary = _phase switch
        {
            Phase.Intro => "Start calibration",
            Phase.Passed => "Save calibration",
            Phase.Saved => "Done",
            _ => _retry ? "Try again" : "Record"
        };
        _primary.Content = primary;
        AutomationProperties.SetName(_primary, primary);
        _primary.IsEnabled = _operation is null && !_stopped;
        _restart.Visibility = _phase is Phase.Intro or Phase.Saved ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = _phase == Phase.Saved ? Visibility.Collapsed : Visibility.Visible;
    }

    private string IntroText()
    {
        List<string> lines =
        [
            "Sit where you normally play, with the microphone where you normally keep it.",
            "Say the word the same way each time, at your normal volume.",
            "Carousel learns the sound you make, not the text you typed.",
            $"There are {Steps.Length} short recordings. Press Record for each one."
        ];
        if (_recalibrating) lines.Add("Your current calibration stays until you save a new one.");
        return string.Join("\n", lines.Select(line => "• " + line));
    }

    private static void Announce(FrameworkElement element)
    {
        if (element.IsLoaded)
        {
            FrameworkElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
