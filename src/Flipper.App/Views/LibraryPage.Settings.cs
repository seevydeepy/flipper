using Flipper.App.Services;
using Flipper.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Flipper.App.Views;

public sealed partial class LibraryPage
{
    private const string KoFiUrl = "https://ko-fi.com/seevydeepy";
    private const string KoFiNormalAsset = "kofi-support.png";
    private const string KoFiHoverAsset = "kofi-support-hover.png";
    private const double SettingsGap = 16;
    private const double SettingsCardMinWidth = 360;
    private const double SettingsCardWidth = 540;
    private const double SettingsCardMargin = 48;
    private const double SettingsCardPadding = 24;
    private const double SettingsBodyMinHeight = 200;

    private static readonly (string Action, string Words)[] VoiceCommands =
    [
        ("Next page", "flip, turn, next, page"),
        ("Previous page", "back, previous"),
        ("First page", "restart, beginning"),
        ("Back to library", "finish")
    ];

    private Grid? _settingsOverlay;

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var body = new StackPanel { Spacing = SettingsGap };
        body.Children.Add(CreateLibraryFolderSection());
        body.Children.Add(CreateSettingsRule());
        body.Children.Add(CreateUiScaleSection());
        body.Children.Add(CreateSettingsRule());
        body.Children.Add(await CreateVoiceSectionAsync());
        body.Children.Add(CreateSettingsRule());
        body.Children.Add(CreateUpdateSection());

        ShowSettingsOverlay(CreateSettingsHeader(CloseSettings), body);
    }

    private FrameworkElement CreateLibraryFolderSection()
    {
        var path = CreateSettingsValue(string.Empty);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(CreateSettingsLabel("Library folder"));
        text.Children.Add(path);
        ShowLibraryPath(path);

        var change = new Button
        {
            Content = "Change",
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(change, "Change library folder");
        change.Click += async (_, _) =>
        {
            await ChooseFolderAsync();
            ShowLibraryPath(path);
        };

        var section = new Grid { ColumnSpacing = 12 };
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        section.Children.Add(text);
        Grid.SetColumn(change, 1);
        section.Children.Add(change);
        return section;
    }

    private static void ShowLibraryPath(TextBlock path)
    {
        var chosen = App.Current.Settings.LibraryPath;
        var missing = string.IsNullOrWhiteSpace(chosen);
        path.Text = missing ? "No folder chosen" : chosen;
        path.FontStyle = missing ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        ToolTipService.SetToolTip(path, missing ? null : chosen);
    }

    private static FrameworkElement CreateUiScaleSection()
    {
        var selected = AppSettings.SnapUiScalePercent(App.Current.Settings.UiScalePercent);
        var stops = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        AutomationProperties.SetName(stops, "UI scale");
        foreach (var stop in AppSettings.UiScaleStops)
        {
            var option = new RadioButton
            {
                Content = $"{stop}%",
                GroupName = "SettingsUiScale",
                MinWidth = 0,
                IsChecked = stop == selected
            };
            option.Checked += (_, _) =>
            {
                if (App.Current.Settings.UiScalePercent == stop)
                {
                    return;
                }

                App.Current.Settings.UiScalePercent = stop;
                App.Current.PersistSettings();
                App.Current.Window?.ApplyUiScale();
            };
            stops.Children.Add(option);
        }

        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(CreateSettingsLabel("UI scale"));
        section.Children.Add(stops);
        return section;
    }

    private static async Task<FrameworkElement> CreateVoiceSectionAsync()
    {
        var mics = await MicrophoneCatalog.ListAsync();
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var mic in mics)
        {
            box.Items.Add(mic);
        }

        var current = App.Current.Settings.MicrophoneDeviceId ?? MicrophoneCatalog.SystemDefaultId;
        box.SelectedItem = mics.FirstOrDefault(item => item.Id == current) ?? mics[0];
        AutomationProperties.SetName(box, "Microphone");
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not MicrophoneOption option)
            {
                return;
            }

            App.Current.Settings.MicrophoneDeviceId = option.Id;
            App.Current.PersistSettings();
        };

        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(CreateSettingsLabel("Microphone"));
        section.Children.Add(box);
        section.Children.Add(new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                CreateSettingsLabel("Voice commands"),
                CreateVoiceCommandList()
            }
        });
        return section;
    }

    private static FrameworkElement CreateVoiceCommandList()
    {
        var list = new Grid { ColumnSpacing = 14, RowSpacing = 4 };
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < VoiceCommands.Length; row++)
        {
            list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var action = new TextBlock
            {
                Text = VoiceCommands[row].Action,
                Foreground = (Brush)Application.Current.Resources["InkBrush"]
            };
            var words = CreateSettingsValue(VoiceCommands[row].Words);
            Grid.SetRow(action, row);
            Grid.SetRow(words, row);
            Grid.SetColumn(words, 1);
            list.Children.Add(action);
            list.Children.Add(words);
        }

        return list;
    }

    private static FrameworkElement CreateUpdateSection()
    {
        var status = CreateSettingsValue(string.Empty);
        status.VerticalAlignment = VerticalAlignment.Center;
        var install = new Button
        {
            Content = "Install update",
            Visibility = Visibility.Collapsed
        };
        var check = new Button { Content = "Check for updates" };
        UpdateOffer? offer = null;
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            install.Visibility = Visibility.Collapsed;
            status.Text = "";
            offer = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var client = new UpdateClient(http);
                var result = await client.CheckAsync(CancellationToken.None);
                offer = result.offer;
                status.Text = result.status;
                install.Visibility = offer is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (HttpRequestException)
            {
                status.Text = "Could not check";
            }
            catch (Exception)
            {
                status.Text = "Could not check";
            }
            finally
            {
                check.IsEnabled = true;
            }
        };
        install.Click += async (_, _) =>
        {
            if (offer is null)
            {
                return;
            }

            install.IsEnabled = false;
            check.IsEnabled = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var client = new UpdateClient(http);
                var files = await client.DownloadAsync(offer, CancellationToken.None);
                if (files is null)
                {
                    status.Text = "Could not download";
                    return;
                }

                var target = Path.GetDirectoryName(Environment.ProcessPath);
                if (!UpdateClient.StartSetup(files.Value.setupPath, files.Value.zipPath, target ?? ""))
                {
                    status.Text = "Could not install";
                    return;
                }

                App.Current.Window?.Close();
            }
            catch (HttpRequestException)
            {
                status.Text = "Could not download";
            }
            catch (Exception)
            {
                status.Text = "Could not download";
            }
            finally
            {
                install.IsEnabled = true;
                check.IsEnabled = true;
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        buttons.Children.Add(check);
        buttons.Children.Add(install);

        var section = new Grid { ColumnSpacing = 12 };
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        section.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        section.Children.Add(buttons);
        Grid.SetColumn(status, 1);
        section.Children.Add(status);
        return section;
    }

    private static TextBlock CreateSettingsLabel(string text) => new()
    {
        Text = text,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources["InkBrush"]
    };

    private static TextBlock CreateSettingsValue(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 2,
        Foreground = (Brush)Application.Current.Resources["MuteBrush"]
    };

    private static Border CreateSettingsRule() => new()
    {
        Height = 1,
        Background = (Brush)Application.Current.Resources["GoldSoftBrush"]
    };

    private void CloseSettings()
    {
        if (_settingsOverlay?.Parent is Panel parent)
        {
            parent.Children.Remove(_settingsOverlay);
        }

        _settingsOverlay = null;
    }

    private void ShowSettingsOverlay(FrameworkElement header, FrameworkElement body)
    {
        CloseSettings();
        if (Content is not Panel root)
        {
            return;
        }

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto
        };

        var layout = new Grid { RowSpacing = SettingsGap };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(header);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["GoldSoftBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            MinWidth = SettingsCardMinWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = layout
        };
        card.Tapped += (_, args) => args.Handled = true;

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsTabStop = true
        };
        overlay.Children.Add(card);
        overlay.Tapped += (_, _) => CloseSettings();
        overlay.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape)
            {
                return;
            }

            args.Handled = true;
            CloseSettings();
        };
        overlay.SizeChanged += (_, args) => FitSettingsCard(card, header, scroll, args.NewSize);
        FitSettingsCard(card, header, scroll, new Size(ActualWidth, ActualHeight));

        _settingsOverlay = overlay;
        root.Children.Add(overlay);
        overlay.Focus(FocusState.Programmatic);
    }

    private static void FitSettingsCard(
        Border card,
        FrameworkElement header,
        ScrollViewer scroll,
        Size available)
    {
        if (available.Width > 0)
        {
            card.Width = Math.Clamp(
                available.Width - SettingsCardMargin,
                SettingsCardMinWidth,
                SettingsCardWidth);
        }

        if (available.Height <= 0)
        {
            return;
        }

        var chrome = SettingsCardMargin + (SettingsCardPadding * 2) + header.ActualHeight + SettingsGap;
        scroll.MaxHeight = Math.Max(SettingsBodyMinHeight, available.Height - chrome);
    }

    private static FrameworkElement CreateSettingsHeader(Action close)
    {
        var back = new Button
        {
            Style = (Style)Application.Current.Resources["QuietButton"],
            Padding = new Thickness(4),
            MinWidth = 40,
            MinHeight = 40,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = "\uE72B",
                FontSize = 18,
                Foreground = (Brush)Application.Current.Resources["InkBrush"]
            }
        };
        AutomationProperties.SetName(back, "Back");
        back.Click += (_, _) => close();

        var titleText = new TextBlock
        {
            Text = "Settings",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = new FontFamily("Cambria"),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["InkBrush"]
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(back);
        Grid.SetColumn(titleText, 1);
        row.Children.Add(titleText);

        var kofi = CreateKofiButton();
        if (kofi is not null)
        {
            Grid.SetColumn(kofi, 2);
            row.Children.Add(kofi);
        }

        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["GoldSoftBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, SettingsGap),
            Child = row
        };
    }

    private static Button? CreateKofiButton()
    {
        if (!TryLoadAssetImage(KoFiNormalAsset, out var normal) ||
            !TryLoadAssetImage(KoFiHoverAsset, out var hover))
        {
            return null;
        }

        var image = new Image
        {
            Source = normal,
            Stretch = Stretch.Uniform,
            Height = 32
        };
        var button = new Button
        {
            Content = image,
            Style = (Style)Application.Current.Resources["ImageButton"],
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, "Support me on Ko-fi");
        button.PointerEntered += (_, _) => image.Source = hover;
        button.PointerExited += (_, _) => image.Source = normal;
        button.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => image.Source = hover),
            true);
        button.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, _) => image.Source = button.IsPointerOver ? hover : normal),
            true);
        button.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(KoFiUrl));
        return button;
    }

    private static bool TryLoadAssetImage(string fileName, out BitmapImage image)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        if (!File.Exists(path))
        {
            image = null!;
            return false;
        }

        image = new BitmapImage(new Uri(path));
        return true;
    }
}
