using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Flipper.App.Controls;

public sealed class ScrollingText : Canvas
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(ScrollingText),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Brush),
        typeof(ScrollingText),
        new PropertyMetadata(null, OnForegroundChanged));

    private const double StillSlack = 1;
    private const double PixelsPerSecond = 18;
    private const double HoldSeconds = 2;

    private readonly TextBlock _label = CreateLabel();

    private double _overflow;
    private bool _running;

    public ScrollingText()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        Children.Add(_label);
        Loaded += (_, _) => InvalidateMeasure();
        Unloaded += (_, _) => StopScroll();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private static TextBlock CreateLabel()
    {
        return new TextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None
        };
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ScrollingText control)
        {
            return;
        }

        control._label.Text = args.NewValue as string ?? string.Empty;
        control.StopScroll();
        control.InvalidateMeasure();
    }

    private static void OnForegroundChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is ScrollingText control)
        {
            control._label.Foreground = args.NewValue as Brush;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var height = Math.Max(_label.DesiredSize.Height, 1);
        var width = double.IsInfinity(availableSize.Width)
            ? _label.DesiredSize.Width
            : availableSize.Width;
        return new Size(Math.Max(0, width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, finalSize.Width, finalSize.Height) };
        Canvas.SetLeft(_label, 0);
        Canvas.SetTop(_label, 0);

        var overflow = _label.DesiredSize.Width - finalSize.Width;
        if (overflow <= StillSlack)
        {
            StopScroll();
            base.ArrangeOverride(finalSize);
            return finalSize;
        }

        base.ArrangeOverride(finalSize);
        if (!_running || Math.Abs(overflow - _overflow) >= StillSlack)
        {
            StartScroll(overflow);
        }

        return finalSize;
    }

    private void StartScroll(double overflow)
    {
        StopScroll();
        _overflow = overflow;
        var travel = overflow / PixelsPerSecond;
        var total = HoldSeconds + travel + HoldSeconds;
        var compositor = ElementCompositionPreview.GetElementVisual(_label).Compositor;
        var linear = compositor.CreateLinearEasingFunction();
        var mildOut = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0f),
            new Vector2(0.35f, 1f));
        var startHold = (float)(HoldSeconds / total);
        var cruise = (float)((HoldSeconds + travel * 0.88) / total);
        var arrive = (float)((HoldSeconds + travel) / total);
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromSeconds(total);
        animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(startHold, 0);
        animation.InsertKeyFrame(cruise, (float)(-overflow * 0.88), linear);
        animation.InsertKeyFrame(arrive, (float)-overflow, mildOut);
        animation.InsertKeyFrame(1, (float)-overflow);
        ElementCompositionPreview.GetElementVisual(_label).StartAnimation("Offset.X", animation);
        _running = true;
    }

    private void StopScroll()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_label);
        visual.StopAnimation("Offset.X");
        visual.Offset = new Vector3(0, visual.Offset.Y, visual.Offset.Z);
        _running = false;
        _overflow = 0;
    }
}
