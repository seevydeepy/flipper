using System.Text;
using System.Text.RegularExpressions;

namespace Flipper.Core.Library;

public static class ScoreLabel
{
    private static readonly Regex Junk = new(
        @"public domain|creative commons|mutopia|typeset|licensed under|reference:|"
        + @"free to download|creativecommons|copyright|this sheet music|"
        + @"sheet music from www|unsaved publication",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingParen = new(
        @"^(.*[^\s(])\s*\(([^()]*)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WholeParen = new(
        @"^\(([^()]*)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Piece = new(
        @"^(?:(?:main|love|end|opening|closing)\s+)?theme(?:\s+from\b.*)?$|^from\b.+$|^(?:piano\s+)?version$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Direction = new(
        @"^(?:(?:\d+\s+)?times|forte|piano|pianissimo|fortissimo|alio modo|ad lib\.?|repeat)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Dash = new(
        @"\s+[-–—]\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingPiece = new(
        @"[\s\-–—]*(?:main theme|love theme|end theme|piano version|piano solo).*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsJunk(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();
        return text.Length < 3
            || text.Contains('[')
            || text.Contains(']')
            || Junk.IsMatch(text);
    }

    public static string Title(string? extracted, string fileName)
    {
        return IsJunk(extracted) ? fileName : extracted!.Trim();
    }

    public static string Composer(string? extracted)
    {
        return IsJunk(extracted) ? string.Empty : extracted!.Trim();
    }

    public static ScoreCardText Card(string? title, string? subtitle, string? composer, string fileName)
    {
        var work = Clean(title);
        var extra = Clean(subtitle);
        var by = Clean(composer);

        if (TrySplitTrailingDescriptor(work, out var head, out var tail))
        {
            work = head;
            extra = First(extra, tail);
        }

        if (TryUnwrapWhole(work, out var inner))
        {
            if (IsPieceDescriptor(inner))
            {
                extra = First(extra, inner);
                work = WorkTitleFromFile(fileName);
            }
            else if (IsDirection(inner))
            {
                work = WorkTitleFromFile(fileName);
            }
            else
            {
                work = inner;
            }
        }

        if (IsPieceDescriptor(work) || IsDirection(work) || IsJunk(work))
        {
            extra = First(extra, IsPieceDescriptor(work) ? Unwrap(work) : extra);
            work = WorkTitleFromFile(fileName);
        }

        if (IsJunk(work))
        {
            work = fileName;
        }

        if (IsJunk(extra) || IsDirection(extra) || Fold(extra) == Fold(work))
        {
            extra = string.Empty;
        }

        if (IsJunk(by) || (Fold(work).Length > 0 && Fold(by).Contains(Fold(work))))
        {
            by = string.Empty;
        }

        return new ScoreCardText(work, extra, by);
    }

    private static string Clean(string? value)
    {
        return IsJunk(value) ? string.Empty : value!.Trim();
    }

    private static string First(string left, string right)
    {
        return left.Length > 0 ? left : right;
    }

    private static bool TrySplitTrailingDescriptor(string value, out string head, out string tail)
    {
        head = value;
        tail = string.Empty;
        var match = TrailingParen.Match(value);
        if (!match.Success)
        {
            return false;
        }

        var inner = match.Groups[2].Value.Trim();
        if (inner.Length == 0 || IsDirection(inner))
        {
            return false;
        }

        head = match.Groups[1].Value.Trim();
        tail = inner;
        return head.Length > 0;
    }

    private static bool TryUnwrapWhole(string value, out string inner)
    {
        var match = WholeParen.Match(value);
        if (!match.Success)
        {
            inner = string.Empty;
            return false;
        }

        inner = match.Groups[1].Value.Trim();
        return inner.Length > 0;
    }

    private static string Unwrap(string value)
    {
        return TryUnwrapWhole(value, out var inner) ? inner : value;
    }

    private static bool IsPieceDescriptor(string value)
    {
        return Piece.IsMatch(Unwrap(value));
    }

    private static bool IsDirection(string value)
    {
        return Direction.IsMatch(Unwrap(value));
    }

    private static string WorkTitleFromFile(string fileName)
    {
        var parts = Dash.Split(fileName, 2);
        if (parts.Length == 2 && !IsPieceDescriptor(parts[0]) && !IsJunk(parts[0]))
        {
            return parts[0].Trim();
        }

        var cut = TrailingPiece.Replace(fileName, string.Empty).Trim();
        return IsJunk(cut) ? fileName : cut;
    }

    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}

public readonly record struct ScoreCardText(string Title, string Subtitle, string Composer);
