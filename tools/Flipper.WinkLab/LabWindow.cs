using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Flipper.App.Services;
using Flipper.Core.Reader;

namespace Flipper.WinkLab;

internal sealed class LabWindow : Form
{
    private const double WarmupMs = 1000;
    private const double AttemptMs = 3000;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 31, 34) };
    private readonly Label _cameraStatus = TextLabel("Camera is off.");
    private readonly Label _eyes = TextLabel("Your left eye: —\nYour right eye: —");
    private readonly Label _prompt = TextLabel("Start the camera, then record an attempt.", 18, true);
    private readonly Label _detected = TextLabel("Detected: —", 14, true);
    private readonly Label _totals = TextLabel("Labelled attempts: 0");
    private readonly Label _saved = TextLabel("");
    private readonly Button _camera = ActionButton("Start camera");
    private readonly Button _record = ActionButton("Record attempt (Space)");
    private readonly Button _discard = ActionButton("Discard attempt");
    private readonly FlowLayoutPanel _labels = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
    private readonly ListView _history = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private CancellationTokenSource? _cameraCts;
    private Task _cameraRun = Task.CompletedTask;
    private Task _saveRun = Task.CompletedTask;
    private AttemptCapture? _capture;
    private IReadOnlyList<TrialFrame>? _pending;
    private int _trialNumber;
    private int _labelled;
    private int _correct;
    private int _cameraEpoch;
    private int _previewQueued;
    private bool _closing;
    private bool _allowClose;

    public LabWindow(string directory)
    {
        _directory = directory;
        Text = "Flipper wink lab";
        Font = new Font("Segoe UI", 11);
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 720);
        ClientSize = new Size(1060, 780);
        KeyPreview = true;

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 6 };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(TextLabel("Make one gesture, then tell us what you did.\nEach attempt has a one-second lead-in and three seconds for a wink, a blink or no gesture."), 0, 0);

        var live = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 14, 0, 10) };
        live.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        live.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        live.Controls.Add(_preview, 0, 0);
        var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Padding = new Padding(20, 0, 0, 0) };
        details.Controls.Add(_cameraStatus);
        details.Controls.Add(_eyes);
        details.Controls.Add(_prompt);
        details.Controls.Add(_detected);
        details.SizeChanged += (_, _) => ConstrainLabels(details);
        live.Controls.Add(details, 1, 0);
        page.Controls.Add(live, 0, 1);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        actions.Controls.AddRange([_camera, _record, _discard]);
        page.Controls.Add(actions, 0, 2);
        foreach (var (actual, text) in new[]
        {
            (ActualGesture.RightWink, "I winked my RIGHT eye"), (ActualGesture.LeftWink, "I winked my LEFT eye"),
            (ActualGesture.Blink, "I blinked"), (ActualGesture.Neither, "I did neither")
        })
        {
            var button = ActionButton(text);
            button.Click += (_, _) => SaveLabel(actual);
            _labels.Controls.Add(button);
        }
        page.Controls.Add(_labels, 0, 3);
        _history.Columns.Add("Attempt", 75);
        _history.Columns.Add("What you did", 190);
        _history.Columns.Add("Result", 160);
        _history.Columns.Add("Turns detected", 320);
        page.Controls.Add(_history, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        footer.Controls.Add(_totals);
        footer.Controls.Add(_saved);
        footer.Controls.Add(TextLabel("Attempt recordings and labels are saved on this device. The preview is mirrored; recordings are not."));
        var path = new TextBox { Text = directory, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, TabStop = false };
        path.AccessibleName = "Recording folder";
        footer.Controls.Add(path);
        footer.SizeChanged += (_, _) => ConstrainLabels(footer);
        page.Controls.Add(footer, 0, 5);
        Controls.Add(page);
        _record.Enabled = _discard.Enabled = _labels.Enabled = false;
        _camera.Click += async (_, _) => await ToggleCameraAsync();
        _record.Click += (_, _) => BeginAttempt();
        _discard.Click += (_, _) => DiscardAttempt();
        KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Space || !_record.Enabled) return;
            args.Handled = args.SuppressKeyPress = true;
            BeginAttempt();
        };
        FormClosing += ClosingAsync;
    }

    private async Task ToggleCameraAsync()
    {
        _camera.Enabled = false;
        if (_cameraCts is not null)
        {
            _record.Enabled = false;
            ++_cameraEpoch;
            _cameraCts.Cancel();
            await _cameraRun;
            _cameraCts.Dispose();
            _cameraCts = null;
            lock (_gate) _capture = null;
            _record.Enabled = false;
            _camera.Text = "Start camera";
            _cameraStatus.Text = "Camera is off.";
            if (_pending is null) _prompt.Text = "Start the camera, then record an attempt.";
            _preview.Image?.Dispose();
            _preview.Image = null;
        }
        else
        {
            var epoch = ++_cameraEpoch;
            _cameraCts = new CancellationTokenSource();
            _camera.Text = "Stop camera";
            _cameraStatus.Text = "Starting camera…";
            _cameraRun = RunCameraAsync(epoch, _cameraCts.Token);
        }
        _camera.Enabled = true;
    }

    private async Task RunCameraAsync(int epoch, CancellationToken token)
    {
        await WinkCameraListener.RunAsync(_ => { }, status => Post(() =>
        {
            if (epoch == _cameraEpoch) _cameraStatus.Text = status;
        }), token, frame => OnFrame(frame, epoch));
        Post(() =>
        {
            if (epoch != _cameraEpoch || _closing) return;
            _record.Enabled = false;
            _camera.Enabled = true;
            lock (_gate) _capture = null;
            _camera.Text = "Reset camera";
            _prompt.Text = "Camera stopped. Start it again to continue.";
        });
    }

    private void BeginAttempt()
    {
        if (!_record.Enabled) return;
        lock (_gate) _capture = new AttemptCapture();
        _pending = null;
        _record.Enabled = _labels.Enabled = _discard.Enabled = false;
        _camera.Enabled = false;
        _prompt.Text = "Keep both eyes open…";
        _detected.Text = "Detected: —";
    }

    private void OnFrame(WinkCameraFrame frame, int epoch)
    {
        IReadOnlyList<TrialFrame>? finished = null;
        double? elapsed = null;
        string? turns = null;
        lock (_gate)
        {
            if (_capture is { } capture)
            {
                capture.Start ??= frame.Timestamp;
                var relativeTime = frame.Timestamp - capture.Start.Value;
                elapsed = relativeTime.TotalMilliseconds;
                var turn = capture.Detector.Observe(frame.Eyes.Left, frame.Eyes.Right, relativeTime);
                capture.Frames.Add(new TrialFrame(elapsed.Value, frame.Width, frame.Height,
                    (byte[])frame.Pixels.Clone(), frame.Eyes, turn));
                turns = DescribeTurns(capture.Frames);
                if (elapsed >= WarmupMs + AttemptMs)
                {
                    finished = capture.Frames;
                    _capture = null;
                }
            }
        }
        if (finished is not null)
        {
            var completed = finished;
            Post(() =>
            {
                if (epoch != _cameraEpoch || _closing) return;
                _pending = completed;
                _camera.Enabled = true;
                _labels.Enabled = _discard.Enabled = true;
                _prompt.Text = "What did you actually do?";
                _detected.Text = "Detected: " + DescribeTurns(completed);
            });
        }

        if (Interlocked.Exchange(ref _previewQueued, 1) != 0) return;
        Bitmap? preview = null;
        try
        {
            preview = MakePreview(frame);
            var picture = preview;
            if (!Post(() =>
            {
                try
                {
                    if (epoch != _cameraEpoch || _closing) { picture.Dispose(); return; }
                    var previous = _preview.Image;
                    _preview.Image = picture;
                    previous?.Dispose();
                    _eyes.Text = $"Your left eye: {EyeText(frame.Eyes.Left, frame.Eyes.LeftOpenProbability)}\n"
                        + $"Your right eye: {EyeText(frame.Eyes.Right, frame.Eyes.RightOpenProbability)}\n"
                        + frame.Eyes.TrackingStatus.Replace('_', ' ');
                    if (elapsed is { } time && finished is null)
                    {
                        _prompt.Text = time < WarmupMs ? "Keep both eyes open…"
                            : $"Make ONE gesture now\n{Math.Max(1, (int)Math.Ceiling((WarmupMs + AttemptMs - time) / 1000))} seconds left";
                        _detected.Text = "Detected: " + turns;
                    }
                    if (_pending is null && _capture is null && _saveRun.IsCompleted && finished is null && !_cameraRun.IsCompleted)
                        _record.Enabled = true;
                }
                finally { Interlocked.Exchange(ref _previewQueued, 0); }
            }))
            {
                picture.Dispose();
                Interlocked.Exchange(ref _previewQueued, 0);
            }
        }
        catch
        {
            preview?.Dispose();
            Interlocked.Exchange(ref _previewQueued, 0);
            throw;
        }
    }

    private void SaveLabel(ActualGesture actual)
    {
        if (_pending is not { } frames || !_saveRun.IsCompleted) return;
        _labels.Enabled = _discard.Enabled = false;
        _prompt.Text = "Saving attempt…";
        _saveRun = SaveAsync(actual, frames);
    }

    private async Task SaveAsync(ActualGesture actual, IReadOnlyList<TrialFrame> frames)
    {
        try
        {
            var number = _trialNumber + 1;
            var summary = await Task.Run(() => TrialData.SaveAsync(_directory, number, actual, frames, WarmupMs));
            _trialNumber = number;
            _labelled++;
            if (summary.Outcome == TrialOutcome.Correct) _correct++;
            _history.Items.Insert(0, new ListViewItem([number.ToString(), GestureText(actual), OutcomeText(summary.Outcome), DescribeTurns(frames)]));
            _totals.Text = $"Labelled attempts: {_labelled} · Correct: {_correct} · Other results: {_labelled - _correct}";
            _saved.Text = $"Saved attempt {number}.";
            _prompt.Text = "Ready for another attempt.";
            _pending = null;
            _record.Enabled = _cameraCts is not null && !_cameraRun.IsCompleted;
        }
        catch (Exception ex)
        {
            _prompt.Text = "Could not save. Your label buttons will retry.";
            _saved.Text = ex.Message;
            _labels.Enabled = _discard.Enabled = true;
        }
    }

    private void DiscardAttempt()
    {
        _pending = null;
        _labels.Enabled = _discard.Enabled = false;
        _record.Enabled = _cameraCts is not null && !_cameraRun.IsCompleted;
        _prompt.Text = "Ready for another attempt.";
        _detected.Text = "Detected: —";
    }

    private async void ClosingAsync(object? sender, FormClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        Enabled = false;
        ++_cameraEpoch;
        _cameraCts?.Cancel();
        await _cameraRun;
        await _saveRun;
        _cameraCts?.Dispose();
        _preview.Image?.Dispose();
        _allowClose = true;
        Close();
    }

    private bool Post(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return false;
        try { BeginInvoke(action); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static Bitmap MakePreview(WinkCameraFrame frame)
    {
        using var full = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var locked = full.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Pixels, row * frame.Width * 4, locked.Scan0 + row * locked.Stride, frame.Width * 4);
        }
        finally { full.UnlockBits(locked); }
        var preview = new Bitmap(full, new Size(480, Math.Max(1, frame.Height * 480 / frame.Width)));
        preview.RotateFlip(RotateFlipType.RotateNoneFlipX);
        return preview;
    }

    private static string DescribeTurns(IEnumerable<TrialFrame> frames)
    {
        var turns = frames.Where(frame => frame.TimeMs >= WarmupMs && frame.Turn != 0)
            .Select(frame => frame.Turn == 1 ? "forward" : "backward").ToArray();
        return turns.Length == 0 ? "no turn" : string.Join(", ", turns);
    }

    private static string EyeText(EyeState state, float? open) => open is { } probability
        ? $"{state.ToString().ToLowerInvariant()} ({probability:P0} open)" : "unknown";

    private static string GestureText(ActualGesture actual) => actual switch
    {
        ActualGesture.RightWink => "Right wink",
        ActualGesture.LeftWink => "Left wink",
        ActualGesture.Blink => "Blink",
        _ => "Neither"
    };

    private static string OutcomeText(TrialOutcome outcome) => outcome switch
    {
        TrialOutcome.WrongDirection => "Wrong direction",
        TrialOutcome.ExtraTurn => "Extra turn",
        TrialOutcome.UnwantedTurn => "Unwanted turn",
        _ => outcome.ToString()
    };

    private static Label TextLabel(string text, float size = 11, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8),
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private static void ConstrainLabels(TableLayoutPanel panel)
    {
        foreach (var label in panel.Controls.OfType<Label>())
            label.MaximumSize = new Size(Math.Max(1, panel.ClientSize.Width - panel.Padding.Horizontal - label.Margin.Horizontal), 0);
    }

    private static Button ActionButton(string text) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(140, 42), Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 4, 8, 4)
    };

    private sealed class AttemptCapture
    {
        public TimeSpan? Start;
        public WinkPageTurnDetector Detector { get; } = new();
        public List<TrialFrame> Frames { get; } = [];
    }
}
