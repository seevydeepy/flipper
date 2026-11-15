using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Flipper.App.Views;

internal enum BannerTone { Success, Warning, Info }

/// <summary>The latest result in a calibration window. Screen readers announce each new message.</summary>
internal sealed class CalibrationBanner : UserControl
{
    private const string DoneGlyph = "\uE930";
    private const string WarningGlyph = "\uE7BA";
    private const string InfoGlyph = "\uE946";
    private readonly Border _border;
    private readonly TextBlock _icon = Ui.Icon(InfoGlyph, 20);
    private readonly TextBlock _text = Ui.Body();
    private string? _shown;

    public CalibrationBanner()
    {
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Polite);
        _icon.VerticalAlignment = VerticalAlignment.Top;
        _icon.Margin = new Thickness(0, 1, 0, 0);
        Grid.SetColumn(_text, 1);
        _border = new Border
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
                Children = { _icon, _text }
            }
        };
        Content = _border;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Shows <paramref name="text"/>, or hides the banner when it is null.</summary>
    public void Show(BannerTone tone, string? text)
    {
        Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is null)
        {
            _shown = null;
            return;
        }

        var (strong, soft, glyph) = tone switch
        {
            BannerTone.Success => ("SuccessBrush", "SuccessSoftBrush", DoneGlyph),
            BannerTone.Warning => ("WarningBrush", "WarningSoftBrush", WarningGlyph),
            _ => ("InkBrush", "SurfaceBrush", InfoGlyph)
        };
        _border.Background = Ui.Brush(soft);
        _border.BorderBrush = Ui.Brush(strong);
        _icon.Text = glyph;
        _icon.Foreground = Ui.Brush(strong);
        _text.Text = text;
        if (_shown == text) return;
        _shown = text;
        if (_text.IsLoaded)
        {
            FrameworkElementAutomationPeer.CreatePeerForElement(_text)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }
}
