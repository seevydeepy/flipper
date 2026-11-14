using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Flipper.Core.Reader;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Flipper.App.Services;

// Pixels are borrowed for the synchronous callback; a recorder must copy them.
internal readonly record struct WinkCameraFrame(TimeSpan Timestamp, byte[] Pixels,
    int Width, int Height, WinkEyeObservation Eyes, int Turn, string CameraId = "");

internal static class WinkCameraListener
{
    // Each run owns its camera, models and buffers. Cancellation completes cleanup
    // before the reader starts a replacement run. Images never leave this process.
    public static async Task RunAsync(Action<int> onTurn, Action<string> onStatus, CancellationToken token,
        Action<WinkCameraFrame>? onFrame = null, WinkCalibrationProfile? calibration = null)
    {
        try
        {
            var groups = await MediaFrameSourceGroup.FindAllAsync();
            token.ThrowIfCancellationRequested();
            var choice = groups.SelectMany(group => group.SourceInfos
                    .Where(source => source.SourceKind == MediaFrameSourceKind.Color
                        && source.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord)
                    .Select(source => (Group: group, Source: source)))
                .OrderByDescending(item => item.Source.DeviceInformation?.EnclosureLocation?.Panel == Panel.Front)
                .ThenByDescending(item => item.Source.MediaStreamType == MediaStreamType.VideoPreview)
                .FirstOrDefault();
            if (choice.Group is null)
            {
                onStatus("Wink turning off — no camera");
                return;
            }
            var cameraId = choice.Source.DeviceInformation?.Id ?? choice.Source.Id;
            var profile = calibration is { IsValid: true }
                && string.Equals(calibration.CameraId, cameraId, StringComparison.Ordinal) ? calibration : null;

            using var estimator = await Task.Run(() => new WinkEyeEstimator(), token);
            token.ThrowIfCancellationRequested();
            using var capture = new MediaCapture();
            using var failed = CancellationTokenSource.CreateLinkedTokenSource(token);
            capture.Failed += OnCaptureFailed;
            void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) => failed.Cancel();
            try
            {
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    SourceGroup = choice.Group,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video
                });
                token.ThrowIfCancellationRequested();
                var source = capture.FrameSources[choice.Source.Id];
                using var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
                reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
                using var available = new SemaphoreSlim(0, 1);
                void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
                {
                    try { available.Release(); }
                    catch (SemaphoreFullException) { }
                    catch (ObjectDisposedException) { }
                }
                reader.FrameArrived += OnFrameArrived;
                try
                {
                    failed.Token.ThrowIfCancellationRequested();
                    if (await reader.StartAsync() != MediaFrameReaderStartStatus.Success)
                    {
                        onStatus("Wink turning off — camera unavailable");
                        return;
                    }
                    failed.Token.ThrowIfCancellationRequested();
                    onStatus("Wink turning on");
                    await Task.Run(() => ReadFramesAsync(reader, available, estimator, onTurn, failed.Token, onFrame, cameraId, profile));
                }
                finally
                {
                    reader.FrameArrived -= OnFrameArrived;
                    try { await reader.StopAsync(); }
                    catch (Exception) { }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                onStatus("Wink turning off — camera unavailable");
            }
            finally
            {
                capture.Failed -= OnCaptureFailed;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) onStatus("Wink turning off — camera access denied");
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) onStatus("Wink turning off — camera unavailable");
        }
    }

    private static async Task ReadFramesAsync(MediaFrameReader reader, SemaphoreSlim available,
        WinkEyeEstimator estimator, Action<int> onTurn, CancellationToken token, Action<WinkCameraFrame>? onFrame,
        string cameraId, WinkCalibrationProfile? calibration)
    {
        var detector = new WinkPageTurnDetector();
        var clock = Stopwatch.StartNew();
        var lastFrame = clock.Elapsed;
        TimeSpan? lastTimestamp = null;
        (int Width, int Height, int Rotation)? lastGeometry = null;
        byte[] pixels = [];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!await available.WaitAsync(TimeSpan.FromSeconds(3), token))
                throw new IOException("Camera stopped supplying frames.");

            var started = clock.Elapsed;
            using (var frame = reader.TryAcquireLatestFrame())
            using (var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap)
            {
                if (bitmap is not null && (frame!.SystemRelativeTime is null || frame.SystemRelativeTime != lastTimestamp))
                {
                    lastFrame = started;
                    lastTimestamp = frame.SystemRelativeTime;
                    // The requested reader format is BGRA. CopyToBuffer produces packed rows.
                    using var converted = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                        ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
                    var image = converted ?? bitmap;
                    var size = checked(image.PixelWidth * image.PixelHeight * 4);
                    if (pixels.Length != size) pixels = new byte[size];
                    image.CopyToBuffer(pixels.AsBuffer());
                    var eyes = estimator.ObserveDetailed(pixels, image.PixelWidth, image.PixelHeight);
                    var geometry = (image.PixelWidth, image.PixelHeight, eyes.RotationQuarterTurns);
                    if (lastGeometry != geometry) detector.Reset();
                    lastGeometry = geometry;
                    var states = calibration is null ? (eyes.Left, eyes.Right)
                        : WinkProbabilityClassifier.Classify(calibration, eyes.LeftOpenProbability, eyes.RightOpenProbability);
                    var direction = detector.Observe(states.Item1, states.Item2, started);
                    token.ThrowIfCancellationRequested();
                    onFrame?.Invoke(new WinkCameraFrame(started, pixels, image.PixelWidth, image.PixelHeight, eyes, direction, cameraId));
                    if (direction != 0) onTurn(direction);
                }
                else if (started - lastFrame > TimeSpan.FromSeconds(3))
                {
                    throw new IOException("Camera stopped supplying readable frames.");
                }
            }
            // Sample up to 30 times per second so short winks can provide two clear closed-eye frames.
            var remaining = TimeSpan.FromMilliseconds(33) - (clock.Elapsed - started);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
        }
    }
}
