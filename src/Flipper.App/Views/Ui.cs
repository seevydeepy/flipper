using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Flipper.App.Views;

/// <summary>Shared look for the views that are built in code.</summary>
internal static class Ui
{
    public const double BodySize = 16;
    public const double CaptionSize = 15;
    public const double LabelSize = 17;
    public const double TitleSize = 26;
    public const double TouchSize = 48;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons,Segoe MDL2 Assets");
    private static readonly FontFamily TitleFont = new("Cambria");
    private static readonly SemaphoreSlim DialogSlot = new(1);

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock Title(string text, double size = TitleSize, AutomationHeadingLevel level = AutomationHeadingLevel.Level2)
    {
        var title = new TextBlock
        {
            Text = text,
            FontFamily = TitleFont,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("InkBrush")
        };
        AutomationProperties.SetHeadingLevel(title, level);
        return title;
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = LabelSize,
        FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("InkBrush")
    };

    public static TextBlock Body(string text = "") => new()
    {
        Text = text,
        FontSize = BodySize,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("InkBrush")
    };

    public static TextBlock Caption(string text = "") => new()
    {
        Text = text,
        FontSize = CaptionSize,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("MuteBrush")
    };

    /// <summary>An icon as text, so that it takes the colour of the control that holds it.</summary>
    public static TextBlock Icon(string glyph, double size = 20)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = Style(primary ? "PrimaryButton" : "SecondaryButton")
        };
        AutomationProperties.SetName(button, text);
        return button;
    }

    public static Button IconButton(string glyph, string text, bool primary = false)
    {
        var button = Button(text, primary);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                Icon(glyph, 16),
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        return button;
    }

    /// <summary>A short status: an icon and text in the colour of <paramref name="brush"/>.</summary>
    public static StackPanel Status(string glyph, string text, string brush)
    {
        var icon = Icon(glyph, 16);
        icon.Foreground = Brush(brush);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                icon,
                new TextBlock
                {
                    Text = text,
                    FontSize = CaptionSize,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush(brush)
                }
            }
        };
    }

    /// <summary>A <see cref="Status"/> in a rounded pill.</summary>
    public static Border Chip(string glyph, string text, string brush, string background) => new()
    {
        Background = Brush(background),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(12, 5, 12, 5),
        Child = Status(glyph, text, brush)
    };

    public static Border Card(UIElement child) => new()
    {
        Background = Brush("CardBrush"),
        BorderBrush = Brush("GoldSoftBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(20),
        Child = child
    };

    public static Border Rule() => new()
    {
        Height = 1,
        Background = Brush("GoldSoftBrush")
    };

    /// <summary>
    /// Puts <paramref name="trailing"/> to the right of <paramref name="main"/>, or below it
    /// when the row is narrower than <paramref name="stackBelow"/>.
    /// </summary>
    public static Grid Split(FrameworkElement main, FrameworkElement trailing, double stackBelow = 440)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.VerticalAlignment = VerticalAlignment.Center;
        trailing.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(main);
        grid.Children.Add(trailing);

        void Arrange(double width)
        {
            var stacked = width > 0 && width < stackBelow;
            Grid.SetColumnSpan(main, stacked ? 2 : 1);
            Grid.SetColumn(trailing, stacked ? 0 : 1);
            Grid.SetRow(trailing, stacked ? 1 : 0);
            Grid.SetColumnSpan(trailing, stacked ? 2 : 1);
            trailing.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        Arrange(0);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        return grid;
    }

    /// <summary>Draws a standard control larger, for touch and for weaker eyesight.</summary>
    public static Viewbox Enlarge(FrameworkElement control, double height = 44) => new()
    {
        Height = height,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
        Child = control
    };

    public static async Task<ContentDialogResult> ShowDialogAsync(
        ContentDialog dialog, CancellationToken cancellationToken = default)
    {
        await DialogSlot.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            dialog.RequestedTheme = ElementTheme.Light;
            var button = Style("SecondaryButton");
            dialog.PrimaryButtonStyle = button;
            dialog.SecondaryButtonStyle = button;
            dialog.CloseButtonStyle = button;
            dialog.Opened += (_, _) => SizeDialogButtons(dialog);
            return await dialog.ShowAsync();
        }
        finally
        {
            DialogSlot.Release();
        }
    }

    /// <summary>
    /// The dialog gives its default button the system accent style, which ignores the styles
    /// above. This gives that button the same size as the others.
    /// </summary>
    private static void SizeDialogButtons(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button { Name: "PrimaryButton" or "SecondaryButton" or "CloseButton" } button)
            {
                button.MinHeight = TouchSize;
                button.CornerRadius = new CornerRadius(8);
                button.FontWeight = FontWeights.SemiBold;
                continue;
            }

            SizeDialogButtons(child);
        }
    }
}
