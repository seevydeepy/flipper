using System.Collections.ObjectModel;
using System.ComponentModel;
using Flipper.App.Services;
using Flipper.Core.Library;
using Flipper.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.UI;

namespace Flipper.App.Views;

public sealed partial class LibraryPage : Page
{
    private readonly LibraryWatcher _watcher = new();
    private readonly ObservableCollection<ScoreCard> _cards = new();
    private LibrarySnapshot _snapshot = new(string.Empty, Array.Empty<ScoreEntry>(), false);
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _refreshQueued;
    private CancellationTokenSource? _previewCts;
    private string? _watchedPath;

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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SelectSort(App.Current.Settings.Sort);
        Reload(App.Current.Settings.LibraryPath);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _previewCts?.Cancel();
        _watcher.Dispose();
        _refreshTimer.Stop();
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

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
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

        App.Current.Settings.LibraryPath = result.Path;
        App.Current.PersistSettings();
        Reload(result.Path);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse<SortMode>(tag, out var mode))
        {
            App.Current.Settings.Sort = mode;
            App.Current.PersistSettings();
            ApplyFilter();
        }
    }

    private void Favourite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not ScoreCard card)
        {
            return;
        }

        App.Current.ToggleFavourite(card.Entry.CanonicalPath);
        card.IsFavourite = App.Current.Settings.StatsFor(card.Entry.CanonicalPath).Favourite;
        if (App.Current.Settings.Sort == SortMode.Favourites)
        {
            ApplyFilter();
        }
    }

    private void ScoreGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ScoreCard card)
        {
            return;
        }

        ErrorLabel.Visibility = Visibility.Collapsed;
        var livePath = File.Exists(card.Entry.DisplayFullPath) ? card.Entry.DisplayFullPath : null;
        var cachePath = App.Current.Cache.TryOpen(
            card.Entry.CanonicalPath,
            livePath,
            card.Entry.DisplayFullPath,
            App.Current.OpenCanonicalPath);
        if (cachePath is null)
        {
            ErrorLabel.Text = "Cannot open this score";
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        App.Current.OpenCanonicalPath = card.Entry.CanonicalPath;
        App.Current.RecordPlay(card.Entry.CanonicalPath);
        App.Current.Window?.ShowReader(card.Entry, cachePath);
    }

    private void Reload(string? libraryPath)
    {
        _refreshQueued = false;
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            OfflineLabel.Visibility = Visibility.Collapsed;
            FolderList.Items.Clear();
            _cards.Clear();
            _watcher.Stop();
            _watchedPath = null;
            return;
        }

        var next = LibraryScanner.Scan(libraryPath);
        if (next.RootReachable)
        {
            next = new LibrarySnapshot(
                next.RootDisplayPath,
                next.Scores.Select(App.Current.ApplyCanonical).ToArray(),
                true);
            OfflineLabel.Visibility = Visibility.Collapsed;
            if (!string.Equals(_watchedPath, libraryPath, StringComparison.OrdinalIgnoreCase))
            {
                _watcher.Start(libraryPath);
                _watchedPath = libraryPath;
            }
        }
        else
        {
            OfflineLabel.Visibility = Visibility.Visible;
            next = CachedAsSnapshot(libraryPath);
            _watcher.Stop();
            _watchedPath = null;
        }

        if (_snapshot.SameMembership(next))
        {
            _snapshot = next;
            return;
        }

        _snapshot = next;
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
        var previous = CurrentFolderKey();
        FolderList.Items.Clear();
        FolderList.Items.Add("All");
        foreach (var folder in _snapshot.Folders)
        {
            FolderList.Items.Add(string.IsNullOrEmpty(folder) ? "\\" : folder);
        }

        if (previous is null)
        {
            FolderList.SelectedItem = "All";
        }
        else
        {
            var label = previous == string.Empty ? "\\" : previous;
            FolderList.SelectedItem = FolderList.Items.Contains(label) ? label : "All";
        }
    }

    private string? CurrentFolderKey()
    {
        return FolderList.SelectedItem switch
        {
            null or "All" => null,
            "\\" => string.Empty,
            string folder => folder,
            _ => null
        };
    }

    private void ApplyFilter()
    {
        var selected = CurrentFolderKey();
        var rows = ScoreSearch.Sort(
            ScoreSearch.Filter(_snapshot.Scores, SearchBox.Text, selected),
            App.Current.Settings.Sort,
            App.Current.Settings.Scores);

        _cards.Clear();
        foreach (var score in rows)
        {
            var favourite = App.Current.Settings.Scores.TryGetValue(score.CanonicalPath, out var stats) && stats.Favourite;
            _cards.Add(new ScoreCard(score, favourite));
        }

        QueuePreviews();
    }

    private void QueuePreviews()
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        var cards = _cards.ToArray();
        _ = LoadPreviewsAsync(cards, token);
    }

    private async Task LoadPreviewsAsync(IReadOnlyList<ScoreCard> cards, CancellationToken token)
    {
        foreach (var card in cards)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var sourcePath = File.Exists(card.Entry.DisplayFullPath)
                ? card.Entry.DisplayFullPath
                : null;
            if (sourcePath is null)
            {
                continue;
            }

            var thumb = ThumbnailStore.PathFor(card.Entry.CanonicalPath, card.Entry.Length, card.Entry.LastWriteUtc);
            if (!File.Exists(thumb))
            {
                await Task.Run(() => PdfPageSource.TrySavePreview(sourcePath, thumb, 360), token);
            }

            if (!File.Exists(thumb) || token.IsCancellationRequested)
            {
                continue;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var image = new BitmapImage();
                image.UriSource = new Uri(thumb);
                card.Preview = image;
            });
        }
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
}

public sealed class ScoreCard : INotifyPropertyChanged
{
    private BitmapImage? _preview;
    private bool _favourite;

    public ScoreCard(ScoreEntry entry, bool favourite)
    {
        Entry = entry;
        Title = entry.DisplayName;
        Folder = string.IsNullOrEmpty(entry.RelativeFolder) ? "\\" : entry.RelativeFolder;
        _favourite = favourite;
    }

    public ScoreEntry Entry { get; }
    public string Title { get; }
    public string Folder { get; }
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
