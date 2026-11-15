using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Flipper.App.Views;

internal enum StageState { Todo, Current, Done }

/// <summary>A row of numbered stages joined by a line. Done stages show a tick.</summary>
internal sealed class VoiceCalibrationStages : UserControl
{
    private const string TickGlyph = "\uE73E";
    private const double CircleSize = 40;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons,Segoe MDL2 Assets");
    private readonly Stage[] _stages;

    private sealed record Stage(Border Circle, TextBlock Mark, TextBlock Title, TextBlock Detail,
        Border Before, Border After);

    public VoiceCalibrationStages(IReadOnlyList<string> names)
    {
        var grid = new Grid();
        _stages = new Stage[names.Count];
        for (var index = 0; index < names.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var before = Line(index > 0);
            var after = Line(index < names.Count - 1);
            Grid.SetColumn(after, 1);
            var mark = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                IsTextScaleFactorEnabled = false
            };
            var circle = new Border
            {
                Width = CircleSize,
                Height = CircleSize,
                CornerRadius = new CornerRadius(CircleSize / 2),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = mark
            };
            Grid.SetColumnSpan(circle, 2);
            var track = new Grid
            {
                Height = CircleSize,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                },
                Children = { before, after, circle }
            };
            var title = new TextBlock
            {
                Text = names[index],
                FontSize = Ui.BodySize,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 0)
            };
            var detail = new TextBlock
            {
                FontSize = Ui.CaptionSize,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 0),
                Foreground = Ui.Brush("MuteBrush")
            };
            var cell = new StackPanel { Spacing = 6, Children = { track, title, detail } };
            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
            _stages[index] = new Stage(circle, mark, title, detail, before, after);
        }

        for (var index = 0; index < _stages.Length; index++)
        {
            Update(index, StageState.Todo, string.Empty);
        }
        AutomationProperties.SetAccessibilityView(grid, AccessibilityView.Raw);
        Content = grid;
    }

    public void Update(int index, StageState state, string detail)
    {
        var stage = _stages[index];
        var done = state == StageState.Done;
        stage.Mark.Text = done ? TickGlyph : (index + 1).ToString();
        stage.Mark.FontFamily = done ? IconFont : FontFamily.XamlAutoFontFamily;
        stage.Mark.FontSize = done ? 18 : 17;
        stage.Circle.Background = Ui.Brush(state switch
        {
            StageState.Done => "SuccessBrush",
            StageState.Current => "InkBrush",
            _ => "CardBrush"
        });
        stage.Circle.BorderBrush = Ui.Brush(state switch
        {
            StageState.Done => "SuccessBrush",
            StageState.Current => "InkBrush",
            _ => "GoldBrush"
        });
        stage.Mark.Foreground = Ui.Brush(state == StageState.Todo ? "MuteBrush" : "CardBrush");
        stage.Title.FontWeight = state == StageState.Current ? FontWeights.SemiBold : FontWeights.Normal;
        stage.Title.Foreground = Ui.Brush(state == StageState.Todo ? "MuteBrush" : "InkBrush");
        stage.Detail.Text = detail;
        stage.Detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        stage.After.Background = Ui.Brush(done ? "SuccessBrush" : "GoldSoftBrush");
        if (index + 1 < _stages.Length)
        {
            _stages[index + 1].Before.Background = stage.After.Background;
        }
    }

    private static Border Line(bool visible) => new()
    {
        Height = 3,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
        Background = Ui.Brush("GoldSoftBrush")
    };
}
