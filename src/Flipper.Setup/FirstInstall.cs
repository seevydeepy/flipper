using System.Diagnostics;
using System.Runtime.Versioning;
using Flipper.Core.Update;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
internal static class FirstInstall
{
    public static int Run()
    {
        var setupPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(setupPath))
        {
            return Fail("Could not find the setup file path.");
        }

        if (!FirstInstallPaths.TryResolveRid(setupPath, RuntimeRid.Current, out var rid))
        {
            return Fail("No installer for this PC.");
        }

        var suggested = FirstInstallPaths.DefaultTarget();
        if (!NativeFolderPicker.TryPick(suggested, out var targetDir))
        {
            return 0;
        }

        if (ProtectedInstallPath.IsProtected(targetDir))
        {
            return Fail("Choose a folder in your user profile. This install does not use administrator rights.");
        }

        if (!TryEnsureWritable(targetDir, out var writeError))
        {
            return Fail(writeError);
        }

        if (!FirstInstallPaths.TryResolveSiblingZip(setupPath, rid, out var siblingZip))
        {
            return Fail("Could not find the package.");
        }

        Console.WriteLine($"Install folder: {targetDir}");

        string zipPath;
        if (File.Exists(siblingZip))
        {
            Console.WriteLine($"Using {siblingZip}");
            zipPath = siblingZip;
        }
        else
        {
            Console.WriteLine("Downloading the Carousel package from GitHub...");
            var downloadDir = Path.Combine(Path.GetTempPath(), "CarouselSetup");
            Directory.CreateDirectory(downloadDir);
            zipPath = Path.Combine(downloadDir, $"Carousel-{rid}.zip");
            if (!TryDownloadLatestZip(rid, zipPath, out var error))
            {
                return Fail(error);
            }
        }

        Console.WriteLine("Installing...");
        if (!InPlaceInstaller.Extract(zipPath, targetDir))
        {
            return Fail("Could not extract the package.");
        }

        if (!InPlaceInstaller.TryListRelativeFiles(zipPath, out var files))
        {
            return Fail("Could not list the package files.");
        }

        InstallManifest.Write(targetDir, files);

        var installedSetup = Path.Combine(targetDir, "Carousel.Setup.exe");
        try
        {
            File.Copy(setupPath, installedSetup, overwrite: true);
        }
        catch (IOException)
        {
            return Fail("Could not copy the uninstaller.");
        }
        catch (UnauthorizedAccessException)
        {
            return Fail("Could not copy the uninstaller.");
        }

        if (!RegisteredInstall.TryWrite(targetDir, zipPath, installedSetup, out var registerError))
        {
            return Fail(registerError);
        }

        var app = Path.Combine(targetDir, "Carousel.exe");
        var message = File.Exists(app)
            ? $"Installed to {targetDir}"
            : $"Installed to {targetDir}, but Carousel.exe was not in the package.";
        Console.WriteLine(message);
        if (ExplorerHost.OpenedFromExplorer())
        {
            NativeDialog.Info(message);
            StartApp(app, targetDir);
        }

        return 0;
    }

    private static bool TryEnsureWritable(string targetDir, out string error)
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

    private static bool TryDownloadLatestZip(string rid, string dest, out string error)
    {
        try
        {
            DownloadLatestZipAsync(rid, dest).GetAwaiter().GetResult();
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

    private static async Task DownloadLatestZipAsync(string rid, string dest)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Carousel");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(GitHubReleases.LatestUrl);
        if (!GitHubReleaseParser.TryParse(json, out var release) || !ReleaseAssets.TryZipForRid(release, rid, out var zip))
        {
            throw new InvalidDataException("release has no zip");
        }

        await DownloadFileAsync(http, zip.BrowserDownloadUrl, dest);
    }

    private static async Task DownloadFileAsync(HttpClient http, string url, string dest)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        var lastMb = -1L;
        int n;
        while ((n = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            var mb = read / (1024 * 1024);
            if (mb == lastMb)
            {
                continue;
            }

            lastMb = mb;
            if (total is long size)
            {
                Console.Write($"\rDownloaded {mb} / {size / (1024 * 1024)} MB");
            }
            else
            {
                Console.Write($"\rDownloaded {mb} MB");
            }
        }

        Console.WriteLine();
    }

    private static void StartApp(string app, string targetDir)
    {
        if (!File.Exists(app))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = app,
                UseShellExecute = true,
                WorkingDirectory = targetDir
            });
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int Fail(string message)
    {
        Console.WriteLine(message);
        if (ExplorerHost.OpenedFromExplorer())
        {
            NativeDialog.Error(message);
        }

        return 4;
    }
}
