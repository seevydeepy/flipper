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
using Windows.UI;

namespace Flipper.App.Views;

public sealed partial class LibraryPage : Page
{
    private const double CardSlot = 192;
    private const int PreviewDecodeWidth = 180;
    private const string ScoreDragFormat = "Flipper.ScoreCanonicalPath";
    private const double PlaylistDeleteSize = 32;

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

        var columns = Math.Max(1, (int)(available / CardSlot));
        var width = Math.Min(available, columns * CardSlot);
        if (double.IsNaN(ScoreContent.Width) || Math.Abs(ScoreContent.Width - width) > 0.5)
        {
            ScoreContent.Width = width;
        }

        if (double.IsNaN(ScoreHeader.Width) || Math.Abs(ScoreHeader.Width - width) > 0.5)
        {
            ScoreHeader.Width = width;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PersistSearchIfChanged();
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

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Settings",
            Content = panel,
            CloseButtonText = "Close",
            RequestedTheme = ElementTheme.Light
        };
        await dialog.ShowAsync();
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
        if (e.Items.Count != 1 || e.Items[0] is not ScoreCard card)
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
    }

    private void HideTrashDrop()
    {
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
        if (sender is not Button button || button.Tag is not ScoreCard card)
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
        }
        finally
        {
            _bindingFolders = false;
        }
    }

    private static TreeViewNode SectionNode(string name)
    {
        return new TreeViewNode { Content = new FolderMark(name, null, section: true) };
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
                    item.MinHeight = 32;
                    item.IsHitTestVisible = false;
                    item.IsSelected = false;
                    item.Foreground = mute;
                    item.FontSize = 12;
                    item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                }
                else
                {
                    item.MinHeight = 48;
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
        if (sender is not Button button || button.Tag is not ScoreCard card)
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
