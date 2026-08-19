using System.Diagnostics;
using System.Reflection;
using Flipper.Core.Update;

namespace Flipper.App.Services;

public sealed class UpdateOffer
{
    public required Version Version { get; init; }
    public required string ZipUrl { get; init; }
    public required string SetupUrl { get; init; }
}

public sealed class UpdateClient
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/seevydeepy/flipper/releases/latest";

    private readonly HttpClient _http;

    public UpdateClient(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Carousel");
        }

        if (!_http.DefaultRequestHeaders.Accept.Any())
        {
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
    }

    public static Version CurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateClient).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var file = assembly.GetName().Version?.ToString();
        return AppVersion.Running(info, file);
    }

    public async Task<(UpdateOffer? offer, string status)> CheckAsync(CancellationToken cancellationToken)
    {
        var rid = RuntimeRid.Current;
        if (rid is null)
        {
            return (null, "No installer for this PC");
        }

        string json;
        try
        {
            json = await _http.GetStringAsync(LatestReleaseUrl, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return (null, "Could not check");
        }
        catch (TaskCanceledException)
        {
            return (null, "Could not check");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "Could not check");
        }

        if (!GitHubReleaseParser.TryParse(json, out var release) || !AppVersion.TryParse(release.TagName, out var remote))
        {
            return (null, "Could not check");
        }

        if (!AppVersion.IsNewer(CurrentVersion(), remote))
        {
            return (null, "Up to date");
        }

        if (!ReleaseAssets.TryForRid(release, rid, out var assets))
        {
            return (null, "No installer for this PC");
        }

        return (new UpdateOffer
        {
            Version = remote,
            ZipUrl = assets.Zip.BrowserDownloadUrl,
            SetupUrl = assets.Setup.BrowserDownloadUrl
        }, $"Version {remote.Major}.{remote.Minor}.{remote.Build} is available");
    }

    public async Task<(string setupPath, string zipPath)?> DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "CarouselUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var setupPath = Path.Combine(folder, "Carousel.Setup.exe");
        var zipPath = Path.Combine(folder, "payload.zip");
        try
        {
            await DownloadFileAsync(offer.SetupUrl, setupPath, cancellationToken);
            await DownloadFileAsync(offer.ZipUrl, zipPath, cancellationToken);
            return (setupPath, zipPath);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool StartSetup(string setupPath, string zipPath, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return false;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = $"--target \"{targetDir}\" --zip \"{zipPath}\" --wait-pid {Environment.ProcessId}",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? targetDir
            };
            return Process.Start(start) is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task DownloadFileAsync(string url, string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        await input.CopyToAsync(output, cancellationToken);
    }
}
