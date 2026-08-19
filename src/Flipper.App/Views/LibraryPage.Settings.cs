using Flipper.App.Services;
using Flipper.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI;

namespace Flipper.App.Views;

public sealed partial class LibraryPage
{
    private const string KoFiUrl = "https://ko-fi.com/seevydeepy";
    private const string KoFiNormalAsset = "kofi-support.png";
    private const string KoFiHoverAsset = "kofi-support-hover.png";

    private Grid? _settingsOverlay;

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var mics = await MicrophoneCatalog.ListAsync();
        var box = new ComboBox
        {
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var mic in mics)
        {
            box.Items.Add(mic);
        }

        var current = App.Current.Settings.MicrophoneDeviceId ?? MicrophoneCatalog.SystemDefaultId;
        box.SelectedItem = mics.FirstOrDefault(item => item.Id == current) ?? mics[0];
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not MicrophoneOption option)
            {
                return;
            }

            App.Current.Settings.MicrophoneDeviceId = option.Id;
            App.Current.PersistSettings();
        };

        var scalePercent = AppSettings.SnapUiScalePercent(App.Current.Settings.UiScalePercent);
        var scaleLabel = new TextBlock
        {
            Text = $"UI scale  {scalePercent}%",
            Foreground = (Brush)Application.Current.Resources["InkBrush"]
        };
        var scaleSlider = new Slider
        {
            Minimum = 0,
            Maximum = AppSettings.UiScaleStops.Length - 1,
            StepFrequency = 1,
            TickFrequency = 1,
            SnapsTo = SliderSnapsTo.Ticks,
            TickPlacement = TickPlacement.Outside,
            Value = AppSettings.IndexOfUiScale(scalePercent)
        };
        AutomationProperties.SetName(scaleSlider, "UI scale");
        scaleSlider.ValueChanged += (_, args) =>
        {
            var index = (int)Math.Clamp(Math.Round(args.NewValue), 0, AppSettings.UiScaleStops.Length - 1);
            var next = AppSettings.UiScaleStops[index];
            scaleLabel.Text = $"UI scale  {next}%";
            if (App.Current.Settings.UiScalePercent == next)
            {
                return;
            }

            App.Current.Settings.UiScalePercent = next;
            App.Current.PersistSettings();
            App.Current.Window?.ApplyUiScale();
        };

        var panel = new StackPanel { Spacing = 10 };
        var folderPath = new TextBlock
        {
            Text = App.Current.Settings.LibraryPath ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MuteBrush"]
        };
        var folderButton = new Button
        {
            Content = "Choose Folder",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        folderButton.Click += async (_, _) =>
        {
            await ChooseFolderAsync();
            folderPath.Text = App.Current.Settings.LibraryPath ?? string.Empty;
        };
        panel.Children.Add(folderButton);
        panel.Children.Add(folderPath);
        panel.Children.Add(scaleLabel);
        panel.Children.Add(scaleSlider);
        panel.Children.Add(new TextBlock
        {
            Text = "Microphone",
            Foreground = (Brush)Application.Current.Resources["InkBrush"]
        });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "Turn: flip, turn, next, page. Back: back, previous. First page: restart, beginning. Leave: finish.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["MuteBrush"]
        });

        var status = new TextBlock
        {
            Text = "",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["InkBrush"]
        };
        var install = new Button
        {
            Content = "Install update",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        var check = new Button
        {
            Content = "Check for updates",
            VerticalAlignment = VerticalAlignment.Center
        };
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
        panel.Children.Add(status);
        panel.Children.Add(install);

        var title = CreateSettingsTitle(check, CloseSettings);
        ShowSettingsOverlay(title, panel);
    }

    private void CloseSettings()
    {
        if (_settingsOverlay?.Parent is Panel parent)
        {
            parent.Children.Remove(_settingsOverlay);
        }

        _settingsOverlay = null;
    }

    private void ShowSettingsOverlay(FrameworkElement title, FrameworkElement body)
    {
        CloseSettings();
        if (Content is not Panel root)
        {
            return;
        }

        var layout = new Grid { RowSpacing = 12 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(title);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["GoldSoftBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24),
            MinWidth = 320,
            MaxWidth = 548,
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
        body.SizeChanged += (_, _) =>
        {
            if (body.ActualWidth > 0)
            {
                title.Width = body.ActualWidth;
            }
        };

        _settingsOverlay = overlay;
        root.Children.Add(overlay);
        overlay.Focus(FocusState.Programmatic);
    }

    private static FrameworkElement CreateSettingsTitle(Button check, Action close)
    {
        var back = new Button
        {
            Style = (Style)Application.Current.Resources["QuietButton"],
            Padding = new Thickness(4),
            MinWidth = 40,
            MinHeight = 40,
            Margin = new Thickness(0, 0, 4, 0),
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
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["InkBrush"]
        };

        check.VerticalAlignment = VerticalAlignment.Center;

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(back);
        Grid.SetColumn(titleText, 1);
        title.Children.Add(titleText);
        Grid.SetColumn(check, 2);
        title.Children.Add(check);

        var kofi = CreateKofiButton();
        if (kofi is not null)
        {
            kofi.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(kofi, 3);
            title.Children.Add(kofi);
        }

        return title;
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
            Height = 40
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
