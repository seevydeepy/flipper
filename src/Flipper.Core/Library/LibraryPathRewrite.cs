using Flipper.Core.Settings;

namespace Flipper.Core.Library;

public static class LibraryPathRewrite
{
    public static bool RewriteRootFolder(AppSettings settings, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        var changed = RewriteScoreKeys(settings, oldName, newName);
        changed |= RewritePlaylists(settings, oldName, newName);
        changed |= RewriteLastScore(settings, oldName, newName);
        changed |= RewriteFolderExpanded(settings, oldName, newName);
        return changed;
    }

    public static bool RewriteCatalogKeys(
        IDictionary<string, ScoreFacts> catalog,
        string oldName,
        string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        var replacements = new List<(string Old, string Next)>();
        foreach (var key in catalog.Keys)
        {
            if (TryRewriteRelative(key, oldName, newName, out var next) && next != key)
            {
                replacements.Add((key, next));
            }
        }

        if (replacements.Count == 0)
        {
            return false;
        }

        foreach (var (oldKey, next) in replacements)
        {
            var facts = catalog[oldKey];
            catalog.Remove(oldKey);
            if (!catalog.ContainsKey(next))
            {
                catalog[next] = facts;
            }
        }

        return true;
    }

    public static bool TryRewriteRelative(string path, string oldName, string newName, out string rewritten)
    {
        rewritten = path;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var slash = path.Contains('/', StringComparison.Ordinal);
        var parts = path.Split(['\\', '/'], StringSplitOptions.None);
        var lastFolder = parts.Length == 1 ? 1 : Math.Max(0, parts.Length - 1);
        var changed = false;
        for (var index = 0; index < lastFolder; index++)
        {
            if (!parts[index].Equals(oldName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parts[index] = newName;
            changed = true;
            break;
        }

        if (!changed)
        {
            return false;
        }

        rewritten = string.Join(slash ? "/" : "\\", parts);
        return true;
    }

    public static bool TryRewriteUnderRoot(
        string path,
        string libraryRoot,
        string oldName,
        string newName,
        out string rewritten)
    {
        rewritten = path;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(libraryRoot))
        {
            return false;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(libraryRoot, path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }

        if (!TryRewriteRelative(relative, oldName, newName, out var nextRelative) || nextRelative == relative)
        {
            return false;
        }

        rewritten = Path.Combine(libraryRoot, nextRelative);
        return true;
    }

    private static bool RewriteScoreKeys(AppSettings settings, string oldName, string newName)
    {
        var replacements = new List<(string Old, string Next)>();
        foreach (var key in settings.Scores.Keys)
        {
            if (TryRewritePath(key, settings.LibraryPath, oldName, newName, out var next))
            {
                replacements.Add((key, next));
            }
        }

        if (replacements.Count == 0)
        {
            return false;
        }

        foreach (var (oldKey, next) in replacements)
        {
            var stats = settings.Scores[oldKey];
            settings.Scores.Remove(oldKey);
            if (!settings.Scores.ContainsKey(next))
            {
                settings.Scores[next] = stats;
            }
        }

        return true;
    }

    private static bool RewritePlaylists(AppSettings settings, string oldName, string newName)
    {
        var changed = false;
        foreach (var playlist in settings.Playlists)
        {
            for (var index = 0; index < playlist.CanonicalPaths.Count; index++)
            {
                if (!TryRewritePath(playlist.CanonicalPaths[index], settings.LibraryPath, oldName, newName, out var next))
                {
                    continue;
                }

                playlist.CanonicalPaths[index] = next;
                changed = true;
            }
        }

        return changed;
    }

    private static bool RewriteLastScore(AppSettings settings, string oldName, string newName)
    {
        if (settings.LastScoreCanonicalPath is null)
        {
            return false;
        }

        if (!TryRewritePath(settings.LastScoreCanonicalPath, settings.LibraryPath, oldName, newName, out var next))
        {
            return false;
        }

        settings.LastScoreCanonicalPath = next;
        return true;
    }

    private static bool RewriteFolderExpanded(AppSettings settings, string oldName, string newName)
    {
        var replacements = new List<(string Old, string Next)>();
        foreach (var key in settings.FolderExpanded.Keys)
        {
            if (TryRewriteRelative(key, oldName, newName, out var next) && next != key)
            {
                replacements.Add((key, next));
            }
        }

        if (replacements.Count == 0)
        {
            return false;
        }

        foreach (var (oldKey, next) in replacements)
        {
            var expanded = settings.FolderExpanded[oldKey];
            settings.FolderExpanded.Remove(oldKey);
            if (!settings.FolderExpanded.ContainsKey(next))
            {
                settings.FolderExpanded[next] = expanded;
            }
        }

        return true;
    }

    private static bool TryRewritePath(
        string path,
        string? libraryRoot,
        string oldName,
        string newName,
        out string rewritten)
    {
        if (!string.IsNullOrEmpty(libraryRoot)
            && TryRewriteUnderRoot(path, libraryRoot, oldName, newName, out rewritten))
        {
            return true;
        }

        return TryRewriteRelative(path, oldName, newName, out rewritten) && rewritten != path;
    }
}
