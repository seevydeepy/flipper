using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Flipper.App.Controls;

public sealed partial class ManuscriptBackdrop : UserControl
{
    private readonly SolidColorBrush _staffBrush = new(Color.FromArgb(72, 148, 168, 182));
    private readonly DispatcherTimer _paintTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _lastWidth;
    private int _lastHeight;

    public ManuscriptBackdrop()
    {
        InitializeComponent();
        _paintTimer.Tick += (_, _) =>
        {
            _paintTimer.Stop();
            DrawStaves();
        };
        SizeChanged += (_, _) =>
        {
            _paintTimer.Stop();
            _paintTimer.Start();
        };
        Loaded += (_, _) => _paintTimer.Start();
        Unloaded += (_, _) => _paintTimer.Stop();
    }

    private void DrawStaves()
    {
        var width = (int)Math.Round(ActualWidth);
        var height = (int)Math.Round(ActualHeight);
        if (width < 8 || height < 8 || (width == _lastWidth && height == _lastHeight && StaffCanvas.Children.Count > 0))
        {
            return;
        }

        StaffCanvas.Children.Clear();
        const double lineGap = 11;
        const double staffGap = 46;
        const double startY = 30;
        var staffHeight = lineGap * 4;
        var left = 18.0;
        var right = Math.Max(left + 8, ActualWidth - 18);

        for (var top = startY; top + staffHeight < ActualHeight - 12; top += staffHeight + staffGap)
        {
            for (var line = 0; line < 5; line++)
            {
                var y = top + line * lineGap;
                StaffCanvas.Children.Add(new Line
                {
                    X1 = left,
                    Y1 = y,
                    X2 = right,
                    Y2 = y,
                    Stroke = _staffBrush,
                    StrokeThickness = 1
                });
            }
        }

        _lastWidth = width;
        _lastHeight = height;
    }
}
