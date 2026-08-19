using System.Text.Json;

namespace Flipper.Core.Library;

public sealed class TrashRecord
{
    public string FileName { get; set; } = "";
    public string OriginalRelativePath { get; set; } = "";
    public List<string> PlaylistIds { get; set; } = new();
}

public sealed record TrashMoveResult(
    string DestinationPath,
    string OriginalRelativePath,
    string FileName,
    IReadOnlyList<string> PlaylistIds);

public static class ScoreTrash
{
    public const string FolderName = ".trash";
    public const string LegacyFolderName = "trash";
    public const string IndexFileName = ".flipper-trash.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static bool IsHiddenFolder(string? relativeFolder)
    {
        if (string.IsNullOrEmpty(relativeFolder) || relativeFolder == ".")
        {
            return false;
        }

        var normalised = relativeFolder.Replace('/', '\\').Trim('\\');
        return IsTrashName(normalised)
            || normalised.StartsWith(FolderName + "\\", StringComparison.OrdinalIgnoreCase)
            || normalised.StartsWith(LegacyFolderName + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string UniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination) && !Directory.Exists(destination))
        {
            return destination;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            destination = Path.Combine(directory, $"{stem} {index}{extension}");
            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                return destination;
            }
        }
    }

    public static string DirectoryPath(string libraryRoot) => Path.Combine(libraryRoot, FolderName);

    public static void Ensure(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return;
        }

        var dest = DirectoryPath(libraryRoot);
        var legacy = Path.Combine(libraryRoot, LegacyFolderName);
        try
        {
            if (Directory.Exists(legacy) && !Directory.Exists(dest))
            {
                Directory.Move(legacy, dest);
            }

            Directory.CreateDirectory(dest);
            HideDirectory(dest);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static IReadOnlyList<TrashRecord> LoadIndex(string libraryRoot)
    {
        var path = IndexPath(libraryRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<List<TrashRecord>>(json, JsonOptions);
            return Sanitize(loaded);
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public static bool TryGetOriginalRelative(string libraryRoot, string fileName, out string originalRelativePath)
    {
        originalRelativePath = string.Empty;
        var record = LoadIndex(libraryRoot)
            .FirstOrDefault(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (record is null || string.IsNullOrWhiteSpace(record.OriginalRelativePath))
        {
            return false;
        }

        originalRelativePath = record.OriginalRelativePath.Replace('/', '\\');
        return true;
    }

    public static bool TryMove(
        string sourcePath,
        string libraryRoot,
        IEnumerable<string>? playlistIds,
        out TrashMoveResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(libraryRoot)
            || !File.Exists(sourcePath)
            || !Directory.Exists(libraryRoot))
        {
            return false;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(libraryRoot, sourcePath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || IsHiddenFolder(Path.GetDirectoryName(relative)))
        {
            return false;
        }

        Ensure(libraryRoot);
        var trashDirectory = DirectoryPath(libraryRoot);
        try
        {
            var destination = UniqueDestination(trashDirectory, Path.GetFileName(sourcePath));
            File.Move(sourcePath, destination);
            var fileName = Path.GetFileName(destination);
            var ids = (playlistIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            UpsertIndex(libraryRoot, new TrashRecord
            {
                FileName = fileName,
                OriginalRelativePath = relative.Replace('/', '\\'),
                PlaylistIds = ids.ToList()
            });
            result = new TrashMoveResult(destination, relative.Replace('/', '\\'), fileName, ids);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryRestore(
        string libraryRoot,
        string trashFileName,
        out string restoredPath,
        out IReadOnlyList<string> playlistIds)
    {
        restoredPath = string.Empty;
        playlistIds = [];
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(trashFileName))
        {
            return false;
        }

        var source = Path.Combine(DirectoryPath(libraryRoot), trashFileName);
        if (!File.Exists(source))
        {
            return false;
        }

        var record = LoadIndex(libraryRoot)
            .FirstOrDefault(item => string.Equals(item.FileName, trashFileName, StringComparison.OrdinalIgnoreCase));
        var original = record?.OriginalRelativePath;
        if (string.IsNullOrWhiteSpace(original) || original.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(original))
        {
            original = trashFileName;
        }

        var destination = Path.Combine(libraryRoot, original.Replace('/', '\\'));
        var destDir = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destDir))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(destDir);
            destination = UniqueDestination(destDir, Path.GetFileName(destination));
            File.Move(source, destination);
            RemoveIndex(libraryRoot, trashFileName);
            restoredPath = destination;
            playlistIds = record?.PlaylistIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsTrashName(string name)
    {
        return name.Equals(FolderName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(LegacyFolderName, StringComparison.OrdinalIgnoreCase);
    }

    private static string IndexPath(string libraryRoot) => Path.Combine(DirectoryPath(libraryRoot), IndexFileName);

    private static void HideDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.Hidden) == 0)
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static List<TrashRecord> Sanitize(IEnumerable<TrashRecord>? records)
    {
        return (records ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.FileName))
            .Select(item =>
            {
                item.OriginalRelativePath ??= string.Empty;
                item.PlaylistIds ??= [];
                item.PlaylistIds = item.PlaylistIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return item;
            })
            .ToList();
    }

    private static void UpsertIndex(string libraryRoot, TrashRecord record)
    {
        var items = Sanitize(LoadIndex(libraryRoot));
        items.RemoveAll(item => string.Equals(item.FileName, record.FileName, StringComparison.OrdinalIgnoreCase));
        items.Add(record);
        SaveIndex(libraryRoot, items);
    }

    private static void RemoveIndex(string libraryRoot, string fileName)
    {
        var items = Sanitize(LoadIndex(libraryRoot));
        items.RemoveAll(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        SaveIndex(libraryRoot, items);
    }

    private static void SaveIndex(string libraryRoot, List<TrashRecord> items)
    {
        Ensure(libraryRoot);
        var path = IndexPath(libraryRoot);
        var json = JsonSerializer.Serialize(items, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
