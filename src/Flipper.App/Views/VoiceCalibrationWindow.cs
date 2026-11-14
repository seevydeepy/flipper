using Flipper.Core.Reader;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Flipper.App.Views;

internal sealed class VoiceCalibrationWindow : Window
{
    private static VoiceCalibrationWindow? _current;
    private readonly VoiceCalibrationPanel _panel;
    private readonly MainWindow? _owner;

    private VoiceCalibrationWindow(string? deviceId, Action<VoiceCalibrationProfile> save)
    {
        Title = "Voice calibration — Carousel";
        _owner = App.Current.Window;
        _panel = new VoiceCalibrationPanel(this, deviceId, save, Close);
        Content = new Grid
        {
            RequestedTheme = ElementTheme.Light,
            Background = (Brush)Application.Current.Resources["PaperBrush"],
            Children =
            {
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollMode = ScrollMode.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new Border { Padding = new Thickness(24), MaxWidth = 760, Child = _panel }
                }
            }
        };
        if (_owner is not null) _owner.Closed += Owner_Closed;
        Closed += (_, _) =>
        {
            _panel.Stop();
            if (_owner is not null) _owner.Closed -= Owner_Closed;
            _current = null;
        };
        var area = DisplayArea.GetFromWindowId(_owner?.AppWindow.Id ?? AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = (_owner?.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Min((int)(660 * scale), area.Width);
        var height = Math.Min((int)(740 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    public static void Show(string? deviceId, Action<VoiceCalibrationProfile> save)
    {
        _current ??= new VoiceCalibrationWindow(deviceId, save);
        _current.Activate();
    }

    public static void CloseCurrent() => _current?.Close();
    private void Owner_Closed(object sender, WindowEventArgs args) => Close();
}
