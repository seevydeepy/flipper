using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Flipper.Core.Update;

namespace Flipper.Setup;

internal sealed record InstallStatus(string Message, int? Percent, bool IsIssue = false, bool Log = false);

internal sealed class InstallOutcome
{
    public required bool Success { get; init; }
    public bool HasIssues { get; init; }
    public string Message { get; init; } = "";
    public string TargetDir { get; init; } = "";
    public string AppPath { get; init; } = "";

    public static InstallOutcome Ok(string targetDir, string appPath, bool hasIssues, string message)
    {
        return new InstallOutcome
        {
            Success = true,
            HasIssues = hasIssues,
            Message = message,
            TargetDir = targetDir,
            AppPath = appPath
        };
    }

    public static InstallOutcome Fail(string message)
    {
        return new InstallOutcome
        {
            Success = false,
            Message = message
        };
    }
}

[SupportedOSPlatform("windows")]
internal static class InstallSession
{
    public const string CloseCarouselMessage = "Close Carousel before installing.";

    public static bool IsCarouselRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("Carousel");
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static bool TryValidateTarget(string targetDir, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            error = "Enter an install folder.";
            return false;
        }

        var trimmed = targetDir.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            error = "Enter a full folder path.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (ArgumentException)
        {
            error = "The install folder is not valid.";
            return false;
        }
        catch (NotSupportedException)
        {
            error = "The install folder is not valid.";
            return false;
        }
        catch (PathTooLongException)
        {
            error = "The install folder is not valid.";
            return false;
        }

        if (ProtectedInstallPath.IsProtected(fullPath))
        {
            error = "Choose a folder in your user profile. This install does not use administrator rights.";
            return false;
        }

        return TryEnsureWritable(fullPath, out error);
    }

    public static InstallOutcome Run(string targetDir, IProgress<InstallStatus> progress)
    {
        if (IsCarouselRunning())
        {
            return Fail(progress, CloseCarouselMessage);
        }

        var setupPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(setupPath))
        {
            return Fail(progress, "Could not find the setup file path.");
        }

        if (!FirstInstallPaths.TryResolveRid(setupPath, RuntimeRid.Current, out var rid))
        {
            return Fail(progress, "No installer for this PC.");
        }

        if (!TryValidateTarget(targetDir, out var fullTarget, out var validateError))
        {
            return Fail(progress, validateError);
        }

        Step(progress, "Checking the install folder...", 5);
        if (!FirstInstallPaths.TryResolveSiblingZip(setupPath, rid, out var siblingZip))
        {
            return Fail(progress, "Could not find the package.");
        }

        string zipPath;
        if (File.Exists(siblingZip))
        {
            Step(progress, $"Using {siblingZip}", 10);
            zipPath = siblingZip;
        }
        else
        {
            Step(progress, "Downloading the Carousel package from GitHub...", null);
            var downloadDir = Path.Combine(Path.GetTempPath(), "CarouselSetup");
            Directory.CreateDirectory(downloadDir);
            zipPath = Path.Combine(downloadDir, $"Carousel-{rid}.zip");
            if (!TryDownloadLatestZip(rid, zipPath, progress, out var downloadError))
            {
                return Fail(progress, downloadError);
            }
        }

        Step(progress, "Extracting files...", 15);
        var extractProgress = new ExtractProgress(progress);
        if (!InPlaceInstaller.TryExtract(zipPath, fullTarget, out var extractError, progress: extractProgress))
        {
            return Fail(progress, extractError);
        }

        Step(progress, "Writing the install record...", 80);
        if (!InPlaceInstaller.TryListRelativeFiles(zipPath, out var files))
        {
            return Fail(progress, "Could not list the package files.");
        }

        InstallManifest.Write(fullTarget, files);

        var installedSetup = Path.Combine(fullTarget, "Carousel.Setup.exe");
        try
        {
            File.Copy(setupPath, installedSetup, overwrite: true);
        }
        catch (IOException)
        {
            return Fail(progress, "Could not copy the uninstaller.");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(progress, "Could not copy the uninstaller.");
        }

        Step(progress, "Registering with Windows...", 90);
        if (!RegisteredInstall.TryWrite(fullTarget, zipPath, installedSetup, out var registerError))
        {
            return Fail(progress, registerError);
        }

        var app = Path.Combine(fullTarget, "Carousel.exe");
        var issues = new List<string>();
        if (!File.Exists(app))
        {
            issues.Add("Carousel.exe was not in the package.");
        }

        Step(progress, "Adding the Start Menu shortcut...", 95);
        if (File.Exists(app) && !StartMenuShortcut.TryCreate(app, fullTarget, out var shortcutError))
        {
            issues.Add(shortcutError);
        }
        else if (!File.Exists(app))
        {
            issues.Add("Could not create the Start Menu shortcut.");
        }

        foreach (var issue in issues)
        {
            progress.Report(new InstallStatus(issue, 100, IsIssue: true, Log: true));
        }

        if (issues.Count > 0)
        {
            var message = $"Installed to {fullTarget} with issues.";
            Step(progress, message, 100);
            return InstallOutcome.Ok(fullTarget, app, hasIssues: true, message);
        }

        var done = $"Installed to {fullTarget}";
        Step(progress, "Completed successfully.", 100);
        return InstallOutcome.Ok(fullTarget, app, hasIssues: false, done);
    }

    public static bool TryEnsureWritable(string targetDir, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(targetDir);
            var probe = Path.Combine(targetDir, ".carousel-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (IOException)
        {
            error = "Could not write to that folder.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Could not write to that folder.";
            return false;
        }
    }

    private static InstallOutcome Fail(IProgress<InstallStatus> progress, string message)
    {
        progress.Report(new InstallStatus(message, null, IsIssue: true, Log: true));
        return InstallOutcome.Fail(message);
    }

    private static void Step(IProgress<InstallStatus> progress, string message, int? percent)
    {
        progress.Report(new InstallStatus(message, percent, Log: true));
    }

    private static bool TryDownloadLatestZip(string rid, string dest, IProgress<InstallStatus> progress, out string error)
    {
        try
        {
            DownloadLatestZipAsync(rid, dest, progress).GetAwaiter().GetResult();
            error = "";
            return true;
        }
        catch (HttpRequestException)
        {
            error = "Could not download the package.";
            return false;
        }
        catch (IOException)
        {
            error = "Could not save the package.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "Could not save the package.";
            return false;
        }
        catch (InvalidDataException)
        {
            error = "The GitHub release has no package for this PC.";
            return false;
        }
        catch (TaskCanceledException)
        {
            error = "The package download timed out.";
            return false;
        }
    }

    private static async Task DownloadLatestZipAsync(string rid, string dest, IProgress<InstallStatus> progress)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Carousel");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(GitHubReleases.LatestUrl);
        if (!GitHubReleaseParser.TryParse(json, out var release) || !ReleaseAssets.TryZipForRid(release, rid, out var zip))
        {
            throw new InvalidDataException("release has no zip");
        }

        await DownloadFileAsync(http, zip.BrowserDownloadUrl, dest, progress);
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string dest, IProgress<InstallStatus> progress)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        var lastPercent = -1;
        int n;
        while ((n = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total is long size && size > 0)
            {
                var percent = (int)(read * 100 / size);
                if (percent == lastPercent)
                {
                    continue;
                }

                lastPercent = percent;
                progress.Report(new InstallStatus($"Downloading package... {percent}%", percent));
            }
            else
            {
                var mb = read / (1024 * 1024);
                progress.Report(new InstallStatus($"Downloading package... {mb} MB", null));
            }
        }
    }

    private sealed class ExtractProgress : IProgress<InstallProgress>
    {
        private readonly IProgress<InstallStatus> _inner;

        public ExtractProgress(IProgress<InstallStatus> inner)
        {
            _inner = inner;
        }

        public void Report(InstallProgress value)
        {
            var percent = value.Total <= 0 ? 80 : 15 + (int)((long)value.Current * 65 / value.Total);
            _inner.Report(new InstallStatus($"Extracting {value.Message}", percent));
        }
    }
}
