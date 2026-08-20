using System.Diagnostics;
using System.IO.Compression;

namespace Flipper.Core.Update;

public static class InPlaceInstaller
{
    public const int DefaultTimeoutSec = 60;

    public static bool Extract(
        string zipPath,
        string targetDir,
        int retries = 5,
        IProgress<InstallProgress>? progress = null)
    {
        return TryExtract(zipPath, targetDir, out _, retries, progress);
    }

    public static bool TryExtract(
        string zipPath,
        string targetDir,
        out string error,
        int retries = 5,
        IProgress<InstallProgress>? progress = null)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(zipPath) || !Path.IsPathRooted(zipPath))
        {
            error = "The package path is not valid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetDir) || !Path.IsPathRooted(targetDir))
        {
            error = "The install folder is not valid.";
            return false;
        }

        if (!File.Exists(zipPath))
        {
            error = "Could not find the package.";
            return false;
        }

        Directory.CreateDirectory(targetDir);

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var totalFiles = 0;
            foreach (var entry in zip.Entries)
            {
                if (!IsDirectory(entry))
                {
                    totalFiles++;
                }
            }

            var current = 0;
            foreach (var entry in zip.Entries)
            {
                if (IsDirectory(entry))
                {
                    Directory.CreateDirectory(Path.Combine(targetDir, entry.FullName));
                    continue;
                }

                var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                var root = Path.GetFullPath(targetDir);
                if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(dest, root, StringComparison.OrdinalIgnoreCase))
                {
                    error = "The package has an unsafe path.";
                    return false;
                }

                var destDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                if (!TryExtractEntry(entry, dest, retries))
                {
                    error = $"Could not write {entry.FullName}.";
                    return false;
                }

                current++;
                progress?.Report(new InstallProgress(current, totalFiles, entry.Name));
            }

            return true;
        }
        catch (InvalidDataException)
        {
            error = "The package is not valid.";
            return false;
        }
        catch (IOException)
        {
            error = "Could not extract the package.";
            return false;
        }
    }

    private static bool IsDirectory(ZipArchiveEntry entry)
    {
        return string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/');
    }

    public static bool TryListRelativeFiles(string zipPath, out IReadOnlyList<string> files)
    {
        files = [];
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            return false;
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var listed = new List<string>();
            foreach (var entry in zip.Entries)
            {
                if (IsDirectory(entry))
                {
                    continue;
                }

                listed.Add(entry.FullName);
            }

            files = listed;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool WaitForProcess(int pid, TimeSpan timeout)
    {
        if (pid <= 0)
        {
            return true;
        }

        Process? process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
    }

    public static bool TryParseArgs(
        string[] args,
        out string target,
        out string zip,
        out int? waitPid,
        out int timeoutSec)
    {
        target = "";
        zip = "";
        waitPid = null;
        timeoutSec = DefaultTimeoutSec;

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                return false;
            }

            switch (args[i])
            {
                case "--target":
                    target = args[++i];
                    break;
                case "--zip":
                    zip = args[++i];
                    break;
                case "--wait-pid":
                    if (!int.TryParse(args[++i], out var pid))
                    {
                        return false;
                    }

                    waitPid = pid;
                    break;
                case "--timeout-sec":
                    if (!int.TryParse(args[++i], out timeoutSec) || timeoutSec <= 0)
                    {
                        return false;
                    }

                    break;
                default:
                    return false;
            }
        }

        return Path.IsPathRooted(target) && Path.IsPathRooted(zip);
    }

    private static bool TryExtractEntry(ZipArchiveEntry entry, string dest, int retries)
    {
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                entry.ExtractToFile(dest, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                if (attempt == retries)
                {
                    return false;
                }

                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == retries)
                {
                    return false;
                }

                Thread.Sleep(200);
            }
        }

        return false;
    }
}
