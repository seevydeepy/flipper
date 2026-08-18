using System.Text.RegularExpressions;

namespace Flipper.Core.Library;

public static class ScoreLabel
{
    private static readonly Regex Junk = new(
        @"public domain|creative commons|mutopia|typeset|licensed under|reference:|"
        + @"free to download|creativecommons|copyright|this sheet music|"
        + @"sheet music from www|unsaved publication",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsJunk(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        return text.Length < 3 || Junk.IsMatch(text);
    }

    public static string Title(string? extracted, string fileName)
    {
        return IsJunk(extracted) ? fileName : extracted!.Trim();
    }

    public static string Composer(string? extracted)
    {
        return IsJunk(extracted) ? string.Empty : extracted!.Trim();
    }
}
