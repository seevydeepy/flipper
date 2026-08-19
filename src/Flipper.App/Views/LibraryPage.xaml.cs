using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Flipper.App.Services;
using Flipper.Core.Library;
using Flipper.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Flipper.App.Views;

public sealed partial class LibraryPage : Page
{
    private const double CardSlot = 192;
    private const double TrashGap = 8;
    private const double TrashZoneWidth = 60;
    private const int PreviewDecodeWidth = 180;
    private const string ScoreDragFormat = "Flipper.ScoreCanonicalPath";
    private const double PlaylistDeleteSize = 32;
    private const string KoFiUrl = "https://ko-fi.com/seevydeepy";
    private const string KoFiNormalAsset = "kofi-support.png";
    private const string KoFiHoverAsset = "kofi-support-hover.png";

    private readonly LibraryWatcher _watcher = new();
    private readonly ResettableCollection<ScoreCard> _cards = new();
    private readonly ScoreCatalogCache _catalogCache = new();
    private readonly PlaylistLibraryCache _playlistCache = new();
    private LibrarySnapshot _snapshot = new(string.Empty, Array.Empty<ScoreEntry>(), false);
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool _refreshQueued;
    private bool _scanBusy;
    private bool _scanAgain;
    private int _scanEpoch;
    private string? _scanPath;
    private string? _watchedPath;
    private string? _selectedFolder;
    private string? _selectedPlaylistId;
    private string? _armedPlaylistId;
    private bool _showFavourites;
    private bool _showTrash;
    private ScoreCard? _assignmentCard;
    private readonly List<AssignmentHit> _assignmentHits = [];
    private bool _hydrating;
    private bool _bindingFolders;
    private bool _suppressItemClick;
    private string? _openingCanonical;

    public LibraryPage()
    {
        InitializeComponent();
        ScoreGrid.ItemsSource = _cards;
        _watcher.Changed += OnWatcherChanged;
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Reload(App.Current.Settings.LibraryPath);
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hydrating = true;
        RestoreGridChrome();
        DispatcherQueue.TryEnqueue(() =>
        {
            RestoreGridChrome();
            _hydrating = false;
        });
        var path = App.Current.Settings.LibraryPath;
        if (App.Current.LastSnapshot is { } cached
            && string.Equals(cached.RootDisplayPath, path, StringComparison.OrdinalIgnoreCase))
        {
            ApplySnapshot(cached, path);
        }

        Reload(path);
    }

    private void ScoreColumn_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AlignScoreContent(e.NewSize.Width);
    }

    private void AlignScoreContent(double available)
    {
        if (available <= 0)
        {
            return;
        }

        var usable = Math.Max(0, available - TrashGap - TrashZoneWidth);
        var columns = Math.Max(1, (int)(usable / CardSlot));
        var width = Math.Min(usable, columns * CardSlot);
        if (double.IsNaN(ScoreContent.Width) || Math.Abs(ScoreContent.Width - width) > 0.5)
        {
            ScoreContent.Width = width;
        }

        if (double.IsNaN(ScoreHeader.Width) || Math.Abs(ScoreHeader.Width - width) > 0.5)
        {
            ScoreHeader.Width = width;
        }

        PositionTrashDrop(width);
    }

    private void PositionTrashDrop(double scoreWidth)
    {
        if (TrashDrop.Parent is not UIElement host)
        {
            return;
        }

        var left = ScoreContent.TransformToVisual(host).TransformPoint(new Point(scoreWidth, 0)).X + TrashGap;
        if (Math.Abs(TrashDrop.Margin.Left - left) > 0.5)
        {
            TrashDrop.Margin = new Thickness(left, 0, 0, 0);
        }

        if (double.IsNaN(TrashDrop.Width) || Math.Abs(TrashDrop.Width - TrashZoneWidth) > 0.5)
        {
            TrashDrop.Width = TrashZoneWidth;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PersistSearchIfChanged();
        ExitAssignment();
        _scanEpoch++;
        _scanAgain = false;
        _watcher.Dispose();
        _refreshTimer.Stop();
        _searchTimer.Stop();
    }

    private void OnWatcherChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_refreshQueued)
            {
                return;
            }

            _refreshQueued = true;
            _refreshTimer.Start();
        });
    }

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
            HorizontalAlignment = HorizontalAlignment.Stretch
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
        panel.Children.Add(check);
        panel.Children.Add(status);
        panel.Children.Add(install);

        var title = CreateSettingsTitle();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = panel,
            CloseButtonText = "Close",
            RequestedTheme = ElementTheme.Light
        };
        dialog.Opened += (_, _) => FitSettingsTitle(dialog, title);
        if (panel is FrameworkElement content)
        {
            content.SizeChanged += (_, _) => FitSettingsTitle(dialog, title);
        }
        await dialog.ShowAsync();
    }

    private static void FitSettingsTitle(ContentDialog dialog, object title)
    {
        if (title is FrameworkElement titleBar &&
            dialog.Content is FrameworkElement content &&
            content.ActualWidth > 0)
        {
            titleBar.Width = content.ActualWidth;
        }
    }

    private static object CreateSettingsTitle()
    {
        var titleText = new TextBlock
        {
            Text = "Settings",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["InkBrush"]
        };
        var kofi = CreateKofiButton();
        if (kofi is null)
        {
            return "Settings";
        }

        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(titleText);
        Grid.SetColumn(kofi, 1);
        title.Children.Add(kofi);
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

    private async Task ChooseFolderAsync()
    {
        var window = App.Current.Window;
        if (window is null)
        {
            return;
        }

        var picker = new FolderPicker(window.AppWindow.Id)
        {
            CommitButtonText = "Select Folder"
        };
        var result = await picker.PickSingleFolderAsync();
        if (result is null || string.IsNullOrWhiteSpace(result.Path))
        {
            return;
        }

        App.Current.UseLibraryPath(result.Path);
        _selectedFolder = null;
        _selectedPlaylistId = null;
        App.Current.Settings.SelectedPlaylistId = null;
        App.Current.LastSnapshot = null;
        ExitAssignment();
        HidePlaylistDelete();
        _snapshot = new LibrarySnapshot(result.Path, Array.Empty<ScoreEntry>(), true);
        FolderTree.RootNodes.Clear();
        _cards.Clear();
        Reload(result.Path);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => QueueSearch();

    private void QueueSearch()
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void FolderTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var mark = args.InvokedItem switch
        {
            FolderMark folder => folder,
            TreeViewNode node when node.Content is FolderMark folder => folder,
            _ => null
        };
        if (mark?.Section == true)
        {
            RestoreSelectedNode();
            return;
        }
        if (mark?.Favourites == true)
        {
            _showFavourites = true;
            _showTrash = false;
            _selectedFolder = null;
            _selectedPlaylistId = null;
        }
        else if (mark?.Trash == true)
        {
            _showFavourites = false;
            _showTrash = true;
            _selectedFolder = null;
            _selectedPlaylistId = null;
        }
        else if (mark?.PlaylistId is { } playlistId)
        {
            _showFavourites = false;
            _showTrash = false;
            _selectedFolder = null;
            _selectedPlaylistId = playlistId;
        }
        else
        {
            _showFavourites = false;
            _showTrash = false;
            _selectedFolder = FolderKey(args.InvokedItem);
            _selectedPlaylistId = null;
        }

        if (_armedPlaylistId is null
            || !string.Equals(_armedPlaylistId, mark?.PlaylistId, StringComparison.OrdinalIgnoreCase))
        {
            HidePlaylistDelete();
        }

        App.Current.Settings.ShowFavourites = _showFavourites;
        App.Current.Settings.ShowTrash = _showTrash;
        App.Current.Settings.SelectedPlaylistId = _selectedPlaylistId;
        App.Current.PersistSettings();
        ApplyFilter();
    }

    private void FolderTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        PersistFolderExpanded(args.Node, true);
    }

    private void FolderTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        PersistFolderExpanded(args.Node, false);
    }

    private void PersistFolderExpanded(TreeViewNode node, bool expanded)
    {
        if (_bindingFolders || _hydrating)
        {
            return;
        }

        if (node.Content is not FolderMark mark || string.IsNullOrEmpty(mark.Key))
        {
            return;
        }

        if (!App.Current.Settings.SetFolderExpanded(mark.Key, expanded))
        {
            return;
        }

        App.Current.PersistSettings();
    }

    private async void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "Name" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add Playlist",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Light
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var playlist = App.Current.TryCreatePlaylist(box.Text ?? string.Empty);
            if (playlist is null)
            {
                args.Cancel = true;
                return;
            }

            _showFavourites = false;
            _showTrash = false;
            _selectedFolder = null;
            _selectedPlaylistId = playlist.Id;
            App.Current.Settings.ShowFavourites = false;
            App.Current.Settings.ShowTrash = false;
            App.Current.Settings.SelectedPlaylistId = playlist.Id;
            App.Current.PersistSettings();
            BindFolders();
            ApplyFilter();
        };
        await dialog.ShowAsync();
    }

    private void FolderTree_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Completed)
        {
            return;
        }

        e.Handled = true;
        ArmPlaylistDelete(e.OriginalSource as DependencyObject);
    }

    private void FolderTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ArmPlaylistDelete(e.OriginalSource as DependencyObject);
    }

    private void ArmPlaylistDelete(DependencyObject? source)
    {
        var node = NodeFromSource(source);
        if (node?.Content is not FolderMark { PlaylistId: { } id })
        {
            HidePlaylistDelete();
            return;
        }

        _armedPlaylistId = id;
        DeletePlaylistButton.Visibility = Visibility.Visible;
        PlaylistDeleteHost.UpdateLayout();
        DeletePlaylistButton.UpdateLayout();
        PositionArmedDelete(node);
    }

    private void PositionArmedDelete(TreeViewNode node)
    {
        if (_armedPlaylistId is null || DeletePlaylistButton.Visibility != Visibility.Visible)
        {
            return;
        }

        if (FolderTree.ContainerFromNode(node) is not TreeViewItem item)
        {
            DeletePlaylistButton.Margin = new Thickness(0);
            return;
        }

        item.UpdateLayout();
        var bounds = item.TransformToVisual(PlaylistDeleteHost)
            .TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        var height = DeletePlaylistButton.ActualHeight > 1
            ? DeletePlaylistButton.ActualHeight
            : PlaylistDeleteSize;
        var top = bounds.Y + ((bounds.Height - height) / 2);
        DeletePlaylistButton.Margin = new Thickness(0, Math.Max(0, top), 0, 0);
    }

    private void HidePlaylistDelete()
    {
        _armedPlaylistId = null;
        DeletePlaylistButton.Visibility = Visibility.Collapsed;
        DeletePlaylistButton.Margin = new Thickness(0);
    }

    private async void DeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_armedPlaylistId is null)
        {
            return;
        }

        var playlist = PlaylistBook.Find(App.Current.Settings.Playlists, _armedPlaylistId);
        if (playlist is null)
        {
            HidePlaylistDelete();
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete playlist?",
            Content = playlist.Name,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Light
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var id = playlist.Id;
        App.Current.DeletePlaylist(id);
        if (string.Equals(_selectedPlaylistId, id, StringComparison.OrdinalIgnoreCase))
        {
            _selectedPlaylistId = null;
            _showFavourites = false;
            _showTrash = false;
            _selectedFolder = null;
        }

        HidePlaylistDelete();
        BindFolders();
        ApplyFilter();
    }

    private void ScoreGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (_assignmentCard is not null || e.Items.Count != 1 || e.Items[0] is not ScoreCard card)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetData(ScoreDragFormat, card.Entry.CanonicalPath);
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        ShowTrashDrop();
    }

    private void ScoreGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        HideTrashDrop();
    }

    private void ShowTrashDrop()
    {
        TrashDrop.Visibility = Visibility.Visible;
        TrashDrop.UpdateLayout();
        if (!double.IsNaN(ScoreContent.Width) && ScoreContent.Width > 0)
        {
            PositionTrashDrop(ScoreContent.Width);
        }
    }

    private void HideTrashDrop()
    {
        if (_assignmentCard is not null)
        {
            return;
        }

        TrashDrop.Visibility = Visibility.Collapsed;
    }

    private void TrashDrop_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(ScoreDragFormat)
            ? DataPackageOperation.Move
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void TrashDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideTrashDrop();
        if (!e.DataView.Contains(ScoreDragFormat))
        {
            return;
        }

        var raw = await e.DataView.GetDataAsync(ScoreDragFormat);
        if (raw is not string path || string.IsNullOrEmpty(path))
        {
            return;
        }

        var entry = _snapshot.Scores.FirstOrDefault(score =>
            string.Equals(score.CanonicalPath, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        await ApplyScoreRemovalAsync(entry);
    }

    private async Task ApplyScoreRemovalAsync(ScoreEntry entry)
    {
        if (_selectedPlaylistId is not null && !_showTrash)
        {
            App.Current.RemoveFromPlaylist(_selectedPlaylistId, entry.CanonicalPath);
            ApplyFilter();
            return;
        }

        if (_showTrash)
        {
            return;
        }

        await TrashScoreAsync(entry);
    }

    private async Task TrashScoreAsync(ScoreEntry entry)
    {
        var library = App.Current.Settings.LibraryPath;
        if (string.IsNullOrWhiteSpace(library))
        {
            ShowCannotDelete();
            return;
        }

        _snapshot = _snapshot.Without(entry.CanonicalPath);
        if (App.Current.LastSnapshot is { } snapshot)
        {
            App.Current.LastSnapshot = snapshot.Without(entry.CanonicalPath);
        }

        ApplyFilter();
        var playlistIds = PlaylistBook.IdsContaining(App.Current.Settings.Playlists, entry.CanonicalPath);
        var moved = await Task.Run(() => ScoreTrash.TryMove(entry.DisplayFullPath, library, playlistIds, out _));
        if (!moved)
        {
            ShowCannotDelete();
            Reload(library);
            return;
        }

        App.Current.TakePlaylistMembership(entry.CanonicalPath);
        App.Current.Cache.Remove(entry.CanonicalPath);
        Reload(library);
    }

    private void FolderTree_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = PlaylistNodeFromPoint(e.GetPosition(FolderTree)) is not null
            && e.DataView.Contains(ScoreDragFormat)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void FolderTree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var node = PlaylistNodeFromPoint(e.GetPosition(FolderTree));
        if (node?.Content is not FolderMark { PlaylistId: { } id } || !e.DataView.Contains(ScoreDragFormat))
        {
            return;
        }

        var raw = await e.DataView.GetDataAsync(ScoreDragFormat);
        if (raw is not string path || string.IsNullOrEmpty(path))
        {
            return;
        }

        if (!App.Current.AddToPlaylist(id, path))
        {
            return;
        }

        if (string.Equals(_selectedPlaylistId, id, StringComparison.OrdinalIgnoreCase))
        {
            ApplyFilter();
        }
    }

    private TreeViewNode? PlaylistNodeFromPoint(Point point)
    {
        var node = NodeFromPoint(point);
        return node?.Content is FolderMark { PlaylistId: not null } ? node : null;
    }

    private TreeViewNode? NodeFromPoint(Point point)
    {
        var hostPoint = FolderTree.TransformToVisual(null).TransformPoint(point);
        foreach (var element in VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, FolderTree))
        {
            var node = NodeFromSource(element);
            if (node is not null)
            {
                return node;
            }
        }

        return null;
    }

    private TreeViewNode? NodeFromSource(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is TreeViewItem item)
            {
                return FolderTree.NodeFromContainer(item);
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ScoreCard_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != HoldingState.Completed
            || sender is not FrameworkElement { Tag: ScoreCard card })
        {
            return;
        }

        e.Handled = true;
        _suppressItemClick = true;
        EnterAssignment(card);
    }

    private void ScoreCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType != PointerDeviceType.Mouse)
        {
            e.Handled = true;
            return;
        }

        if (sender is not FrameworkElement { Tag: ScoreCard card })
        {
            return;
        }

        e.Handled = true;
        _suppressItemClick = true;
        EnterAssignment(card);
    }

    private void SelectionShade_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        HandleAssignmentPoint(e.GetPosition(SelectionShade));
    }

    private void SelectionShade_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        HandleAssignmentPoint(e.GetPosition(SelectionShade));
    }

    private void SelectionShade_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAssignmentShade();
    }

    private void EnterAssignment(ScoreCard card)
    {
        _assignmentCard = card;
        HidePlaylistDelete();
        ShowTrashDrop();
        SelectionShade.Visibility = Visibility.Visible;
        SelectionShade.UpdateLayout();
        UpdateAssignmentShade();
        DispatcherQueue.TryEnqueue(UpdateAssignmentShade);
    }

    private void ExitAssignment()
    {
        if (_assignmentCard is null && SelectionShade.Visibility == Visibility.Collapsed)
        {
            return;
        }

        _assignmentCard = null;
        _assignmentHits.Clear();
        SelectionShadeSoft.Data = null;
        SelectionShadeMid.Data = null;
        SelectionShadePath.Data = null;
        SelectionShade.Visibility = Visibility.Collapsed;
        HideTrashDrop();
    }

    private void HandleAssignmentPoint(Point point)
    {
        if (_assignmentCard is null)
        {
            return;
        }

        var stay = false;
        foreach (var hit in _assignmentHits)
        {
            if (!hit.Bounds.Contains(point))
            {
                continue;
            }

            if (hit.Kind == AssignmentHitKind.Stay)
            {
                stay = true;
                continue;
            }

            _ = ApplyAssignmentHitAsync(hit);
            return;
        }

        if (!stay)
        {
            ExitAssignment();
        }
    }

    private async Task ApplyAssignmentHitAsync(AssignmentHit hit)
    {
        var card = _assignmentCard;
        if (card is null)
        {
            return;
        }

        switch (hit.Kind)
        {
            case AssignmentHitKind.Playlist when hit.PlaylistId is { } playlistId:
                App.Current.AddToPlaylist(playlistId, card.Entry.CanonicalPath);
                if (string.Equals(_selectedPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyFilter();
                }

                return;
            case AssignmentHitKind.Favourites:
                if (App.Current.AddFavourite(card.Entry.CanonicalPath))
                {
                    card.IsFavourite = true;
                    if (_showFavourites)
                    {
                        ApplyFilter();
                    }
                }

                return;
            case AssignmentHitKind.Trash:
                await ApplyScoreRemovalAsync(card.Entry);
                ExitAssignment();
                return;
            default:
                ExitAssignment();
                return;
        }
    }

    private void RetainAssignment()
    {
        if (_assignmentCard is null)
        {
            return;
        }

        var path = _assignmentCard.Entry.CanonicalPath;
        var next = _cards.FirstOrDefault(card =>
            string.Equals(card.Entry.CanonicalPath, path, StringComparison.OrdinalIgnoreCase));
        if (next is null)
        {
            ExitAssignment();
            return;
        }

        _assignmentCard = next;
        UpdateAssignmentShade();
    }

    private void UpdateAssignmentShade()
    {
        if (_assignmentCard is null || SelectionShade.Visibility != Visibility.Visible)
        {
            return;
        }

        var width = SelectionShade.ActualWidth;
        var height = SelectionShade.ActualHeight;
        if (width < 1 || height < 1)
        {
            return;
        }

        _assignmentHits.Clear();
        var holes = new List<Rect>();
        Rect? playlistSection = null;

        void AddHit(Rect rect, AssignmentHitKind kind, string? playlistId = null)
        {
            if (rect.Width < 1 || rect.Height < 1)
            {
                return;
            }

            _assignmentHits.Add(new AssignmentHit(Inflate(rect, 2), kind, playlistId));
        }

        void AddHole(Rect rect)
        {
            if (rect.Width < 1 || rect.Height < 1)
            {
                return;
            }

            holes.Add(Inflate(rect, 4));
        }

        foreach (var node in FolderTree.RootNodes)
        {
            if (node.Content is not FolderMark mark
                || FolderTree.ContainerFromNode(node) is not TreeViewItem item)
            {
                continue;
            }

            var rect = ElementRect(item, SelectionShade);
            if (mark.Favourites)
            {
                AddHole(rect);
                AddHit(rect, AssignmentHitKind.Favourites);
                continue;
            }

            if (mark.Section && mark.Name == "Playlists")
            {
                playlistSection = Union(playlistSection, rect);
                continue;
            }

            if (mark.PlaylistId is { } playlistId)
            {
                playlistSection = Union(playlistSection, rect);
                AddHit(rect, AssignmentHitKind.Playlist, playlistId);
            }
        }

        if (playlistSection is { } section)
        {
            AddHole(section);
            AddHit(section, AssignmentHitKind.Stay);
        }

        if (TrashDrop.Visibility == Visibility.Visible)
        {
            var trash = ElementRect(TrashDrop, SelectionShade);
            AddHole(trash);
            AddHit(trash, AssignmentHitKind.Trash);
        }

        if (ScoreGrid.ContainerFromItem(_assignmentCard) is GridViewItem cardItem)
        {
            var card = ElementRect(cardItem, SelectionShade);
            AddHole(card);
            AddHit(card, AssignmentHitKind.SelectedCard);
        }

        SelectionShadeSoft.Data = ShadeMask(width, height, holes, inflate: 12, radius: 24);
        SelectionShadeMid.Data = ShadeMask(width, height, holes, inflate: 7, radius: 18);
        SelectionShadePath.Data = ShadeMask(width, height, holes, inflate: 0, radius: 12);
    }

    private static Geometry ShadeMask(double width, double height, IReadOnlyList<Rect> holes, double inflate, double radius)
    {
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, width, height) });
        foreach (var hole in holes)
        {
            var rect = ClipToShade(Inflate(hole, inflate), width, height);
            if (rect.Width < 1 || rect.Height < 1)
            {
                continue;
            }

            group.Children.Add(RoundedRect(rect, radius));
        }

        return group;
    }

    private static Geometry RoundedRect(Rect rect, double radius)
    {
        var cap = Math.Min(rect.Width, rect.Height) / 2;
        radius = Math.Min(Math.Max(0, radius), cap);
        if (radius < 0.5)
        {
            return new RectangleGeometry { Rect = rect };
        }

        var left = rect.X;
        var top = rect.Y;
        var right = rect.X + rect.Width;
        var bottom = rect.Y + rect.Height;
        var size = new Size(radius, radius);
        var figure = new PathFigure
        {
            StartPoint = new Point(left + radius, top),
            IsClosed = true,
            IsFilled = true
        };
        figure.Segments.Add(new LineSegment { Point = new Point(right - radius, top) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(right, top + radius),
            Size = size,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = new Point(right, bottom - radius) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(right - radius, bottom),
            Size = size,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = new Point(left + radius, bottom) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(left, bottom - radius),
            Size = size,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = new Point(left, top + radius) });
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(left + radius, top),
            Size = size,
            SweepDirection = SweepDirection.Clockwise
        });

        var path = new PathGeometry();
        path.Figures.Add(figure);
        return path;
    }

    private static Rect ElementRect(FrameworkElement element, UIElement relativeTo)
    {
        return element.TransformToVisual(relativeTo)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    private static Rect Inflate(Rect rect, double amount)
    {
        return new Rect(rect.X - amount, rect.Y - amount, rect.Width + (amount * 2), rect.Height + (amount * 2));
    }

    private static Rect ClipToShade(Rect rect, double width, double height)
    {
        var x = Math.Max(0, rect.X);
        var y = Math.Max(0, rect.Y);
        var right = Math.Min(width, rect.X + rect.Width);
        var bottom = Math.Min(height, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Rect Union(Rect? current, Rect next)
    {
        if (current is not { } rect)
        {
            return next;
        }

        var x = Math.Min(rect.X, next.X);
        var y = Math.Min(rect.Y, next.Y);
        var right = Math.Max(rect.X + rect.Width, next.X + next.Width);
        var bottom = Math.Max(rect.Y + rect.Height, next.Y + next.Height);
        return new Rect(x, y, right - x, bottom - y);
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hydrating)
        {
            return;
        }

        if (SortBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse<SortMode>(tag, out var mode))
        {
            App.Current.Settings.Sort = mode;
            App.Current.PersistSettings();
            ApplyFilter();
        }
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        App.Current.Settings.SortReversed = !App.Current.Settings.SortReversed;
        App.Current.PersistSettings();
        SyncSortDirectionIcon();
        ApplyFilter();
    }

    private void Favourite_Click(object sender, RoutedEventArgs e)
    {
        if (_assignmentCard is not null
            || sender is not Button button
            || button.Tag is not ScoreCard card)
        {
            return;
        }

        App.Current.ToggleFavourite(card.Entry.CanonicalPath);
        card.IsFavourite = App.Current.Settings.StatsFor(card.Entry.CanonicalPath).Favourite;
        if (_showFavourites)
        {
            ApplyFilter();
        }
    }

    private void ScoreCard_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (FromFavouriteButton(e.OriginalSource) || sender is not FrameworkElement { Tag: ScoreCard card })
        {
            return;
        }

        e.Handled = true;
        OpenScore(card);
    }

    private void ScoreGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ScoreCard card)
        {
            return;
        }

        OpenScore(card);
    }

    private void OpenScore(ScoreCard card)
    {
        if (_assignmentCard is not null)
        {
            return;
        }

        if (_suppressItemClick)
        {
            _suppressItemClick = false;
            return;
        }

        if (string.Equals(_openingCanonical, card.Entry.CanonicalPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _openingCanonical = card.Entry.CanonicalPath;
        PersistSearchIfChanged();
        ErrorLabel.Visibility = Visibility.Collapsed;
        var livePath = File.Exists(card.Entry.DisplayFullPath) ? card.Entry.DisplayFullPath : null;
        var cachePath = App.Current.Cache.TryOpen(
            card.Entry.CanonicalPath,
            livePath,
            card.Entry.DisplayFullPath,
            App.Current.OpenCanonicalPath);
        if (cachePath is null)
        {
            _openingCanonical = null;
            ErrorLabel.Text = "Cannot open this score";
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        App.Current.OpenCanonicalPath = card.Entry.CanonicalPath;
        App.Current.RecordPlay(card.Entry.CanonicalPath);
        App.Current.Window?.ShowReader(card.Entry, cachePath);
    }

    private static bool FromFavouriteButton(object? source)
    {
        for (var current = source as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    private void Reload(string? libraryPath)
    {
        _refreshQueued = false;
        _scanPath = libraryPath;
        _ = ScanLoopAsync();
    }

    private async Task ScanLoopAsync()
    {
        if (_scanBusy)
        {
            _scanAgain = true;
            return;
        }

        _scanBusy = true;
        try
        {
            do
            {
                _scanAgain = false;
                var path = _scanPath;
                var epoch = ++_scanEpoch;
                var app = App.Current;
                LibrarySnapshot next;
                try
                {
                    next = await Task.Run(() => BuildSnapshot(app, path));
                }
                catch (Exception)
                {
                    continue;
                }

                if (epoch != _scanEpoch)
                {
                    continue;
                }

                if (DispatcherQueue.HasThreadAccess)
                {
                    ApplySnapshotIfCurrent(next, path, epoch);
                }
                else
                {
                    var applied = new TaskCompletionSource();
                    if (!DispatcherQueue.TryEnqueue(() =>
                    {
                        ApplySnapshotIfCurrent(next, path, epoch);
                        applied.SetResult();
                    }))
                    {
                        continue;
                    }

                    await applied.Task;
                }
            }
            while (_scanAgain);
        }
        finally
        {
            _scanBusy = false;
        }
    }

    private void ApplySnapshotIfCurrent(LibrarySnapshot next, string? path, int epoch)
    {
        if (epoch != _scanEpoch)
        {
            return;
        }

        ApplySnapshot(next, path);
    }

    private LibrarySnapshot BuildSnapshot(App app, string? libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return new LibrarySnapshot(string.Empty, Array.Empty<ScoreEntry>(), false);
        }

        var next = LibraryScanner.Scan(libraryPath, _catalogCache);
        if (!next.RootReachable)
        {
            return next;
        }

        return new LibrarySnapshot(
            next.RootDisplayPath,
            app.ApplyCanonical(next.Scores, next.RootDisplayPath),
            true);
    }

    private void ApplySnapshot(LibrarySnapshot next, string? libraryPath)
    {
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            OfflineLabel.Visibility = Visibility.Collapsed;
            FolderTree.RootNodes.Clear();
            _cards.Clear();
            _watcher.Stop();
            _watchedPath = null;
            _snapshot = next;
            App.Current.LastSnapshot = null;
            return;
        }

        if (next.RootReachable)
        {
            OfflineLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            OfflineLabel.Visibility = Visibility.Visible;
            next = CachedAsSnapshot(libraryPath);
        }

        if (!string.Equals(_watchedPath, libraryPath, StringComparison.OrdinalIgnoreCase))
        {
            _watcher.Start(libraryPath);
            _watchedPath = libraryPath;
        }

        var playlistsChanged = _playlistCache.TryRefresh(App.Current.Settings);
        if (_snapshot.SameMembership(next) && !playlistsChanged)
        {
            _snapshot = next;
            App.Current.LastSnapshot = next;
            return;
        }

        _snapshot = next;
        App.Current.LastSnapshot = next;
        BindFolders();
        ApplyFilter();
    }

    private static LibrarySnapshot CachedAsSnapshot(string libraryPath)
    {
        var entries = App.Current.Cache.ListRecent()
            .Where(entry => App.Current.Cache.HasCopy(entry.CanonicalPath))
            .Select(entry => new ScoreEntry(
                Path.GetFileNameWithoutExtension(entry.DisplayPath),
                string.Empty,
                entry.DisplayPath,
                entry.CanonicalPath,
                entry.Length,
                entry.LastWriteUtc))
            .ToArray();
        return new LibrarySnapshot(libraryPath, entries, false);
    }

    private void BindFolders()
    {
        HidePlaylistDelete();
        var previousFolder = _selectedFolder;
        var previousPlaylist = _selectedPlaylistId;
        _bindingFolders = true;
        try
        {
            FolderTree.RootNodes.Clear();
            var all = new TreeViewNode { Content = new FolderMark("All", null) };
            var favourites = new TreeViewNode { Content = new FolderMark("Favourites", null, favourites: true) };
            FolderTree.RootNodes.Add(all);
            FolderTree.RootNodes.Add(favourites);
            var playlists = App.Current.Settings.Playlists
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (playlists.Length > 0)
            {
                FolderTree.RootNodes.Add(SectionNode("Playlists"));
                foreach (var playlist in playlists)
                {
                    FolderTree.RootNodes.Add(new TreeViewNode
                    {
                        Content = new FolderMark(playlist.Name, null, playlistId: playlist.Id)
                    });
                }
            }

            var folderItems = Flipper.Core.Library.FolderTree.FromRelativeFolders(_snapshot.Folders);
            var hasRootFiles = _snapshot.Folders.Any(folder => string.IsNullOrEmpty(folder));
            if (hasRootFiles || folderItems.Count > 0)
            {
                FolderTree.RootNodes.Add(SectionNode("Folders"));
            }

            if (hasRootFiles)
            {
                FolderTree.RootNodes.Add(new TreeViewNode { Content = new FolderMark("\\", string.Empty) });
            }

            foreach (var item in folderItems)
            {
                FolderTree.RootNodes.Add(ToNode(item, defaultExpanded: true));
            }

            var trash = new TreeViewNode { Content = new FolderMark("Trash", null, trash: true) };
            FolderTree.RootNodes.Add(trash);

            TreeViewNode? match;
            if (_showTrash)
            {
                match = FindTrashNode(FolderTree.RootNodes);
            }
            else if (_showFavourites)
            {
                match = FindNode(FolderTree.RootNodes, key: null, favourites: true);
            }
            else if (previousPlaylist is not null)
            {
                match = FindPlaylistNode(FolderTree.RootNodes, previousPlaylist);
            }
            else
            {
                match = FindNode(FolderTree.RootNodes, previousFolder, favourites: false);
            }

            FolderTree.SelectedNode = match ?? all;
            ApplySelectedMark(FolderTree.SelectedNode?.Content as FolderMark);
            FolderTree.UpdateLayout();
            StyleSidebarRows();
            DispatcherQueue.TryEnqueue(StyleSidebarRows);
            RetainAssignment();
        }
        finally
        {
            _bindingFolders = false;
        }
    }

    private static TreeViewNode SectionNode(string name)
    {
        return new TreeViewNode { Content = new FolderMark(name.ToUpperInvariant(), null, section: true) };
    }

    private void RestoreSelectedNode()
    {
        TreeViewNode? match;
        if (_showTrash)
        {
            match = FindTrashNode(FolderTree.RootNodes);
        }
        else if (_showFavourites)
        {
            match = FindNode(FolderTree.RootNodes, key: null, favourites: true);
        }
        else if (_selectedPlaylistId is not null)
        {
            match = FindPlaylistNode(FolderTree.RootNodes, _selectedPlaylistId);
        }
        else
        {
            match = FindNode(FolderTree.RootNodes, _selectedFolder, favourites: false);
        }

        FolderTree.SelectedNode = match ?? FindNode(FolderTree.RootNodes, key: null, favourites: false);
    }

    private void StyleSidebarRows()
    {
        StyleSidebarRows(FolderTree.RootNodes);
    }

    private void StyleSidebarRows(IList<TreeViewNode> nodes)
    {
        var mute = (Brush)Application.Current.Resources["MuteBrush"];
        var ink = (Brush)Application.Current.Resources["InkBrush"];
        foreach (var node in nodes)
        {
            if (FolderTree.ContainerFromNode(node) is TreeViewItem item && node.Content is FolderMark mark)
            {
                if (mark.Section)
                {
                    item.MinHeight = 24;
                    item.Padding = new Thickness(0, 10, 8, 2);
                    item.Margin = new Thickness(-12, 6, 0, 0);
                    item.IsHitTestVisible = false;
                    item.IsSelected = false;
                    item.Foreground = mute;
                    item.FontFamily = new FontFamily("Felix Titling");
                    item.FontSize = 10;
                    item.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                }
                else
                {
                    item.ClearValue(TreeViewItem.MinHeightProperty);
                    item.ClearValue(TreeViewItem.PaddingProperty);
                    item.ClearValue(TreeViewItem.MarginProperty);
                    item.ClearValue(TreeViewItem.FontFamilyProperty);
                    item.ClearValue(TreeViewItem.FontSizeProperty);
                    item.ClearValue(TreeViewItem.FontWeightProperty);
                    item.IsHitTestVisible = true;
                    item.Foreground = ink;
                }
            }

            StyleSidebarRows(node.Children);
        }
    }

    private TreeViewNode ToNode(FolderItem item, bool defaultExpanded)
    {
        var node = new TreeViewNode
        {
            Content = new FolderMark(item.Name, item.Key)
        };
        foreach (var child in item.Children)
        {
            node.Children.Add(ToNode(child, defaultExpanded: false));
        }

        node.IsExpanded = item.Children.Count > 0
            && App.Current.Settings.FolderIsExpanded(item.Key, defaultExpanded);
        return node;
    }

    private static TreeViewNode? FindNode(IList<TreeViewNode> nodes, string? key, bool favourites)
    {
        foreach (var node in nodes)
        {
            if (node.Content is FolderMark mark && !mark.Section && !mark.Trash && mark.PlaylistId is null)
            {
                if (favourites && mark.Favourites)
                {
                    return node;
                }

                if (!favourites && !mark.Favourites && (FolderKey(node) == key
                    || (key is null && mark.Key is null && mark.Name == "All")))
                {
                    return node;
                }
            }

            var child = FindNode(node.Children, key, favourites);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static TreeViewNode? FindPlaylistNode(IList<TreeViewNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Content is FolderMark mark
                && string.Equals(mark.PlaylistId, id, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindPlaylistNode(node.Children, id);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static TreeViewNode? FindTrashNode(IList<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Content is FolderMark { Trash: true })
            {
                return node;
            }

            var child = FindTrashNode(node.Children);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private void ApplySelectedMark(FolderMark? mark)
    {
        if (mark?.Section == true)
        {
            return;
        }

        if (mark?.Favourites == true)
        {
            _showFavourites = true;
            _showTrash = false;
            _selectedFolder = null;
            _selectedPlaylistId = null;
            return;
        }

        if (mark?.Trash == true)
        {
            _showFavourites = false;
            _showTrash = true;
            _selectedFolder = null;
            _selectedPlaylistId = null;
            return;
        }

        if (mark?.PlaylistId is { } playlistId)
        {
            _showFavourites = false;
            _showTrash = false;
            _selectedFolder = null;
            _selectedPlaylistId = playlistId;
            return;
        }

        _showFavourites = false;
        _showTrash = false;
        _selectedFolder = mark?.Key;
        _selectedPlaylistId = null;
    }

    private static string? FolderKey(object? item)
    {
        return item switch
        {
            TreeViewNode node => FolderKey(node.Content),
            FolderMark mark => mark.Key,
            _ => null
        };
    }

    private string? CurrentFolderKey() => _selectedFolder;

    public void RefreshFilter() => ApplyFilter();

    public void ShowCannotDelete()
    {
        ErrorLabel.Text = "Cannot delete this score";
        ErrorLabel.Visibility = Visibility.Visible;
    }

    public void ForgetCommitted(string canonicalPath)
    {
        _snapshot = _snapshot.Without(canonicalPath);
        _scanEpoch++;
        ApplyFilter();
        Reload(_scanPath ?? App.Current.Settings.LibraryPath);
    }

    private void RestoreGridChrome()
    {
        var settings = App.Current.Settings;
        var query = settings.SearchQuery ?? string.Empty;
        if (SearchBox.Text != query)
        {
            SearchBox.Text = query;
        }

        SelectSort(settings.Sort);
        SyncSortDirectionIcon();
        _showFavourites = settings.ShowFavourites;
        _showTrash = settings.ShowTrash;
        _selectedPlaylistId = settings.SelectedPlaylistId;
        if (_showTrash)
        {
            _showFavourites = false;
            _selectedPlaylistId = null;
        }
        else if (_selectedPlaylistId is not null)
        {
            _showFavourites = false;
        }
    }

    private void PersistSearchIfChanged()
    {
        var query = SearchBox.Text ?? string.Empty;
        if (string.Equals(App.Current.Settings.SearchQuery, query, StringComparison.Ordinal))
        {
            return;
        }

        App.Current.Settings.SearchQuery = query;
        App.Current.PersistSettings();
    }

    private void ApplyFilter()
    {
        PersistSearchIfChanged();
        var selected = _showFavourites || _showTrash || _selectedPlaylistId is not null ? null : CurrentFolderKey();
        IReadOnlySet<string>? playlistPaths = null;
        if (!_showTrash && _selectedPlaylistId is not null)
        {
            var playlist = PlaylistBook.Find(App.Current.Settings.Playlists, _selectedPlaylistId);
            playlistPaths = playlist is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : playlist.CanonicalPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var rows = ScoreSearch.Sort(
            ScoreSearch.Filter(
                _snapshot.Scores,
                SearchBox.Text,
                selected,
                _showFavourites,
                App.Current.Settings.Scores,
                App.Current.PendingDeletes.CanonicalPaths,
                playlistPaths,
                trashOnly: _showTrash),
            App.Current.Settings.Sort,
            App.Current.Settings.Scores,
            App.Current.Settings.SortReversed);

        if (SameCardOrder(rows))
        {
            return;
        }

        var previous = new Dictionary<string, ScoreCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in _cards)
        {
            previous[card.Entry.CanonicalPath] = card;
        }

        var next = new List<ScoreCard>(rows.Count);
        foreach (var score in rows)
        {
            var favourite = App.Current.Settings.Scores.TryGetValue(score.CanonicalPath, out var stats) && stats.Favourite;
            if (previous.TryGetValue(score.CanonicalPath, out var card)
                && card.Entry.Length == score.Length
                && card.Entry.LastWriteUtc == score.LastWriteUtc
                && card.Title == score.CardTitle
                && card.Composer == score.CardComposer
                && card.ShowRestore == _showTrash)
            {
                card.IsFavourite = favourite;
                next.Add(card);
            }
            else
            {
                next.Add(new ScoreCard(score, favourite, _showTrash));
            }
        }

        _cards.ReplaceAll(next);
        RetainAssignment();
    }

    private bool SameCardOrder(IReadOnlyList<ScoreEntry> rows)
    {
        if (_cards.Count != rows.Count)
        {
            return false;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(_cards[i].Entry.CanonicalPath, rows[i].CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ScoreGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ScoreCard card)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            card.PreviewEpoch++;
            card.Preview = null;
            return;
        }

        if (card.Preview is not null || args.Phase > 0)
        {
            return;
        }

        var epoch = card.PreviewEpoch;
        args.RegisterUpdateCallback((_, _) =>
        {
            _ = LoadPreviewAsync(card, epoch);
        });
    }

    private async Task LoadPreviewAsync(ScoreCard card, int epoch)
    {
        if (card.PreviewEpoch != epoch)
        {
            return;
        }

        var thumb = ThumbnailStore.PathFor(card.Entry.CanonicalPath, card.Entry.Length, card.Entry.LastWriteUtc);
        if (!File.Exists(thumb))
        {
            var sourcePath = card.Entry.DisplayFullPath;
            var created = await Task.Run(() =>
                File.Exists(sourcePath) && PdfPageSource.TrySavePreview(sourcePath, thumb, 360));
            if (!created || card.PreviewEpoch != epoch)
            {
                return;
            }
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (card.PreviewEpoch != epoch)
            {
                return;
            }

            var image = new BitmapImage
            {
                DecodePixelWidth = PreviewDecodeWidth
            };
            image.UriSource = new Uri(thumb);
            card.Preview = image;
        });
    }

    private void SelectSort(SortMode mode)
    {
        foreach (var item in SortBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == mode.ToString())
            {
                SortBox.SelectedItem = item;
                return;
            }
        }

        SortBox.SelectedIndex = 0;
    }

    private void SyncSortDirectionIcon()
    {
        SortDirectionIcon.Glyph = App.Current.Settings.SortReversed ? "\uE74A" : "\uE74B";
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_assignmentCard is not null
            || sender is not Button button
            || button.Tag is not ScoreCard card)
        {
            return;
        }

        await RestoreScoreAsync(card.Entry);
    }

    private async Task RestoreScoreAsync(ScoreEntry entry)
    {
        var library = App.Current.Settings.LibraryPath;
        if (string.IsNullOrWhiteSpace(library))
        {
            ShowCannotRestore();
            return;
        }

        var fileName = Path.GetFileName(entry.DisplayFullPath);
        string? restoredPath = null;
        IReadOnlyList<string> playlistIds = [];
        var ok = await Task.Run(() =>
            ScoreTrash.TryRestore(library, fileName, out restoredPath, out playlistIds));
        if (!ok || string.IsNullOrEmpty(restoredPath))
        {
            ShowCannotRestore();
            Reload(library);
            return;
        }

        var relative = Path.GetRelativePath(library, restoredPath);
        var folder = Path.GetDirectoryName(relative) ?? string.Empty;
        if (folder == ".")
        {
            folder = string.Empty;
        }

        var canonical = App.Current.ApplyCanonical(new ScoreEntry(
            Path.GetFileNameWithoutExtension(restoredPath),
            folder,
            restoredPath,
            restoredPath,
            entry.Length,
            entry.LastWriteUtc)).CanonicalPath;
        App.Current.RestorePlaylistMembership(playlistIds, canonical);
        Reload(library);
    }

    private void ShowCannotRestore()
    {
        ErrorLabel.Text = "Cannot restore this score";
        ErrorLabel.Visibility = Visibility.Visible;
    }
}

public sealed class ScoreCard : INotifyPropertyChanged
{
    private BitmapImage? _preview;
    private bool _favourite;

    public ScoreCard(ScoreEntry entry, bool favourite, bool restore = false)
    {
        Entry = entry;
        Title = entry.CardTitle;
        Composer = entry.CardComposer;
        _favourite = favourite;
        ShowRestore = restore;
    }

    public ScoreEntry Entry { get; }
    public string Title { get; }
    public string Composer { get; }
    public bool ShowRestore { get; }
    public Visibility RestoreVisibility => ShowRestore ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FavouriteVisibility => ShowRestore ? Visibility.Collapsed : Visibility.Visible;
    public int PreviewEpoch { get; set; }
    public string StarGlyph => _favourite ? "\uE735" : "\uE734";
    public Brush StarBrush => new SolidColorBrush(_favourite
        ? Color.FromArgb(255, 241, 196, 15)
        : Color.FromArgb(255, 107, 124, 134));

    public bool IsFavourite
    {
        get => _favourite;
        set
        {
            if (_favourite == value)
            {
                return;
            }

            _favourite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavourite)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarBrush)));
        }
    }

    public BitmapImage? Preview
    {
        get => _preview;
        set
        {
            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class FolderMark
{
    public FolderMark(string name, string? key, bool favourites = false, string? playlistId = null, bool section = false, bool trash = false)
    {
        Name = name;
        Key = key;
        Favourites = favourites;
        PlaylistId = playlistId;
        Section = section;
        Trash = trash;
    }

    public string Name { get; }
    public string? Key { get; }
    public bool Favourites { get; }
    public string? PlaylistId { get; }
    public bool Section { get; }
    public bool Trash { get; }

    public override string ToString() => Name;
}

internal sealed class ResettableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

internal readonly record struct AssignmentHit(Rect Bounds, AssignmentHitKind Kind, string? PlaylistId);

internal enum AssignmentHitKind
{
    Stay,
    Playlist,
    Favourites,
    Trash,
    SelectedCard
}
