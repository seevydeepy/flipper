using System.Numerics;
using Flipper.App.Services;
using Flipper.Core.Reader;
using Flipper.Core.Settings;
using Flipper.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Devices;
using Windows.System;
using Windows.UI;

namespace Flipper.App.Views;

public sealed partial class LibraryPage
{
    private const string KoFiUrl = "https://ko-fi.com/seevydeepy";
    private const string KoFiNormalAsset = "kofi-support.png";
    private const string KoFiHoverAsset = "kofi-support-hover.png";
    private const double SettingsMargin = 48;
    private const double SettingsNarrowMargin = 24;
    private const double SettingsMinWidth = 360;
    private const double SettingsMaxWidth = 960;
    private const double SettingsMinHeight = 280;
    private const double SettingsMaxHeight = 780;
    private const double SettingsTwoPaneWidth = 720;
    private const double SettingsNavWidth = 288;

    private static readonly (VoiceCommand Command, string Action)[] VoiceActions =
    [
        (VoiceCommand.Next, "Next page"),
        (VoiceCommand.Back, "Previous page"),
        (VoiceCommand.Restart, "First page"),
        (VoiceCommand.Finish, "Back to library")
    ];

    private const string VoiceReadyGlyph = "\uE930";
    private const string VoiceAttentionGlyph = "\uE7BA";
    private const string VoiceEmptyGlyph = "\uE946";

    private Grid? _settingsOverlay;

    private sealed record SettingsSection(string Title, string Glyph, Func<FrameworkElement> Build);

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsOverlay(
        [
            new("Library folder", "\uE8B7", CreateLibraryFolderSection),
            new("UI scale", "\uE8E9", CreateUiScaleSection),
            new("Voice turning", "\uE720", CreateVoiceSection),
            new("Wink turning", "\uE890", CreateWinkSection),
            new("Jev score identification", "\uE8EC", CreateJevSection),
            new("Updates", "\uE895", CreateUpdateSection)
        ]);
    }

    private static StackPanel CreateSettingsPage(string title, FrameworkElement? trailing, params UIElement[] content)
    {
        FrameworkElement heading = Ui.Title(title);
        if (trailing is not null)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(heading);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 1);
            row.Children.Add(trailing);
            heading = row;
        }

        var page = new StackPanel { Spacing = 16 };
        page.Children.Add(heading);
        foreach (var item in content)
        {
            page.Children.Add(item);
        }

        return page;
    }

    private FrameworkElement CreateLibraryFolderSection()
    {
        var path = Ui.Body();
        path.MaxLines = 3;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        ShowLibraryPath(path);

        var change = Ui.Button("Change", primary: true);
        AutomationProperties.SetName(change, "Change library folder");
        change.Click += async (_, _) =>
        {
            await ChooseFolderAsync();
            ShowLibraryPath(path);
        };

        return CreateSettingsPage("Library folder", null, Ui.Card(Ui.Split(path, change)));
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
        var stops = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 92,
            ItemHeight = 60,
            Margin = new Thickness(0, 0, -8, -8)
        };
        AutomationProperties.SetName(stops, "UI scale");
        foreach (var stop in AppSettings.UiScaleStops)
        {
            var option = new RadioButton
            {
                Style = Ui.Style("ChoiceRadioButton"),
                Content = $"{stop}%",
                GroupName = "SettingsUiScale",
                Margin = new Thickness(0, 0, 8, 8),
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

        return CreateSettingsPage("UI scale", null, Ui.Card(stops));
    }

    private FrameworkElement CreateJevSection()
    {
        var key = new PasswordBox
        {
            PlaceholderText = "Paste your TypeSafe API key",
            Style = Ui.Style("AppPasswordBox")
        };
        AutomationProperties.SetName(key, "Jev API key");
        var status = CreateJevMessage();
        var jobStatus = CreateJevMessage();
        var progress = new ProgressBar { Visibility = Visibility.Collapsed, Minimum = 0 };
        AutomationProperties.SetName(progress, "Jev catalogue progress");
        AutomationProperties.SetLiveSetting(jobStatus, AutomationLiveSetting.Polite);
        var save = Ui.Button("Save key");
        var remove = Ui.Button("Remove key");
        var reanalyse = Ui.Button("Reanalyse current catalogue");
        var hasKey = false;
        void ShowProgress()
        {
            var job = _automaticCatalog.JevProgress;
            reanalyse.IsEnabled = job is { IsRunning: true, WaitingForSave: true }
                || hasKey && job?.IsRunning != true;
            var action = job is { IsRunning: true, WaitingForSave: true }
                ? "Retry catalogue save" : "Reanalyse current catalogue";
            if (!Equals(reanalyse.Content, action))
            {
                reanalyse.Content = action;
                AutomationProperties.SetName(reanalyse, action);
            }

            progress.Visibility = job is { Total: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            jobStatus.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
            if (job is null) return;
            progress.Maximum = Math.Max(1, job.Total);
            progress.Value = job.Completed;
            var heading = job.Total == 0 ? "No eligible scores to reanalyse."
                : !job.IsRunning ? (job.Failed == 0 && job.ImageFailures == 0 ? "Reanalysis finished." : "Reanalysis finished with issues.")
                : job.WaitingForSave ? "Paused: could not save catalogue results. Retry the save."
                : job.Paused ? "Paused while the reader is open."
                : job.CurrentScore is null ? "Reanalysis queued…" : $"Analysing: {job.CurrentScore}";
            jobStatus.Text = $"{heading}\n{job.Completed} of {job.Total} checked · {job.Matched} with matches · "
                + $"{job.Unmatched} without a confident match · {job.Failed} failed"
                + (job.ImageChecks + job.ImageFailures > 0
                    ? $"\nPage image checks: {job.ImageChecks} completed · {job.ImageFailures} failed (text results kept)." : "")
                + (job.Skipped > 0 ? $"\n{job.Skipped} skipped (manual, legacy or not yet catalogued)." : "")
                + (job.LastIssue is not null ? $"\nLast issue: {job.LastIssue}" : "");
        }
        try
        {
            hasKey = JevApiKeyStore.Load() is not null;
            status.Text = hasKey ? "Key saved on this Windows account" : "No key saved";
        }
        catch (Exception) { status.Text = "Windows credential storage is unavailable"; }
        save.Click += (_, _) =>
        {
            try
            {
                JevApiKeyStore.Save(key.Password);
                key.Password = string.Empty;
                status.Text = "Key saved on this Windows account";
                hasKey = true;
                ShowProgress();
            }
            catch (ArgumentException) { status.Text = "Enter an API key first"; }
            catch (Exception) { status.Text = "Could not save key"; }
        };
        remove.Click += (_, _) =>
        {
            try
            {
                JevApiKeyStore.Remove();
                key.Password = string.Empty;
                status.Text = "No key saved";
                hasKey = false;
                ShowProgress();
            }
            catch (Exception) { status.Text = "Could not remove key"; }
        };
        reanalyse.Click += (_, _) =>
        {
            var root = App.Current.Settings.LibraryPath;
            if (string.IsNullOrWhiteSpace(root) || !_snapshot.RootReachable)
            {
                status.Text = "Open a library folder first";
                return;
            }
            _automaticCatalog.QueueJevReanalysis(root, _snapshot);
            ShowProgress();
        };

        var images = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Use page images for uncertain matches (experimental)",
                FontSize = Ui.BodySize,
                TextWrapping = TextWrapping.Wrap
            },
            IsChecked = App.Current.Settings.JevImageFallbackEnabled,
            MinHeight = Ui.TouchSize,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(images, "Use page images for uncertain Jev matches (experimental)");
        images.Click += (_, _) =>
        {
            App.Current.Settings.JevImageFallbackEnabled = images.IsChecked == true;
            App.Current.PersistSettings();
        };
        var info = new Button
        {
            Content = "?",
            Style = Ui.Style("RoundButton"),
            FontSize = 20,
            Flyout = new Flyout
            {
                Content = new ScrollViewer
                {
                    MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        MaxWidth = 360, TextWrapping = TextWrapping.Wrap, FontSize = Ui.BodySize,
                        Text = "Jev is optional. A saved TypeSafe API key enables matching for new or changed scores. "
                            + "It sends the filename, extracted text and PDF metadata to TypeSafe. Remove the key to use local identification only.\n\n"
                            + "Page images are off by default. If enabled, uncertain text results can trigger one extra request with images of up to the first two pages. "
                            + "This experimental check may improve matching but adds API usage. Confident text results are kept if it fails. "
                            + "The PDF file itself is never uploaded. API usage may incur charges.\n\n"
                            + "Reanalyse checks existing generated entries. It can replace or clear generated details; "
                            + "manual corrections and legacy entries are kept. Progress and results remain here when you close and reopen Settings during this app session."
                    }
                }
            }
        };
        AutomationProperties.SetName(info, "About Jev score identification");
        ToolTipService.SetToolTip(info, "About Jev");

        var buttons = new Grid { ColumnSpacing = 12 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        save.HorizontalAlignment = remove.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.Padding = remove.Padding = new Thickness(8, 10, 8, 10);
        buttons.Children.Add(save);
        Grid.SetColumn(remove, 1);
        buttons.Children.Add(remove);
        var keyCard = new StackPanel { Spacing = 12 };
        keyCard.Children.Add(key);
        keyCard.Children.Add(buttons);
        keyCard.Children.Add(status);

        reanalyse.HorizontalAlignment = HorizontalAlignment.Left;
        var catalogueCard = new StackPanel { Spacing = 12 };
        catalogueCard.Children.Add(reanalyse);
        catalogueCard.Children.Add(progress);
        catalogueCard.Children.Add(jobStatus);

        var section = CreateSettingsPage("Jev score identification", info,
            Ui.Card(keyCard), Ui.Card(images), Ui.Card(catalogueCard));
        // The service owns the job. Only its visible controls need a UI timer.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => ShowProgress();
        section.Loaded += (_, _) => { ShowProgress(); timer.Start(); };
        section.Unloaded += (_, _) => timer.Stop();
        ShowProgress();
        return section;
    }

    private static TextBlock CreateJevMessage() => Ui.Caption();

    private static ToggleSwitch CreateSettingsToggle(bool on, string name)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = on,
            OnContent = "On",
            OffContent = "Off",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    private static FrameworkElement CreateVoiceSection()
    {
        var details = new StackPanel { Spacing = 16 };
        var toggle = CreateSettingsToggle(App.Current.Settings.VoiceTurningEnabled, "Voice turning");

        var epoch = 0;
        async Task FillDetailsAsync()
        {
            var token = ++epoch;
            details.Children.Clear();
            if (!toggle.IsOn)
            {
                details.Visibility = Visibility.Collapsed;
                return;
            }

            details.Visibility = Visibility.Visible;
            var mics = await MicrophoneCatalog.ListAsync();
            if (token != epoch)
            {
                return;
            }

            var commands = CreateVoiceCommandList(mics, out var refreshCommands);
            details.Children.Add(CreateMicrophonePicker(mics, () =>
            {
                VoiceCalibrationWindow.CloseCurrent();
                refreshCommands();
            }));
            details.Children.Add(commands);
        }

        toggle.Toggled += async (_, _) =>
        {
            if (App.Current.Settings.VoiceTurningEnabled != toggle.IsOn)
            {
                App.Current.Settings.VoiceTurningEnabled = toggle.IsOn;
                App.Current.PersistSettings();
            }

            if (!toggle.IsOn) VoiceCalibrationWindow.CloseCurrent();
            await FillDetailsAsync();
        };

        _ = FillDetailsAsync();
        return CreateSettingsPage("Voice turning", Ui.Enlarge(toggle), details);
    }

    private static FrameworkElement CreateWinkSection()
    {
        var calibrated = App.Current.Settings.WinkCalibration is { IsValid: true };
        var calibrationStatus = Ui.Caption(calibrated ? "Calibrated" : string.Empty);
        calibrationStatus.Visibility = calibrated ? Visibility.Visible : Visibility.Collapsed;
        var calibrate = Ui.Button(calibrated ? "Recalibrate" : "Calibrate", primary: true);
        calibrate.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(calibrate, "Calibrate wink turning");
        var details = Ui.Card(new StackPanel { Spacing = 12, Children = { calibrationStatus, calibrate } });
        calibrate.Click += (_, _) =>
        {
            WinkCalibrationWindow.Show(profile =>
            {
                var previous = App.Current.Settings.WinkCalibration;
                App.Current.Settings.WinkCalibration = profile;
                try { App.Current.SettingsStore.Save(App.Current.Settings); }
                catch
                {
                    App.Current.Settings.WinkCalibration = previous;
                    throw;
                }
                calibrationStatus.Text = "Calibrated";
                calibrationStatus.Visibility = Visibility.Visible;
                calibrate.Content = "Recalibrate";
            });
        };
        var toggle = CreateSettingsToggle(App.Current.Settings.WinkTurningEnabled, "Wink turning");
        toggle.Toggled += (_, _) =>
        {
            App.Current.Settings.WinkTurningEnabled = toggle.IsOn;
            App.Current.PersistSettings();
            details.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (!toggle.IsOn) WinkCalibrationWindow.CloseCurrent();
        };
        details.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

        return CreateSettingsPage("Wink turning", Ui.Enlarge(toggle),
            Ui.Body("Use the camera to turn pages. Your right eye: next page. Your left eye: previous page."),
            details);
    }

    private static FrameworkElement CreateMicrophonePicker(IReadOnlyList<MicrophoneOption> mics, Action? changed = null)
    {
        var box = new ComboBox
        {
            Style = Ui.Style("AppComboBox"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var mic in mics)
        {
            box.Items.Add(mic);
        }

        var current = App.Current.Settings.MicrophoneDeviceId ?? MicrophoneCatalog.SystemDefaultId;
        box.SelectedItem = mics.FirstOrDefault(item => item.Id == current);
        AutomationProperties.SetName(box, "Microphone");
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not MicrophoneOption option)
            {
                return;
            }

            App.Current.Settings.MicrophoneDeviceId = option.Id;
            App.Current.PersistSettings();
            changed?.Invoke();
        };

        var picker = new StackPanel { Spacing = 10 };
        picker.Children.Add(Ui.Label("Microphone"));
        picker.Children.Add(box);
        return Ui.Card(picker);
    }

    private static FrameworkElement CreateVoiceCommandList(IReadOnlyList<MicrophoneOption> mics, out Action refresh)
    {
        var settings = App.Current.Settings;
        var list = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        list.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                Ui.Label("Voice commands"),
                Ui.Caption("Add and calibrate each word or sound separately.")
            }
        });
        var groups = new List<(VoiceCommand Command, string Action, StackPanel Profiles, TextBox Input, Border Status)>();

        void RefreshProfiles()
        {
            var deviceId = settings.MicrophoneDeviceId;
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                try { deviceId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default); }
                catch { deviceId = null; }
            }
            else if (!mics.Any(mic => mic.Id == deviceId))
            {
                deviceId = null;
            }

            foreach (var (command, action, profiles, input, actionStatus) in groups)
            {
                profiles.Children.Clear();
                var words = settings.VoiceCommands.Where(profile => profile.Command == command).ToList();
                actionStatus.Child = words.Any(profile => profile.MatchesDevice(deviceId))
                    ? Ui.Chip(VoiceReadyGlyph, "Ready", "SuccessBrush", "SuccessSoftBrush")
                    : words.Count > 0
                        ? Ui.Chip(VoiceAttentionGlyph, "Not calibrated", "WarningBrush", "WarningSoftBrush")
                        : Ui.Chip(VoiceEmptyGlyph, "No word yet", "MuteBrush", "SurfaceBrush");
                foreach (var profile in words)
                {
                    var ready = profile.MatchesDevice(deviceId);
                    var verb = ready ? "Recalibrate" : "Calibrate";
                    var recalibrate = Ui.Button(verb, primary: !ready);
                    AutomationProperties.SetName(recalibrate, $"{verb} {profile.Label} for {action}");
                    recalibrate.Click += (_, _) => Calibrate(profile, action);

                    var error = Ui.Caption();
                    error.Visibility = Visibility.Collapsed;
                    AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
                    var remove = Ui.Button("Remove");
                    AutomationProperties.SetName(remove, $"Remove {profile.Label} from {action}");
                    remove.Click += (_, _) =>
                    {
                        VoiceCalibrationWindow.CloseCurrent();
                        try
                        {
                            PersistVoiceCommands(settings.VoiceCommands.Where(item => item.Id != profile.Id).ToList());
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            error.Text = "Could not remove the voice command.";
                            error.Visibility = Visibility.Visible;
                            return;
                        }
                        RefreshProfiles();
                        input.Focus(FocusState.Programmatic);
                    };

                    var name = Ui.Body(profile.Label);
                    name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    var status = ready
                        ? Ui.Status(VoiceReadyGlyph, "Ready", "SuccessBrush")
                        : Ui.Status(VoiceAttentionGlyph, "Not calibrated for this microphone", "WarningBrush");
                    var text = new StackPanel { Spacing = 4, Children = { name, status } };
                    var buttons = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, Children = { recalibrate, remove }
                    };
                    profiles.Children.Add(new Border
                    {
                        BorderBrush = Ui.Brush("GoldSoftBrush"),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(0, 0, 0, 12),
                        Child = new StackPanel { Spacing = 6, Children = { Ui.Split(text, buttons, 460), error } }
                    });
                }

                profiles.Visibility = profiles.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void Calibrate(VoiceSoundProfile draft, string action, Action? onSaved = null)
        {
            VoiceCalibrationWindow.Show(settings.MicrophoneDeviceId, draft, action, settings.VoiceCommands, profile =>
            {
                var commands = settings.VoiceCommands.ToList();
                var index = commands.FindIndex(item => item.Id == profile.Id);
                if (index >= 0) commands[index] = profile;
                else commands.Add(profile);
                PersistVoiceCommands(commands);
                onSaved?.Invoke();
                RefreshProfiles();
            });
        }

        foreach (var (command, action) in VoiceActions)
        {
            var profiles = new StackPanel { Spacing = 12 };
            var input = new TextBox
            {
                Style = Ui.Style("AppTextBox"),
                PlaceholderText = "Word or label",
                MaxLength = 80,
                MinWidth = 0
            };
            var actionStatus = new Border { VerticalAlignment = VerticalAlignment.Center };
            groups.Add((command, action, profiles, input, actionStatus));
            AutomationProperties.SetName(input, $"Word or label for {action}");
            var add = Ui.Button("Add and calibrate");
            add.IsEnabled = false;
            AutomationProperties.SetName(add, $"Add and calibrate a voice command for {action}");
            input.TextChanged += (_, _) => add.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            void AddWord()
            {
                var entered = input.Text;
                var label = entered.Trim();
                if (label.Length == 0) return;
                Calibrate(new VoiceSoundProfile { Command = command, Label = label }, action, () =>
                {
                    if (input.Text == entered) input.Text = string.Empty;
                });
            }
            add.Click += (_, _) => AddWord();
            input.KeyDown += (_, args) =>
            {
                if (args.Key != VirtualKey.Enter) return;
                args.Handled = true;
                AddWord();
            };
            var heading = Ui.Label(action);
            heading.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(actionStatus, 1);
            var header = new Grid
            {
                ColumnSpacing = 12,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                Children = { heading, actionStatus }
            };
            list.Children.Add(Ui.Card(new StackPanel
            {
                Spacing = 12,
                Children = { header, profiles, Ui.Split(input, add, 420) }
            }));
        }

        refresh = RefreshProfiles;
        refresh();
        return list;
    }

    private static void PersistVoiceCommands(List<VoiceSoundProfile> commands)
    {
        var settings = App.Current.Settings;
        var previous = settings.VoiceCommands;
        settings.VoiceCommands = commands;
        try { App.Current.PersistSettings(); }
        catch
        {
            settings.VoiceCommands = previous;
            throw;
        }
    }

    private static FrameworkElement CreateUpdateSection()
    {
        var current = UpdateClient.CurrentVersion();
        var version = Ui.Label($"Current Version: {current.Major}.{current.Minor}.{current.Build}");
        var status = Ui.Body();
        status.Visibility = Visibility.Collapsed;
        void ShowStatus(string text)
        {
            status.Text = text;
            status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed
        };
        var install = Ui.Button("Install update", primary: true);
        install.Visibility = Visibility.Collapsed;
        var check = Ui.Button("Check for updates");
        UpdateOffer? offer = null;
        void HideProgress()
        {
            bar.Visibility = Visibility.Collapsed;
            bar.IsIndeterminate = true;
            bar.Value = 0;
        }

        void ShowProgress(InstallProgress value)
        {
            bar.Visibility = Visibility.Visible;
            ShowStatus(string.IsNullOrWhiteSpace(value.Message) ? "Downloading..." : value.Message);
            if (value.Total <= 0)
            {
                bar.IsIndeterminate = true;
                return;
            }

            bar.IsIndeterminate = false;
            bar.Maximum = value.Total;
            bar.Value = Math.Clamp(value.Current, 0, value.Total);
        }

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            install.Visibility = Visibility.Collapsed;
            HideProgress();
            ShowStatus("");
            offer = null;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var client = new UpdateClient(http);
                var result = await client.CheckAsync(CancellationToken.None);
                offer = result.offer;
                ShowStatus(result.status);
                install.Visibility = offer is null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (HttpRequestException)
            {
                ShowStatus("Could not check");
            }
            catch (Exception)
            {
                ShowStatus("Could not check");
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
            ShowProgress(new InstallProgress(0, 0, "Downloading..."));
            var closing = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var client = new UpdateClient(http);
                var progress = new Progress<InstallProgress>(ShowProgress);
                var files = await client.DownloadAsync(offer, progress, CancellationToken.None);
                if (files is null)
                {
                    HideProgress();
                    ShowStatus("Could not download");
                    return;
                }

                ShowProgress(new InstallProgress(0, 0, "Installing..."));
                var target = Path.GetDirectoryName(Environment.ProcessPath);
                if (!UpdateClient.StartSetup(files.Value.setupPath, files.Value.zipPath, target ?? ""))
                {
                    HideProgress();
                    ShowStatus("Could not install");
                    return;
                }

                closing = true;
                App.Current.Window?.Close();
            }
            catch (HttpRequestException)
            {
                HideProgress();
                ShowStatus("Could not download");
            }
            catch (Exception)
            {
                HideProgress();
                ShowStatus("Could not download");
            }
            finally
            {
                if (!closing)
                {
                    install.IsEnabled = true;
                    check.IsEnabled = true;
                }
            }
        };

        check.Margin = install.Margin = new Thickness(0, 0, 8, 8);
        var buttons = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, -8, -8),
            Children = { check, install }
        };
        var card = new StackPanel { Spacing = 12 };
        card.Children.Add(version);
        card.Children.Add(buttons);
        card.Children.Add(status);
        card.Children.Add(bar);
        return CreateSettingsPage("Updates", null, Ui.Card(card));
    }

    private void CloseSettings()
    {
        if (_settingsOverlay?.Parent is Panel parent)
        {
            parent.Children.Remove(_settingsOverlay);
        }

        _settingsOverlay = null;
    }

    private void ShowSettingsOverlay(IReadOnlyList<SettingsSection> sections)
    {
        CloseSettings();
        if (Content is not Panel root)
        {
            return;
        }

        var built = new FrameworkElement?[sections.Count];
        var navItems = new List<RadioButton>();
        var chevrons = new List<UIElement>();
        var selected = 0;
        var wide = true;
        var showingDetail = false;
        var syncing = false;
        var opened = false;

        var detail = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Auto
        };

        var navList = new StackPanel { Spacing = 4 };
        AutomationProperties.SetName(navList, "Settings");
        var navContent = new Grid { RowSpacing = 12 };
        navContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        navContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        navContent.Children.Add(navList);
        var kofi = CreateKofiButton();
        if (kofi is not null)
        {
            kofi.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(kofi, 1);
            navContent.Children.Add(kofi);
        }

        var navScroll = new ScrollViewer
        {
            Content = navContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };
        navScroll.SizeChanged += (_, args) => navContent.MinHeight = args.NewSize.Height;
        var navPane = new Grid
        {
            Padding = new Thickness(12),
            BorderBrush = Ui.Brush("GoldSoftBrush")
        };
        navPane.Children.Add(navScroll);

        var navColumn = new ColumnDefinition();
        var detailColumn = new ColumnDefinition();
        var body = new Grid();
        body.ColumnDefinitions.Add(navColumn);
        body.ColumnDefinitions.Add(detailColumn);
        body.Children.Add(navPane);
        Grid.SetColumn(detail, 1);
        body.Children.Add(detail);

        var back = Ui.IconButton("\uE72B", "Back");
        var header = CreateSettingsHeader(back);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(header);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var card = new Border
        {
            Background = Ui.Brush("SurfaceBrush"),
            BorderBrush = Ui.Brush("GoldSoftBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Shadow = new ThemeShadow(),
            Translation = new Vector3(0, 0, 48),
            Child = layout
        };
        card.Tapped += (_, args) => args.Handled = true;

        void ApplyMode()
        {
            var showNav = wide || !showingDetail;
            var showDetail = wide || showingDetail;
            navPane.Visibility = showNav ? Visibility.Visible : Visibility.Collapsed;
            detail.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
            navColumn.Width = wide ? new GridLength(SettingsNavWidth)
                : new GridLength(showNav ? 1 : 0, GridUnitType.Star);
            detailColumn.Width = new GridLength(showDetail ? 1 : 0, GridUnitType.Star);
            navPane.BorderThickness = new Thickness(0, 0, wide ? 1 : 0, 0);
            detail.Padding = wide ? new Thickness(28, 24, 28, 28) : new Thickness(16, 16, 16, 20);
            foreach (var chevron in chevrons)
            {
                chevron.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            }

            // On one pane the list is a menu, so no row stays marked as open.
            syncing = true;
            for (var i = 0; i < navItems.Count; i++)
            {
                navItems[i].IsChecked = wide && i == selected;
            }

            syncing = false;
        }

        void Select(int index, bool open)
        {
            selected = index;
            var section = built[index] ??= sections[index].Build();
            if (!ReferenceEquals(detail.Content, section))
            {
                detail.Content = section;
                detail.ChangeView(null, 0, null, true);
            }

            if (open && !wide)
            {
                showingDetail = true;
            }

            ApplyMode();
            if (open && !wide)
            {
                back.Focus(FocusState.Programmatic);
            }
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var index = i;
            var chevron = Ui.Icon("\uE76C", 14);
            chevrons.Add(chevron);
            var content = new Grid { ColumnSpacing = 14 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(Ui.Icon(sections[i].Glyph, 22));
            var label = new TextBlock
            {
                Text = sections[i].Title,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);
            Grid.SetColumn(chevron, 2);
            content.Children.Add(chevron);

            var item = new RadioButton
            {
                Style = Ui.Style("NavRadioButton"),
                GroupName = "SettingsSections",
                Content = content
            };
            AutomationProperties.SetName(item, sections[i].Title);
            item.Checked += (_, _) =>
            {
                if (!syncing) Select(index, open: false);
            };
            item.Click += (_, _) => Select(index, open: true);
            navItems.Add(item);
            navList.Children.Add(item);
        }

        back.Click += (_, _) =>
        {
            if (wide || !showingDetail)
            {
                CloseSettings();
                return;
            }

            showingDetail = false;
            ApplyMode();
            navItems[selected].Focus(FocusState.Programmatic);
        };

        void Fit(Size available)
        {
            if (available.Width <= 0 || available.Height <= 0)
            {
                return;
            }

            var margin = available.Width < SettingsTwoPaneWidth + SettingsMargin
                ? SettingsNarrowMargin : SettingsMargin;
            card.Width = Math.Clamp(available.Width - margin, SettingsMinWidth, SettingsMaxWidth);
            card.Height = Math.Clamp(available.Height - margin, SettingsMinHeight, SettingsMaxHeight);
            var nowWide = card.Width >= SettingsTwoPaneWidth;
            if (opened && wide && !nowWide)
            {
                // A larger UI scale can remove the second pane. Keep the open section in view.
                showingDetail = true;
            }

            wide = nowWide;
            opened = true;
            ApplyMode();
        }

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x0E, 0x1F, 0x28)),
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
        overlay.SizeChanged += (_, args) => Fit(args.NewSize);
        Fit(new Size(ActualWidth, ActualHeight));
        Select(0, open: false);

        _settingsOverlay = overlay;
        root.Children.Add(overlay);
        overlay.Focus(FocusState.Programmatic);
    }

    private static FrameworkElement CreateSettingsHeader(Button back)
    {
        AutomationProperties.SetName(back, "Back");
        var title = Ui.Title("Settings", 28, AutomationHeadingLevel.Level1);
        title.TextWrapping = TextWrapping.NoWrap;

        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(back);
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        return new Border
        {
            BorderBrush = Ui.Brush("GoldSoftBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 14, 16, 14),
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
            Height = 36
        };
        var button = new Button
        {
            Content = image,
            Style = Ui.Style("ImageButton"),
            MinHeight = Ui.TouchSize,
            HorizontalAlignment = HorizontalAlignment.Left,
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
