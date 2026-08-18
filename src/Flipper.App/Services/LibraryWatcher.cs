namespace Flipper.App.Services;

public sealed class LibraryWatcher : IDisposable
{
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(400);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _debounceCts;
    private bool _fastPoll;
    private bool _disposed;

    public event Action? Changed;

    public void Start(string path)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        _fastPoll = PathCanonicalizer.NeedsFastPoll(path);
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
            };
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Error += (_, _) => _fastPoll = true;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            _fastPoll = true;
        }

        _pollCts = new CancellationTokenSource();
        _ = PollLoop(_pollCts.Token);
    }

    public void Stop()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ScheduleNotify();

    private void ScheduleNotify()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Debounce(token);
    }

    private async Task Debounce(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, token);
            Changed?.Invoke();
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = _fastPoll ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10);
            try
            {
                await Task.Delay(delay, token);
                Changed?.Invoke();
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
