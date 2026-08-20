using System.Globalization;
using System.Text.RegularExpressions;

namespace Flipper.Core.Library;

public static class ScoreFactInference
{
    private static readonly Regex CopySuffix = new(
        @"(?:\s*[-–—]\s*)?(?:copy|duplicate)(?:\s*\(\d+\)|\s+\d+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NumberSuffix = new(
        @"\s*\(\d+\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Separators = new(
        @"_+|[.\-–—]{2,}|\s+[-–—]\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CamelBoundary = new(
        @"(?<=[a-z]{3})(?=[A-Z])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NumberBoundary = new(
        @"(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhiteSpace = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Junk = new(
        @"public domain|creative commons|mutopia|typeset|licensed under|reference:|"
        + @"free to download|creativecommons|copyright|this sheet music|"
        + @"sheet music from www|unsaved publication|www\.|https?://|finale \d|"
        + @"untitled\d*|created o[nm]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Tempo = new(
        @"^(moderato|andante|allegro|allegretto|adagio|largo|presto|vivace|swing|"
        + @"maestoso|andantino|rubato|a tempo|rit\.?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BadRole = new(
        @"^(pedal|piano|basso|violino|viola|cello|flute|guitar|soprano|alto|tenor|"
        + @"bass|tema|andantino|allegro|andante|adagio|hob\.|op\.|bwv|arr\.|"
        + @"sheet music|solo|trombone|trumpet|violin|oboe|utente)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Collection = new(
        @"^\d+\s*(?:\(\d+\))?\s+(?:pieces|studies|etudes|études|duets|lessons|caprices|exercises|airs)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WholeParen = new(
        @"^\(([^()]*)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingParen = new(
        @"^(.*[^\s(])\s*\(([^()]*)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Piece = new(
        @"^(?:(?:main|love|end|opening|closing)\s+)?theme(?:\s+from\b.*)?$|^from\b.+$|^(?:piano\s+)?version$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Direction = new(
        @"^(?:(?:\d+\s+)?times|forte|piano|pianissimo|fortissimo|alio modo|ad lib\.?|repeat)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Byline = new(
        @"^(?:by|arr\.?|arranged by|transc(?:ribed)?\.? by|composed by|music by)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CreditLabel = new(
        @"^(?:music|composed|arranged|transcribed)\s+by$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LabelledComposer = new(
        @"^(?:performer|artist|composer|arranger)\s*:\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Years = new(
        @"\s*\(\s*\d{3,4}(?:\s*[-–]\s*\d{2,4})?\s*\)\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> TitleGlue = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "of", "the", "and", "from", "to", "in", "at", "for", "by", "with", "is", "a", "an"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "from", "with", "pdf", "piano", "solo", "arr", "sheet", "music"
    };

    public static string CleanFileName(string? fileName)
    {
        var original = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (original.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            original = original[..^4];
        }

        var text = original;
        text = CopySuffix.Replace(text, string.Empty);
        text = NumberSuffix.Replace(text, string.Empty);
        text = Separators.Replace(text, " ");
        text = CamelBoundary.Replace(text, " ");
        text = NumberBoundary.Replace(text, " ");
        text = WhiteSpace.Replace(text, " ").Trim(' ', '-', '–', '—', '_', '.');
        if (text.Length == 0)
        {
            var fallback = Separators.Replace(original, " ");
            fallback = WhiteSpace.Replace(fallback, " ").Trim(' ', '-', '–', '—', '_', '.');
            text = fallback.Any(char.IsLetter) ? fallback : "Untitled";
        }

        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length > 0 && letters.All(char.IsLower))
        {
            text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
        }

        return text;
    }

    public static ScoreFacts Infer(
        string fileName,
        ScoreMetadata metadata,
        IReadOnlyList<string> pageLines)
    {
        var fileTitle = CleanFileName(fileName);
        var lines = pageLines
            .Select(CleanText)
            .Where(IsUsefulLine)
            .Take(10)
            .ToArray();
        var metadataTitle = CleanTitle(metadata.Title);
        var metadataComposer = CleanComposer(metadata.Author);
        var metadataSubtitle = CleanSubtitle(metadata.Subject);
        var headings = PickHeadings(lines, fileTitle, metadataTitle, metadataComposer);
        var title = headings.Title ?? metadataTitle ?? fileTitle;
        var composer = metadataComposer ?? PickComposer(lines, title);
        var subtitle = headings.Subtitle ?? metadataSubtitle;

        if (string.Equals(title, composer, StringComparison.OrdinalIgnoreCase))
        {
            composer = null;
        }

        return new ScoreFacts
        {
            Title = Limit(title, 160),
            Composer = Limit(composer, 80),
            Subtitle = Limit(subtitle, 160)
        };
    }

    public static bool HasUsefulPageText(string fileName, IReadOnlyList<string> pageLines)
    {
        var lines = pageLines.Select(CleanText).Where(IsUsefulLine).Take(10).ToArray();
        return lines.Sum(line => line.Count(char.IsLetter)) >= 20
            && PickPageTitle(lines, CleanFileName(fileName), null, null) is not null;
    }

    private static HeadingPair PickHeadings(
        IReadOnlyList<string> lines,
        string fileTitle,
        string? metadataTitle,
        string? metadataComposer)
    {
        var title = PickPageTitle(lines, fileTitle, metadataTitle, metadataComposer);
        string? subtitle = null;
        if (title is null)
        {
            return default;
        }

        var whole = UnwrapWhole(title);
        if (whole is not null)
        {
            if (IsPiece(whole))
            {
                subtitle = whole;
                title = PickPageTitle(
                    lines.Where(line => line != title).ToArray(),
                    fileTitle,
                    metadataTitle,
                    metadataComposer) ?? fileTitle;
            }
            else
            {
                title = whole;
            }
        }

        var trailing = TrailingParen.Match(title);
        if (trailing.Success)
        {
            var extra = trailing.Groups[2].Value.Trim();
            if (extra.Length > 0 && !IsDirection(extra))
            {
                title = trailing.Groups[1].Value.Trim();
                subtitle ??= extra;
            }
        }

        if (subtitle is null)
        {
            foreach (var line in lines)
            {
                var wrapped = UnwrapWhole(line);
                if (wrapped is null
                    || string.Equals(wrapped, title, StringComparison.OrdinalIgnoreCase)
                    || IsDirection(wrapped))
                {
                    continue;
                }

                subtitle = wrapped;
                break;
            }
        }

        return new HeadingPair(CleanText(title), CleanSubtitle(subtitle));
    }

    private static string? PickPageTitle(
        IReadOnlyList<string> lines,
        string fileTitle,
        string? metadataTitle,
        string? metadataComposer)
    {
        var candidates = lines
            .Where(line => !IsBadTitle(line) && !IsDirection(line))
            .Where(line => !string.Equals(line, metadataComposer, StringComparison.OrdinalIgnoreCase))
            .Where(line => !IsComposerCredit(line))
            .ToArray();
        if (metadataTitle is not null)
        {
            var exact = candidates.FirstOrDefault(line =>
                string.Equals(UnwrapWhole(line) ?? line, metadataTitle, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        var fileTokens = Tokens(fileTitle);
        return candidates
            .Select(line =>
            {
                var inner = UnwrapWhole(line) ?? line;
                var tokens = Tokens(inner);
                var overlap = tokens.Count(token => fileTokens.Contains(token));
                var prefix = PrefixMatches(inner, fileTitle) ? 1 : 0;
                return new TitleCandidate(
                    line,
                    UnwrapWhole(line) is null ? 1 : 0,
                    IsPiece(inner) ? 0 : 1,
                    LooksLikeName(inner) ? 0 : 1,
                    overlap,
                    prefix);
            })
            .OrderByDescending(item => item.NotWrapped)
            .ThenByDescending(item => item.NotPiece)
            .ThenByDescending(item => item.NotName)
            .ThenByDescending(item => item.Overlap)
            .ThenByDescending(item => item.Prefix)
            .Select(item => item.Text)
            .FirstOrDefault();
    }

    private static bool IsComposerCredit(string line)
    {
        return Byline.IsMatch(line) || LabelledComposer.IsMatch(line);
    }

    private static string? PickComposer(IReadOnlyList<string> lines, string title)
    {
        foreach (var line in lines)
        {
            var match = Byline.Match(line);
            if (match.Success)
            {
                var byline = CleanComposer(match.Groups[1].Value);
                if (byline is not null)
                {
                    return byline;
                }
            }

            if (!string.Equals(line, title, StringComparison.OrdinalIgnoreCase)
                && UnwrapWhole(line) is null
                && !IsPiece(line)
                && LooksLikeName(line))
            {
                return CleanComposer(line);
            }
        }

        return null;
    }

    private static string CleanText(string? value)
    {
        var text = value ?? string.Empty;
        text = new string(text.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
        text = text.Replace('•', ' ').Replace('©', ' ');
        text = text.Replace('（', '(').Replace('）', ')');
        return WhiteSpace.Replace(text, " ").Trim(' ', '\u00a0', '-', '–', '_', '|');
    }

    private static string? CleanTitle(string? value)
    {
        var text = CleanText(value);
        return IsBadTitle(text) ? null : text;
    }

    private static string? CleanSubtitle(string? value)
    {
        var text = CleanText(value);
        return text.Length < 3 || Junk.IsMatch(text) || IsDirection(text) ? null : text;
    }

    private static string? CleanComposer(string? value)
    {
        var text = CleanText(value);
        text = Regex.Replace(
            text,
            @"^(?:performer|artist|composer|arranger)\s*:\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return text.Length < 3 || Junk.IsMatch(text) || BadRole.IsMatch(text) ? null : text;
    }

    private static bool IsUsefulLine(string line)
    {
        if (line.Length == 0 || Junk.IsMatch(line) || BadRole.IsMatch(line))
        {
            return false;
        }

        var letters = line.Count(char.IsLetter);
        return letters >= 3 && (double)letters / line.Length >= 0.35;
    }

    private static bool IsBadTitle(string value)
    {
        if (value.Length < 3
            || Junk.IsMatch(value)
            || Collection.IsMatch(value)
            || Tempo.IsMatch(value)
            || BadRole.IsMatch(value)
            || CreditLabel.IsMatch(value)
            || value.All(char.IsDigit))
        {
            return true;
        }

        var letters = value.Count(char.IsLetter);
        return letters < 3 || (double)letters / value.Length < 0.35;
    }

    private static bool LooksLikeName(string value)
    {
        var text = Years.Replace(value, string.Empty).Trim();
        if (text.Length == 0 || WholeParen.IsMatch(text) || Regex.IsMatch(text, @"[A-Za-z]['’]s\b"))
        {
            return false;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 6
            || words.Any(word => TitleGlue.Contains(word))
            || words.Any(word => BadRole.IsMatch(word)))
        {
            return false;
        }

        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length >= 2 && letters.All(char.IsUpper))
        {
            return false;
        }

        var capitals = words.Count(word => word.Length > 0 && char.IsUpper(word[0]));
        return capitals >= Math.Max(1, words.Length - 1);
    }

    private static bool IsPiece(string value)
    {
        return Piece.IsMatch(UnwrapWhole(value) ?? value);
    }

    private static bool IsDirection(string value)
    {
        return Direction.IsMatch(UnwrapWhole(value) ?? value);
    }

    private static string? UnwrapWhole(string value)
    {
        var match = WholeParen.Match(value.Trim());
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static HashSet<string> Tokens(string value)
    {
        var folded = value.ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
        return Regex.Matches(folded, "[a-z]{3,}")
            .Select(match => match.Value)
            .Where(word => !StopWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool PrefixMatches(string title, string fileTitle)
    {
        var titleWords = Tokens(title).ToArray();
        var fileWords = Tokens(fileTitle).ToArray();
        return titleWords.Length > 0
            && titleWords.Length <= fileWords.Length
            && titleWords.SequenceEqual(fileWords.Take(titleWords.Length), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Limit(string? value, int length)
    {
        return value is null || value.Length <= length ? value : value[..length];
    }

    private readonly record struct HeadingPair(string? Title, string? Subtitle);

    private readonly record struct TitleCandidate(
        string Text,
        int NotWrapped,
        int NotPiece,
        int NotName,
        int Overlap,
        int Prefix);
}

public readonly record struct ScoreMetadata(string? Title, string? Author, string? Subject);
