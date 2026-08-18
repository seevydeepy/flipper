using Flipper.App.Services;
using Flipper.Core.Cache;
using Flipper.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Flipper.App.Views;

public sealed partial class LibraryPage : Page
{
    private readonly LibraryWatcher _watcher = new();
    private LibrarySnapshot _snapshot = new(string.Empty, Array.Empty<ScoreEntry>(), false);
    private string? _selectedFolder = string.Empty;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _refreshQueued;

    public LibraryPage()
    {
        InitializeComponent();
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
        Reload(App.Current.Settings.LibraryPath);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
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

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFolder = FolderList.SelectedItem as string;
        if (_selectedFolder == "\\")
        {
            _selectedFolder = string.Empty;
        }
        ApplyFilter();
    }

    private async void ScoreList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ScoreRow row)
        {
            return;
        }

        ErrorLabel.Visibility = Visibility.Collapsed;
        var livePath = File.Exists(row.Entry.DisplayFullPath) ? row.Entry.DisplayFullPath : null;
        var cachePath = App.Current.Cache.TryOpen(
            row.Entry.CanonicalPath,
            livePath,
            row.Entry.DisplayFullPath,
            App.Current.OpenCanonicalPath);
        if (cachePath is null)
        {
            ErrorLabel.Text = "Cannot open this score";
            ErrorLabel.Visibility = Visibility.Visible;
            return;
        }

        App.Current.OpenCanonicalPath = row.Entry.CanonicalPath;
        App.Current.Settings.LastScoreCanonicalPath = row.Entry.CanonicalPath;
        App.Current.PersistSettings();
        App.Current.Window?.ShowReader(row.Entry, cachePath);
        await Task.CompletedTask;
    }

    private void Reload(string? libraryPath)
    {
        _refreshQueued = false;
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            SearchBox.Visibility = Visibility.Collapsed;
            OfflineLabel.Visibility = Visibility.Collapsed;
            FolderList.Items.Clear();
            ScoreList.Items.Clear();
            _watcher.Stop();
            return;
        }

        SearchBox.Visibility = Visibility.Visible;
        _snapshot = LibraryScanner.Scan(libraryPath);
        if (_snapshot.RootReachable)
        {
            OfflineLabel.Visibility = Visibility.Collapsed;
            _snapshot = new LibrarySnapshot(
                _snapshot.RootDisplayPath,
                _snapshot.Scores.Select(App.Current.ApplyCanonical).ToArray(),
                true);
            _watcher.Start(libraryPath);
        }
        else
        {
            OfflineLabel.Visibility = Visibility.Visible;
            _snapshot = CachedAsSnapshot(libraryPath);
            _watcher.Stop();
        }

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
        var previous = _selectedFolder;
        FolderList.Items.Clear();
        foreach (var folder in _snapshot.Folders)
        {
            FolderList.Items.Add(string.IsNullOrEmpty(folder) ? "\\" : folder);
        }

        var selectedLabel = string.IsNullOrEmpty(previous) ? "\\" : previous;
        if (previous is not null && FolderList.Items.Contains(selectedLabel))
        {
            FolderList.SelectedItem = selectedLabel;
        }
        else if (FolderList.Items.Count > 0)
        {
            FolderList.SelectedIndex = 0;
        }
    }

    private void ApplyFilter()
    {
        var selectedLabel = FolderList.SelectedItem as string;
        var selected = selectedLabel == "\\" ? string.Empty : selectedLabel ?? string.Empty;
        var rows = ScoreSearch.Filter(_snapshot.Scores, SearchBox.Text, selected)
            .Select(score => new ScoreRow(score))
            .ToArray();
        ScoreList.Items.Clear();
        foreach (var row in rows)
        {
            ScoreList.Items.Add(row);
        }
    }
}

public sealed class ScoreRow
{
    public ScoreRow(ScoreEntry entry)
    {
        Entry = entry;
        Title = string.IsNullOrEmpty(entry.RelativeFolder)
            ? entry.DisplayName
            : $"{entry.DisplayName}  {entry.RelativeFolder}";
    }

    public ScoreEntry Entry { get; }
    public string Title { get; }
    public override string ToString() => Title;
}
