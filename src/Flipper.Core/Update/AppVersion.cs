namespace Flipper.Core.Update;

public static class AppVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var cut = trimmed.IndexOfAny(new[] { '+', '-' });
        if (cut >= 0)
        {
            trimmed = trimmed[..cut];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }

    public static int Compare(Version current, Version remote)
    {
        return Normalize(current).CompareTo(Normalize(remote));
    }

    public static bool IsNewer(Version current, Version remote)
    {
        return Compare(current, remote) < 0;
    }

    public static Version NextPatch(Version current)
    {
        var value = Normalize(current);
        return new Version(value.Major, value.Minor, value.Build + 1);
    }

    public static Version FromTags(IEnumerable<string> tags)
    {
        Version? highest = null;
        foreach (var tag in tags)
        {
            if (!TryParse(tag, out var parsed))
            {
                continue;
            }

            if (highest is null || parsed > highest)
            {
                highest = parsed;
            }
        }

        return highest ?? new Version(1, 0, 0);
    }

    public static Version NextRelease(IEnumerable<string> tags)
    {
        var found = false;
        foreach (var tag in tags)
        {
            if (TryParse(tag, out _))
            {
                found = true;
                break;
            }
        }

        return found ? NextPatch(FromTags(tags)) : new Version(1, 0, 0);
    }

    public static Version Running(string? informationalVersion, string? fileVersion)
    {
        if (TryParse(informationalVersion, out var info))
        {
            return info;
        }

        if (TryParse(fileVersion, out var file))
        {
            return file;
        }

        return new Version(1, 0, 0);
    }

    private static Version Normalize(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
