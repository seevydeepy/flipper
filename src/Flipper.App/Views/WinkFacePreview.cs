using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Flipper.App.Services;
using Flipper.Core.Reader;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Flipper.App.Views;

internal sealed class WinkFacePreview : UserControl
{
    private const int MaximumPreviewSide = 480;
    private const int UpdateIntervalMs = 100;
    private const int ReframeDelayMs = 800;
    private const int ReframeIntervalMs = 4000;
    private const int LabelGap = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _overlays = new() { IsHitTestVisible = false };
    private readonly Ellipse _leftRing = Ring();
    private readonly Ellipse _rightRing = Ring();
    private readonly Border _leftLabel = Label();
    private readonly Border _rightLabel = Label();
    private readonly SolidColorBrush _open = new(Color.FromArgb(255, 72, 231, 117));
    private readonly SolidColorBrush _closed = new(Color.FromArgb(255, 255, 102, 102));
    private readonly SolidColorBrush _unknown = new(Color.FromArgb(255, 255, 222, 79));
    private WriteableBitmap? _bitmap;
    private byte[] _previewPixels = [];
    private EyeOverlay _left;
    private EyeOverlay _right;
    private long _lastQueuedAt;
    private int _queued;
    private int _epoch;

    private int _framingEpoch;
    private (int Width, int Height) _sourceSize;
    private int _displayRotation;
    private FrameCrop? _faceCrop;
    private long _lastReframeAt;
    private long? _reframeNeededSince;

    private readonly record struct EyeOverlay(EyeState State, float? OpenProbability, Vector2? Centre);
    private readonly record struct FrameCrop(int X, int Y, int Width, int Height);

    public WinkFacePreview()
    {
        _dispatcher = DispatcherQueue;
        AutomationProperties.SetName(_image, "Camera preview");
        _overlays.Children.Add(_leftRing);
        _overlays.Children.Add(_rightRing);
        _overlays.Children.Add(_leftLabel);
        _overlays.Children.Add(_rightLabel);
        _overlays.SizeChanged += (_, _) => LayoutOverlays();
        Content = new Grid { Children = { _image, _overlays } };
    }

    // Consume the borrowed camera pixels before returning to the capture loop.
    public void SetFrame(WinkCameraFrame frame, EyeState left, EyeState right, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return;
        var epoch = Volatile.Read(ref _epoch);
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastQueuedAt) < UpdateIntervalMs || Interlocked.Exchange(ref _queued, 1) != 0) return;
        Volatile.Write(ref _lastQueuedAt, now);

        var queued = false;
        try
        {
            var (crop, rotation) = FrameFace(frame, epoch, now);
            var scale = Math.Min(1d, (double)MaximumPreviewSide / Math.Max(crop.Width, crop.Height));
            var previewWidth = Math.Max(1, (int)Math.Round(crop.Width * scale));
            var previewHeight = Math.Max(1, (int)Math.Round(crop.Height * scale));
            var size = previewWidth * previewHeight * 4;
            if (_previewPixels.Length < size) _previewPixels = new byte[size];

            // Read the crop from right to left to mirror the image without mirroring the labels.
            for (var y = 0; y < previewHeight; y++)
            {
                var frameY = crop.Y + (int)((y + 0.5) * crop.Height / previewHeight);
                for (var x = 0; x < previewWidth; x++)
                {
                    var frameX = crop.X + (int)((previewWidth - x - 0.5) * crop.Width / previewWidth);
                    var source = WinkFrameRotation.ToSource(new Vector2(frameX, frameY), frame.Width, frame.Height, rotation);
                    var from = ((int)source.Y * frame.Width + (int)source.X) * 4;
                    var to = (y * previewWidth + x) * 4;
                    frame.Pixels.AsSpan(from, 3).CopyTo(_previewPixels.AsSpan(to, 3));
                    _previewPixels[to + 3] = 255;
                }
            }

            var leftEye = new EyeOverlay(EyeState.Unknown, null, null);
            var rightEye = new EyeOverlay(EyeState.Unknown, null, null);
            if (frame.Eyes.Geometry is { } face)
            {
                leftEye = new(left, frame.Eyes.LeftOpenProbability, MirrorInCrop(face.LeftEyeX, face.LeftEyeY, crop));
                rightEye = new(right, frame.Eyes.RightOpenProbability, MirrorInCrop(face.RightEyeX, face.RightEyeY, crop));
            }

            queued = _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (epoch != _epoch || token.IsCancellationRequested) return;
                    if (_bitmap is null || _bitmap.PixelWidth != previewWidth || _bitmap.PixelHeight != previewHeight)
                    {
                        _bitmap = new WriteableBitmap(previewWidth, previewHeight);
                        _image.Source = _bitmap;
                    }

                    using var stream = _bitmap.PixelBuffer.AsStream();
                    stream.Write(_previewPixels, 0, size);
                    _bitmap.Invalidate();
                    _left = leftEye;
                    _right = rightEye;
                    LayoutOverlays();
                }
                finally { Interlocked.Exchange(ref _queued, 0); }
            });
        }
        finally
        {
            if (!queued) Interlocked.Exchange(ref _queued, 0);
        }
    }

    public void Reset()
    {
        Interlocked.Increment(ref _epoch);
        _bitmap = null;
        _image.Source = null;
        _overlays.Visibility = Visibility.Collapsed;
    }

    private (FrameCrop Crop, int Rotation) FrameFace(WinkCameraFrame frame, int epoch, long now)
    {
        var geometry = frame.Eyes.Geometry;
        var rotation = frame.Eyes.RotationQuarterTurns;
        // Keep the last confirmed orientation while the estimator searches for a face.
        if (_framingEpoch != epoch || _sourceSize != (frame.Width, frame.Height)
            || (geometry.HasValue && _displayRotation != rotation))
        {
            _framingEpoch = epoch;
            _sourceSize = (frame.Width, frame.Height);
            _displayRotation = rotation;
            _faceCrop = null;
            _reframeNeededSince = null;
        }

        var (width, height) = WinkFrameRotation.GetSize(frame.Width, frame.Height, _displayRotation);
        if (geometry is not { Width: > 0, Height: > 0 } face)
        {
            _reframeNeededSince = null;
        }
        else if (_faceCrop is not { } crop)
        {
            _faceCrop = CropFace(width, height, face);
            _lastReframeAt = now;
        }
        else if (FitsCrop(face, crop))
        {
            _reframeNeededSince = null;
        }
        else
        {
            _reframeNeededSince ??= now;
            if (now - _reframeNeededSince.Value >= ReframeDelayMs && now - _lastReframeAt >= ReframeIntervalMs)
            {
                _reframeNeededSince = null;
                var next = CropFace(width, height, face);
                if (next != crop)
                {
                    _faceCrop = next;
                    _lastReframeAt = now;
                }
            }
        }

        return (_faceCrop ?? new FrameCrop(0, 0, width, height), _displayRotation);
    }

    private static bool FitsCrop(WinkFaceGeometry face, FrameCrop crop)
    {
        var inset = crop.Width * 0.04;
        return Math.Max(face.Width, face.Height) >= crop.Width * 0.5
            && face.X >= crop.X + inset && face.Y >= crop.Y + inset
            && face.X + face.Width <= crop.X + crop.Width - inset
            && face.Y + face.Height <= crop.Y + crop.Height - inset;
    }

    private static FrameCrop CropFace(int width, int height, WinkFaceGeometry face)
    {
        var side = (int)Math.Ceiling(Math.Clamp(1.35 * Math.Max(face.Width, face.Height), 1, Math.Min(width, height)));
        var x = (int)Math.Round(Math.Clamp(face.X + (face.Width - side) / 2d, 0, width - side));
        var y = (int)Math.Round(Math.Clamp(face.Y + (face.Height - side) / 2d, 0, height - side));
        return new(x, y, side, side);
    }

    private static Vector2? MirrorInCrop(float x, float y, FrameCrop crop)
    {
        var position = new Vector2((x - crop.X + 0.5f) / crop.Width, (y - crop.Y + 0.5f) / crop.Height);
        return position is { X: >= 0 and <= 1, Y: >= 0 and <= 1 }
            ? new Vector2(1 - position.X, position.Y) : null;
    }

    private void LayoutOverlays()
    {
        if (_bitmap is not { } bitmap) return;
        _overlays.Visibility = Visibility.Visible;
        var width = _overlays.ActualWidth;
        var height = _overlays.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var scale = Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight);
        var imageWidth = bitmap.PixelWidth * scale;
        var imageHeight = bitmap.PixelHeight * scale;
        var imageBounds = new Rect((width - imageWidth) / 2, (height - imageHeight) / 2, imageWidth, imageHeight);
        var leftOnLeft = _left.Centre is not { } left || _right.Centre is not { } right || left.X <= right.X;
        LayoutEye(_left, _leftRing, _leftLabel, "Left eye", imageBounds, leftOnLeft);
        LayoutEye(_right, _rightRing, _rightLabel, "Right eye", imageBounds, !leftOnLeft);
    }

    private void LayoutEye(EyeOverlay eye, Ellipse ring, Border label, string eyeName, Rect image, bool onLeft)
    {
        var width = _overlays.ActualWidth;
        var height = _overlays.ActualHeight;
        var colour = eye.State switch { EyeState.Open => _open, EyeState.Closed => _closed, _ => _unknown };
        var text = eye.OpenProbability is { } probability && float.IsFinite(probability)
            ? $"{eye.State} · {probability * 100:F0}% open" : eye.State.ToString();
        var textBlock = (TextBlock)label.Child;
        textBlock.Text = eye.Centre is null ? $"{eyeName}: {text}" : text;
        textBlock.Foreground = colour;
        AutomationProperties.SetName(textBlock, $"{eyeName}, {text}");

        var maxLabelWidth = Math.Max(1, width / 2 - LabelGap * 2);
        label.Visibility = Visibility.Visible;
        label.MaxWidth = maxLabelWidth;
        label.Measure(new Size(maxLabelWidth, double.PositiveInfinity));
        var labelWidth = label.DesiredSize.Width;
        var labelHeight = label.DesiredSize.Height;
        var minX = onLeft ? LabelGap : width / 2 + LabelGap;
        var maxX = Math.Max(minX, (onLeft ? width / 2 : width) - labelWidth - LabelGap);
        var labelX = Math.Clamp(onLeft ? image.Left : image.Right - labelWidth, minX, maxX);
        var labelY = image.Bottom - labelHeight - LabelGap;

        ring.Visibility = Visibility.Collapsed;
        if (eye.Centre is { } centre)
        {
            var x = image.X + centre.X * image.Width;
            var y = image.Y + centre.Y * image.Height;
            var diameter = Math.Clamp(image.Width * 0.12, 16, 40);
            ring.Stroke = colour;
            ring.Width = ring.Height = diameter;
            Canvas.SetLeft(ring, x - diameter / 2);
            Canvas.SetTop(ring, y - diameter / 2);
            ring.Visibility = Visibility.Visible;
            labelX = Math.Clamp(x - labelWidth / 2, minX, maxX);
            labelY = y + diameter / 2 + LabelGap;
            if (labelY + labelHeight > height) labelY = y - diameter / 2 - LabelGap - labelHeight;
        }

        Canvas.SetLeft(label, labelX);
        Canvas.SetTop(label, Math.Clamp(labelY, 0, Math.Max(0, height - labelHeight)));
    }

    private static Ellipse Ring() => new() { StrokeThickness = 3, Visibility = Visibility.Collapsed };

    private static Border Label() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(232, 0, 0, 0)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 2, 5, 2),
        Child = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap },
        Visibility = Visibility.Collapsed
    };
}
