namespace Flipper.Core.Library;

public static class ScoreTrash
{
    public const string FolderName = "trash";

    public static bool IsHiddenFolder(string? relativeFolder)
    {
        if (string.IsNullOrEmpty(relativeFolder) || relativeFolder == ".")
        {
            return false;
        }

        var normalised = relativeFolder.Replace('/', '\\').Trim('\\');
        return normalised.Equals(FolderName, StringComparison.OrdinalIgnoreCase)
            || normalised.StartsWith(FolderName + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public static string UniqueDestination(string trashDirectory, string fileName)
    {
        var destination = Path.Combine(trashDirectory, fileName);
        if (!File.Exists(destination) && !Directory.Exists(destination))
        {
            return destination;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            destination = Path.Combine(trashDirectory, $"{stem} {index}{extension}");
            if (!File.Exists(destination) && !Directory.Exists(destination))
            {
                return destination;
            }
        }
    }

    public static bool TryMove(string sourcePath, string libraryRoot, out string destinationPath)
    {
        destinationPath = string.Empty;
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

        var trashDirectory = Path.Combine(libraryRoot, FolderName);
        try
        {
            Directory.CreateDirectory(trashDirectory);
            destinationPath = UniqueDestination(trashDirectory, Path.GetFileName(sourcePath));
            File.Move(sourcePath, destinationPath);
            return true;
        }
        catch (IOException)
        {
            destinationPath = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            destinationPath = string.Empty;
            return false;
        }
    }
}
