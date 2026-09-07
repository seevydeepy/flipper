using System.Globalization;
using System.Text.RegularExpressions;

namespace Flipper.Core.Library;

public static class ScoreFactInference
{
    private static readonly Regex CopySuffix = new(
        @"(?:\s*[-–—]\s*|^)(?:copy|duplicate)(?:\s*\(\d+\)|\s+\d+)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Extractor version for <see cref="ScoreFacts.CurrentExtractorVersion"/>.
    /// This inference implementation is version 2: evidence-scored selection
    /// (credits parsed with roles, structured filename segments) replaced the
    /// name-capitalisation heuristic ranking.
    /// </summary>
    public const int InferenceVersion = 2;

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

    /// <summary>
    /// Multiword tempo/performance directions that are never work titles, but
    /// only when nothing corroborates them (a real title may coincide).
    /// </summary>
    private static readonly Regex TempoPhrase = new(
        @"^(?:allegro|allegretto|andante|andantino|adagio|largo|lento|moderato|presto|"
        + @"vivace|maestoso)\b.*\b(?:con\s+brio|con\s+moto|con\s+spirito|assai|molto|"
        + @"cantabile|espressivo|dolce|maestoso)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BadRole = new(
        @"^(pedal|piano|basso|violino|viola|cello|flute|guitar|orgue|organ|soprano|alto|tenor|"
        + @"bass|tema|andantino|allegro|andante(\.?|sostenuto)?|adagio|hob\.|op\.|bwv|arr\.|"
        + @"sheet music|solo|trombone|trumpet|violin(e)?|oboe|utente|d\.c\.|d\. s\.|al fine|"
        + @"tempo (i|ii|iii|primo)|cantus|chorus|tutti|refrain|"
        + @"dedi[ée]e?|dedicated|dedicato)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Collection = new(
        @"^\d+\s*(?:\(\d+\))?\s+(?:pieces?|pi[eè]ces?|studies|etudes|études|duets?|lessons|caprices|exercises|airs|fugues?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Collection headers ("12 Études", "6 Lieder") are bad titles only when
    /// bare: with an opus/catalogue number, key, or composer attribution they
    /// are meaningful work titles.
    /// </summary>
    /// <summary>
    /// Bare catalogue fragments ("Op. 50", "BWV Anh. 114") carry no work
    /// identity on their own. A line with a substantial non-marker word
    /// ("Sonata" in "Sonata II BWV 1003", "Études" in "12 Études, Op. 10")
    /// names a work and is never a fragment.
    /// </summary>
    private static readonly HashSet<string> CatalogueMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "op", "opus", "bwv", "kv", "hob", "rv", "anh", "no", "nr", "number", "d", "k"
    };

    private static bool IsCatalogueFragment(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 4)
        {
            return false;
        }

        foreach (var word in words)
        {
            var letters = new string(word.Where(char.IsLetter).ToArray());
            if (letters.Length >= 5 && !CatalogueMarkers.Contains(letters))
            {
                return false;
            }
        }

        var tiny = words.Count(w =>
            w.Length <= 2 || (w.Length <= 4 && w.All(ch => !char.IsLetter(ch) || char.ToLowerInvariant(ch) == 'z')));
        if ((double)tiny / words.Length < 0.5)
        {
            return false;
        }

        return Regex.IsMatch(
            value,
            @"\b(?:op(?:us)?\.?|bwv|kv|hob|rv|anh\.?|no\.?|nr\.?)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsBareCollection(string value)
    {
        if (!Collection.IsMatch(value))
        {
            return false;
        }

        return !Regex.IsMatch(
            value,
            @"\b(?:op(?:us)?\.?|bwv|kv|k\.?\s*\d|hob|rv|no\.?|nr\.?|n°|nº)\b|\b[a-g][#♯b♭]?\s+(?:major|minor|dur|moll)\b|,\s*op\.?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Split "F. Chopin. Op.6, No.1." into composer + catalogue parts.
    /// Returns null unless the head is a plausible name and the tail carries
    /// an opus/catalogue identifier.
    /// </summary>
    private static (string Name, string Catalogue)? SplitComposerCatalogue(string line)
    {
        var match = Regex.Match(
            line.Trim(),
            @"^(.+?)\.\s*((?:op(?:us)?\.?|bwv|kv|hob|rv|anh\.?|d\.?)\s*.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups[1].Value.Trim().TrimEnd('.');
        // "F. Chopin" is name-like only under the relaxed single-lowercase
        // rule; accept it when it has capitals and no glue words.
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ok = words.Length is >= 1 and <= 6
            && words.Any(w => w.Length > 0 && char.IsUpper(w[0]))
            && words.All(w => !TitleGlue.Contains(w));
        if (!ok || CleanComposer(name) is null)
        {
            return null;
        }

        return (name, match.Groups[2].Value.Trim());
    }

    /// <summary>
    /// Token agreement between two strings (shared word after folding).
    /// Static helper for the pre-selector split functions below.
    /// </summary>
    private static bool StaticAgrees(string left, string right)
    {
        var l = Tokens(left);
        var r = Tokens(right);
        return l.Count > 0 && r.Count > 0 && l.Overlaps(r);
    }

    /// <summary>
    /// Split fused movement + composer lines ("Beati mortui Felix Mendelssohn
    /// Bartholdy"): the trailing words agreeing with the embedded author or
    /// folder hint are the composer; the movement head is not a rival credit.
    /// Returns null without metadata/folder corroboration.
    /// </summary>
    private static (string Name, string Movement)? SplitFusedCredit(string line, string? metadataComposer)
    {
        var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3 || words.Length > 8)
        {
            return null;
        }

        // Every tail word must agree with the embedded author: "Beati mortui
        // Felix Mendelssohn Bartholdy" yields "Felix Mendelssohn Bartholdy",
        // not "mortui + …". Longest agreeing run wins.
        for (var take = Math.Min(4, words.Length - 1); take >= 1; take--)
        {
            var tailWords = words[^take..];
            var tail = string.Join(" ", tailWords);
            if (!LooksLikeName(tail) || CleanComposer(tail) is null)
            {
                continue;
            }

            if (metadataComposer is not null && TailAgrees(tailWords, metadataComposer))
            {
                return (CleanComposer(tail)!, string.Join(" ", words[..^take]));
            }
        }

        return null;
    }

    private static bool TailAgrees(string[] tailWords, string metadataComposer)
    {
        var meta = Tokens(metadataComposer);
        if (meta.Count == 0)
        {
            return false;
        }

        foreach (var word in tailWords)
        {
            var folded = word.ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
            var parts = Regex.Matches(folded, @"\p{L}{3,}")
                .Select(match => match.Value)
                .ToArray();
            if (parts.Length == 0)
            {
                continue;
            }

            if (parts.Any(part => !meta.Contains(part)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Split "F. Sor Allegro" into name + trailing tempo/role word.
    /// Returns null unless the head is a plausible name and the tail is a
    /// single known direction/role word.
    /// </summary>
    private static (string Name, string Tail)? SplitComposerTempo(string line)
    {
        var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words.Length > 6)
        {
            return null;
        }

        var tail = words[^1].TrimEnd('.');
        if (!Tempo.IsMatch(tail) && !TempoPhrase.IsMatch(tail) && !BadRole.IsMatch(tail))
        {
            return null;
        }

        var name = string.Join(" ", words[..^1]).Trim().TrimEnd('.');
        if (CleanComposer(name) is null)
        {
            return null;
        }

        return (name, tail);
    }

    /// <summary>
    /// Whether the composer pass would claim this line as the composer.
    /// Title selection consults it so a credit line is never recycled as the
    /// work title when a real title candidate exists.
    /// </summary>
    private static bool ComposerClaims(string line, IReadOnlyList<string> lines)
    {
        if (ClassifyCredit(line, out _) != CreditRole.None || !LooksLikeName(line))
        {
            return false;
        }

        if (CleanComposer(line) is null || UnwrapWhole(line) is not null || IsPiece(line))
        {
            return false;
        }

        return true;
    }

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
        @"^(?:(?:\d+\s+)?times|forte|piano|pianissimo|fortissimo|alio modo|ad lib\.?|repeat|"
        + @"allegro con brio|allegro|allegretto|andante|andantino|adagio|largo|lento|moderato|"
        + @"presto|vivace|maestoso|rubato|a tempo|rit\.?|rall\.?|accel\.?|dolce|cantabile|"
        + @"espressivo|con moto|con brio|con spirito|molto allegro|allegro assai|presto assai)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Byline = new(
        @"^(?:by|arr\.?|arranged by|transc(?:ribed)?\.? by|composed by|music by)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Credit roles: only composer/composed/music-by may propose the composer.</summary>
    internal enum CreditRole
    {
        None,
        Composer,
        Arranger,
        Other
    }

    private static readonly Regex ComposerCredit = new(
        @"^(?:composed\s+by|music\s+by|by)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ArrangerCredit = new(
        @"^(?:arr\.?|arranged\s+by|transc(?:ribed)?\.?\s+by)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OtherCredit = new(
        @"^(?:words\s+by|lyrics\s+by|performed\s+by|transcribed\s+by)\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static CreditRole ClassifyCredit(string line, out string? name)
    {
        name = null;
        var match = ComposerCredit.Match(line);
        if (match.Success)
        {
            name = match.Groups[1].Value.Trim();
            return CreditRole.Composer;
        }

        match = ArrangerCredit.Match(line);
        if (match.Success)
        {
            name = match.Groups[1].Value.Trim();
            return CreditRole.Arranger;
        }

        match = OtherCredit.Match(line);
        if (match.Success)
        {
            name = match.Groups[1].Value.Trim();
            return CreditRole.Other;
        }

        match = LabelledComposer.Match(line);
        if (match.Success)
        {
            var label = line[..line.IndexOf(':')].Trim().ToLowerInvariant();
            name = match.Groups[1].Value.Trim();
            return label.Contains("arrang") ? CreditRole.Arranger
                : label.Contains("composer") ? CreditRole.Composer
                : CreditRole.Other;
        }

        return CreditRole.None;
    }

    private static readonly Regex MovementHeader = new(
        @"^(?:[ivxlcdm]+\.|no\.?\s*\d|n°\s*\d|\d+\.)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool IsMovementHeader(string value) => MovementHeader.IsMatch(value.Trim());

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
        "on", "of", "the", "and", "from", "to", "in", "at", "for", "by", "with", "is", "a", "an", "op"
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
        return InferWithEvidence(fileName, metadata, pageLines, relativeFolder: null).Facts;
    }

    /// <summary>
    /// Layout-aware entry point: heading/credit blocks steer selection.
    /// Multiline titles merge when continuation lines share size/bold; a
    /// separate title block and composer block resolve independently.
    /// </summary>
    public static ScoreFacts InferRich(
        string fileName,
        ScoreMetadata metadata,
        IReadOnlyList<ScoreTextLine> richLines)
    {
        var merged = MergeMultilineTitles(richLines);
        var strings = merged.Select(line => line.Text).ToArray();
        return InferWithEvidence(fileName, metadata, strings, relativeFolder: null).Facts;
    }

    /// <summary>
    /// Infer plus the evidence behind the decision: winning candidates, their
    /// corroboration, and the runner-up. A readable filename fallback is always
    /// available but reported as unverified, never as a confirmed title.
    /// </summary>
    public static InferenceDecision InferWithEvidence(
        string fileName,
        ScoreMetadata metadata,
        IReadOnlyList<string> pageLines,
        string? relativeFolder = null)
    {
        var fileTitle = CleanFileName(fileName);
        var segments = ParseFilenameSegments(fileName);
        var lines = pageLines
            .Select(CleanText)
            .Where(IsUsefulLine)
            .Take(10)
            .ToArray();
        var credits = ExtractCredits(lines);
        var metadataTitle = CleanTitle(metadata.Title);
        var metadataComposer = CleanComposer(metadata.Author);
        var metadataSubtitle = CleanSubtitle(metadata.Subject);
        var folderHint = FolderComposerHint(relativeFolder);

        var selector = new EvidenceSelector(fileTitle, segments, lines, credits, folderHint);
        var titleDecision = selector.SelectTitle(metadataTitle);
        var composerDecision = selector.SelectComposer(
            titleDecision.Value ?? metadataTitle ?? fileTitle,
            metadataComposer,
            credits);

        var title = titleDecision.Value ?? metadataTitle ?? fileTitle;
        var titleVerified = titleDecision.Value is not null || metadataTitle is not null;
        string? composer;
        if (metadataComposer is not null && composerDecision.Corroborated)
        {
            // An explicit printed composer credit corroborates generic PDF Author
            // metadata; the printed credit wins and the metadata is supporting
            // evidence rather than the decision.
            composer = composerDecision.Value ?? metadataComposer;
        }
        else if (composerDecision.Value is not null)
        {
            composer = composerDecision.Value;
        }
        else
        {
            composer = metadataComposer;
        }

        var subtitle = PickSubtitle(lines, title, credits) ?? metadataSubtitle;

        if (string.Equals(title, composer, StringComparison.OrdinalIgnoreCase))
        {
            composer = null;
        }

        return new InferenceDecision(
            new ScoreFacts
            {
                Title = Limit(title, 160),
                Composer = Limit(composer, 80),
                Subtitle = Limit(subtitle, 160)
            },
            titleVerified,
            titleDecision.Evidence,
            composerDecision.Evidence,
            titleDecision.RunnerUp);
    }

    public static bool HasUsefulPageText(string fileName, IReadOnlyList<string> pageLines)
    {
        var lines = pageLines.Select(CleanText).Where(IsUsefulLine).Take(10).ToArray();
        return lines.Sum(line => line.Count(char.IsLetter)) >= 20
            && PickPageTitle(lines, CleanFileName(fileName), null, null) is not null;
    }

    /// <summary>
    /// Merge wrapped title lines: adjacent lines on the same page with matching
    /// size/bold that are both too short to stand alone join with a space.
    /// Returns the merged sequence (original order otherwise preserved).
    /// </summary>
    public static IReadOnlyList<ScoreTextLine> MergeMultilineTitles(IReadOnlyList<ScoreTextLine> lines)
    {
        if (lines.Count < 2)
        {
            return lines;
        }

        var merged = new List<ScoreTextLine>();
        ScoreTextLine? pending = null;
        foreach (var line in lines)
        {
            if (pending is { } head
                && head.PageNumber == line.PageNumber
                && head.Source == line.Source
                && SizesMatch(head.FontSize, line.FontSize)
                && head.Bold == line.Bold
                && head.Text.Length < 40
                && line.Text.Length < 40
                && VerticalGap(head, line) < 0.06
                && IsBadTitle(head.Text + " " + line.Text) == false
                && (IsBadTitle(head.Text) || LooksLikeName(head.Text) == false || LooksLikeName(line.Text) == false))
            {
                pending = head with
                {
                    Text = head.Text + " " + line.Text,
                    Width = Math.Max(head.Width, line.Width),
                    Height = head.Height + line.Height
                };
                continue;
            }

            if (pending is not null)
            {
                merged.Add(pending);
            }

            pending = line;
        }

        if (pending is not null)
        {
            merged.Add(pending);
        }

        return merged;
    }

    private static bool SizesMatch(double? left, double? right)
    {
        if (left is null || right is null)
        {
            return true;
        }

        return Math.Abs(left.Value - right.Value) <= Math.Max(1.0, left.Value * 0.15);
    }

    private static double VerticalGap(ScoreTextLine upper, ScoreTextLine lower)
    {
        return Math.Max(0, lower.Y - (upper.Y + upper.Height));
    }
    /// the next useful line, and a leading "by <name>" byline attaches to the
    /// previous line's context. Returns the merged lines plus parsed credits.
    /// </summary>
    internal static IReadOnlyList<ParsedCredit> ExtractCredits(IReadOnlyList<string> lines)
    {
        var credits = new List<ParsedCredit>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (CreditLabel.IsMatch(line) && i + 1 < lines.Count)
            {
                var next = lines[i + 1].Trim();
                if (next.Length >= 3)
                {
                    var role = line.StartsWith("music", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("composed", StringComparison.OrdinalIgnoreCase)
                        ? CreditRole.Composer
                        : CreditRole.Arranger;
                    credits.Add(new ParsedCredit(next, role, Labelled: true, LineIndex: i));
                    i++;
                    continue;
                }
            }

            var role2 = ClassifyCredit(line, out var name);
            if (role2 != CreditRole.None && !string.IsNullOrWhiteSpace(name))
            {
                credits.Add(new ParsedCredit(name!.Trim(), role2, Labelled: true, LineIndex: i));
            }
        }

        return credits;
    }

    /// <summary>
    /// Structured filename segments: split on " - " first, then parse an
    /// explicit "by &lt;name&gt;" byline from either side. Returns title/composer
    /// candidates usable only when no better evidence exists. A bare fragment
    /// ("Bwv", "Op") is never a title segment.
    /// </summary>
    internal static FilenameSegments ParseFilenameSegments(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        var dash = Regex.Split(stem, @"\s+[-\u2013\u2014]\s+", RegexOptions.CultureInvariant);
        string? title = null;
        string? composer = null;
        if (dash.Length >= 2)
        {
            // Conservative: "Title - Music by Name" or "Name - Title".
            // A "by <name>" byline is strong; otherwise keep both sides as
            // title candidates and let corroboration decide.
            var left = CleanFileName(dash[0]);
            var right = dash[^1].Trim();
            var byRight = Regex.Match(right, @"^(?:music\s+by|composed\s+by|by)\s+(.+)$", RegexOptions.IgnoreCase);
            var byLeft = Regex.Match(dash[0].Trim(), @"^(?:music\s+by|composed\s+by|by)\s+(.+)$", RegexOptions.IgnoreCase);
            if (byRight.Success && byRight.Groups[1].Value.Trim().Length >= 3)
            {
                title = left;
                composer = byRight.Groups[1].Value.Trim();
            }
            else if (byLeft.Success && left.Length >= 3)
            {
                composer = byLeft.Groups[1].Value.Trim();
                title = CleanFileName(dash[^1]);
            }
            else
            {
                title = left;
            }
        }
        else
        {
            var byWhole = Regex.Match(stem, @"^(.*?)\s+(?:music\s+by|composed\s+by|by)\s+(.+)$", RegexOptions.IgnoreCase);
            if (byWhole.Success
                && byWhole.Groups[1].Value.Trim().Length >= 3
                && byWhole.Groups[2].Value.Trim().Length >= 3)
            {
                title = CleanFileName(byWhole.Groups[1].Value);
                composer = byWhole.Groups[2].Value.Trim();
            }
        }

        if (title is not null && (IsBadTitle(title) || BadRole.IsMatch(title) || IsCatalogueFragment(title)))
        {
            title = null;
        }

        return new FilenameSegments(
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(composer) ? null : composer);
    }

    internal static string? FolderComposerHint(string? relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder))
        {
            return null;
        }

        var parts = relativeFolder.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Equals("corpus", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1].Trim();
        }

        return null;
    }

    private static string? PickSubtitle(
        IReadOnlyList<string> lines,
        string title,
        IReadOnlyList<ParsedCredit> credits)
    {
        var creditLines = credits.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A translation/subtitle line carries separator marks (*, /, |, —):
        // prefer it as subtitle so the composer pass never sees it as a
        // person credit ("Fortschritt * Progress" is a gloss of "Progrès").
        var marked = new List<string>();
        var plain = new List<string>();
        foreach (var line in lines)
        {
            if (creditLines.Contains(line))
            {
                continue;
            }

            var whole = UnwrapWhole(line);
            string? candidate = null;
            if (whole is not null)
            {
                if (IsPiece(whole))
                {
                    return whole;
                }

                if (!string.Equals(whole, title, StringComparison.OrdinalIgnoreCase) && !IsDirection(whole))
                {
                    candidate = whole;
                }
            }
            else
            {
                var trailing = TrailingParen.Match(line);
                if (trailing.Success)
                {
                    var extra = trailing.Groups[2].Value.Trim();
                    if (extra.Length > 0
                        && !IsDirection(extra)
                        && string.Equals(trailing.Groups[1].Value.Trim(), title, StringComparison.OrdinalIgnoreCase))
                    {
                        return extra;
                    }
                }
            }

            if (candidate is not null)
            {
                (HasSeparatorMark(candidate) ? marked : plain).Add(candidate);
            }
        }

        return marked.FirstOrDefault() ?? plain.FirstOrDefault();
    }

    internal static bool HasSeparatorMark(string value)
    {
        return value.Contains('*') || value.Contains('/') || value.Contains('|')
            || value.Contains('—') || value.Contains(" - ");
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
        var text = (value ?? string.Empty).Normalize(System.Text.NormalizationForm.FormC);
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
        if (IsQuotedSeriesHeader(text))
        {
            return null;
        }
        // Trailing parenthetical dates ("Name (1685-1750)") and catalogue
        // suffixes ("Op. 57") belong to the credit line, not the name.
        // Keep the original display spelling: only strip when the remainder
        // stays a plausible name.
        var stripped = Regex.Replace(
            text,
            @"\s*\(\s*\d{3,4}(?:\s*[–—-]\s*\d{2,4})?\s*\)\s*$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();
        if (LooksLikeName(stripped) || stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2)
        {
            text = stripped;
        }

        stripped = Regex.Replace(
            text,
            @"\s*(?:op(?:us)?\.?\s*\d+.*|bwv\s*\d+.*|hob.*|rv.*)$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        if (stripped.Length >= 3 && (LooksLikeName(stripped) || stripped.Contains(' ')))
        {
            text = stripped;
        }

        return text.Length < 3 || Junk.IsMatch(text) || BadRole.IsMatch(text) ? null : text;
    }

    private static bool IsUsefulLine(string line)
    {
        if (line.Length == 0 || Junk.IsMatch(line) || BadRole.IsMatch(line))
        {
            return false;
        }

        // Single Roman numerals ("III", "VI V") and bare movement numbers are
        // engraving marks, not headings.
        if (Regex.IsMatch(line.Trim(), @"^(?:[ivxlcdm]+\s*){1,3}\.?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        // Engraving tablature / note-glyph rows ("4 Z Z Z Z Z", "Guitar 0 Z 0 Z"):
        // single letters and digits with almost no real words are not text.
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 3)
        {
            var tiny = words.Count(w =>
                w.Length <= 2 || (w.Length <= 4 && w.All(ch => !char.IsLetter(ch) || char.ToLowerInvariant(ch) == 'z')));
            if ((double)tiny / words.Length >= 0.6)
            {
                return false;
            }
        }

        // Single-instrument labels ("Guitar") and bare catalogue numbers are
        // roles, not headings; the BadRole check below covers whole-line hits.

        // Music-font glyph noise (single symbols, figured-bass digits): a line
        // with almost no letters is engraving, not text.
        var letters = line.Count(char.IsLetter);
        if (letters < 3 || (double)letters / line.Length < 0.35)
        {
            return false;
        }

        // Lines dominated by symbol/digit runs (e.g. a font-decoded word salad
        // like "789: ;<=> 9: ?9@AB...") are mis-decoded glyphs, not headings.
        // A dense run of digits+symbols (>= 8 in any 12-char window) marks
        // engraving noise even when scattered letters pass the ratio check.
        var symbols = line.Count(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch));
        if (symbols > letters || HasDenseNoiseRun(line))
        {
            return false;
        }

        return true;
    }

    private static bool HasDenseNoiseRun(string line)
    {
        if (line.Length < 12)
        {
            return false;
        }

        for (var i = 0; i + 12 <= line.Length; i++)
        {
            var noise = 0;
            for (var j = i; j < i + 12; j++)
            {
                var ch = line[j];
                if (char.IsDigit(ch) || (!char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch)))
                {
                    noise++;
                }
            }

            if (noise >= 8)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBadTitle(string value)
    {
        if (value.Length < 3
            || Junk.IsMatch(value)
            || IsBareCollection(value)
            || Tempo.IsMatch(value)
            || TempoPhrase.IsMatch(value)
            || BadRole.IsMatch(value.Trim())
            || CreditLabel.IsMatch(value)
            || IsQuotedSeriesHeader(value)
            || value.All(char.IsDigit))
        {
            return true;
        }

        var letters = value.Count(char.IsLetter);
        return letters < 3 || (double)letters / value.Length < 0.35;
    }

    /// <summary>
    /// Quoted series headers ('"Sechs Sonaten für Violine"') name a collection,
    /// never a work: they are bad titles and bad composer credits alike.
    /// Trailing-quote form ('Sechs Sonaten für Violine"') matches too, since
    /// engraving extraction often drops the opening quote. A bare fragment
    /// check needs the quotes stripped: '"Sechs …"' is name-like only with
    /// them removed.
    /// </summary>
    private static bool IsQuotedSeriesHeader(string value)
    {
        var text = value.Trim();
        if (text.Length < 2 || !text.Contains('"'))
        {
            return false;
        }

        // Series/collection names ("Sechs Sonaten für Violine"): a quoted
        // multi-word title-cased phrase. Instrument words (Violine) are
        // expected here — they mark a collection, not a person — so check
        // phrase shape directly instead of LooksLikeName (which rejects
        // instrument roles). Quoted composer credits are vanishingly rare
        // on engraved title pages; quotes mark a series, not a person.
        var stripped = text.Trim('"');
        var words = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2
            && words.Count(w => w.Length > 0 && char.IsUpper(w[0])) >= 2;
    }

    private static bool LooksLikeName(string value)
    {
        var text = Years.Replace(value, string.Empty).Trim();
        if (text.Length == 0 || WholeParen.IsMatch(text) || Regex.IsMatch(text, @"[A-Za-z]['’]s\b"))
        {
            return false;
        }

        // Dedication lines ("À Mlle la Comtesse PAULINE PLATER.",
        // "dédiés aux amateurs de la Musique") are not composers.
        if (Regex.IsMatch(text, @"^(à|a|to|for|dediée|dedicated|pour|dédiés)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
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

        // Name-like capitalisation is weak evidence (never decisive): accept any
        // letter case here and let the evidence scorer weigh corroboration.
        // All-uppercase names ("CLAUDE DEBUSSY") are real composer credits.
        var capitals = words.Count(word => word.Length > 0 && char.IsUpper(word[0]));
        var lowercase = words.Count(word => word.Length > 0 && char.IsLower(word[0]));
        if (lowercase > 1)
        {
            return false;
        }

        return capitals >= 1;
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
        // Unicode-aware word scan: letters of any script, length 3+.
        return Regex.Matches(folded, @"\p{L}{3,}")
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

    /// <summary>A parsed credit line: name, role, and where it was found.</summary>
    internal readonly record struct ParsedCredit(
        string Name,
        CreditRole Role,
        bool Labelled,
        int LineIndex);

    internal readonly record struct FilenameSegments(string? Title, string? Composer);

    /// <summary>
    /// The decision plus its evidence. Confidence is an evidence weight
    /// (stronger corroboration scores higher), not a calibrated probability.
    /// TitleVerified is false when the title is only the filename fallback.
    /// </summary>
    public sealed record InferenceDecision(
        ScoreFacts Facts,
        bool TitleVerified,
        string TitleEvidence,
        string ComposerEvidence,
        string? TitleRunnerUp);

    internal sealed class EvidenceSelector
    {
        private readonly string _fileTitle;
        private readonly FilenameSegments _segments;
        private readonly IReadOnlyList<string> _lines;
        private readonly IReadOnlyList<ParsedCredit> _credits;
        private readonly string? _folderHint;
        private readonly HashSet<string> _fileTokens;

        internal EvidenceSelector(
            string fileTitle,
            FilenameSegments segments,
            IReadOnlyList<string> lines,
            IReadOnlyList<ParsedCredit> credits,
            string? folderHint)
        {
            _fileTitle = fileTitle;
            _segments = segments;
            _lines = lines;
            _credits = credits;
            _folderHint = folderHint;
            _fileTokens = Tokens(fileTitle);
        }

        internal (string? Value, string Evidence, string? RunnerUp) SelectTitle(string? metadataTitle)
        {
            var scored = new List<(string Line, int Score, string Why)>();
            foreach (var line in _lines)
            {
                if (IsBadTitle(line) || IsDirection(line) || IsCreditLine(line))
                {
                    continue;
                }

                var inner = UnwrapWhole(line) ?? line;
                var tokens = Tokens(inner);
                var overlap = tokens.Count(token => _fileTokens.Contains(token));
                var score = 0;
                var reasons = new List<string>();
                if (metadataTitle is not null
                    && string.Equals(inner, metadataTitle, StringComparison.OrdinalIgnoreCase))
                {
                    score += 4;
                    reasons.Add("matches embedded title");
                }

                // A bare catalogue fragment ("Op. 50", "BWV Anh. 114") is not a
                // title candidate: it only scores via full agreement above.
                // Skip it so metadata-agreeing titles always win.
                if (score == 0 && IsCatalogueFragment(inner))
                {
                    continue;
                }

                // A person-like line that the composer pass also claims is a
                // credit, not a title — unless the filename corroborates THIS
                // line (overlap or prefix): then it is the work, not a credit
                // (Moonlight Sonata must not yield to the composer line, and
                // "12 Études" / "Clair de Lune" stay titles). Uncorroborated
                // person-like lines yield to real title candidates.
                var otherTitleShaped = _lines.Any(l =>
                    !string.Equals(l, line, StringComparison.OrdinalIgnoreCase)
                    && !IsBadTitle(l) && !IsDirection(l) && !IsCreditLine(l));
                if (score <= 1
                    && LooksLikeName(inner)
                    && ComposerClaims(inner, _lines)
                    && overlap == 0
                    && !PrefixMatches(inner, _fileTitle)
                    && otherTitleShaped)
                {
                    continue;
                }

                if (overlap >= 2)
                {
                    score += 4;
                    reasons.Add($"shares {overlap} filename words");
                }
                else if (overlap == 1)
                {
                    score += 2;
                    reasons.Add("shares 1 filename word");
                }

                if (PrefixMatches(inner, _fileTitle))
                {
                    score += 1;
                    reasons.Add("filename prefix");
                }

                if (_segments.Title is not null
                    && string.Equals(inner, _segments.Title, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                    reasons.Add("filename segment");
                }

                // Printed evidence outranks the no-evidence filename fallback:
                // a page line at neutral score still beats falling back to the
                // bare filename. Strong negative signals keep their veto below.
                // Skipped when the filename itself is unreadable (Untitled):
                // with no filename evidence there is nothing to outrank.
                if (score == 0 && !_fileTitle.Equals("Untitled", StringComparison.OrdinalIgnoreCase))
                {
                    score += 1;
                    reasons.Add("printed over fallback");
                }

                if (UnwrapWhole(line) is not null)
                {
                    score -= 1;
                    reasons.Add("parenthesised");
                }

                // Name-like capitalisation is weak evidence: a small nudge, and
                // only when nothing stronger has spoken.
                if (LooksLikeName(inner) && score == 0)
                {
                    score += 0;
                    reasons.Add("name-like (weak, ignored)");
                }

                if (LooksLikeComposerCredit(inner))
                {
                    score -= 3;
                    reasons.Add("looks like a person credit");
                }

                scored.Add((line, score, reasons.Count > 0 ? string.Join("; ", reasons) : "no corroboration"));
            }

            if (scored.Count == 0)
            {
                // Structured filename segment is usable when nothing better exists.
                if (_segments.Title is not null)
                {
                    return (_segments.Title, "filename segment (uncorroborated)", null);
                }

                return (null, "no page evidence", null);
            }

            // Printed evidence outranks the no-evidence filename fallback (handled
            // by the neutral-score nudge above); strong negative signals keep
            // their veto here.
            scored.Sort((a, b) => b.Score.CompareTo(a.Score));
            var winner = scored[0];
            var runnerUp = scored.Count > 1 && scored[1].Score >= winner.Score - 1
                ? $"{scored[1].Line} ({scored[1].Score}: {scored[1].Why})"
                : null;
            if (winner.Score <= -2)
            {
                // Only piece-descriptor / credit-like lines: abstain from the page
                // and let metadata/filename decide.
                return (null, $"page lines unusable (best: {winner.Line}: {winner.Why})", null);
            }

            var evidence = winner.Score > 0
                ? $"{winner.Line} ({winner.Score}: {winner.Why})"
                : $"{winner.Line} (weak: {winner.Why})";
            return (UnwrapWhole(winner.Line) is { } whole && !IsPiece(whole) ? whole : winner.Line,
                evidence, runnerUp);
        }

        internal (string? Value, string Evidence, bool Corroborated) SelectComposer(
            string title,
            string? metadataComposer,
            IReadOnlyList<ParsedCredit> credits)
        {
            // Explicit composer-role credits first: labelled and split-line alike,
            // independent of capitalisation. Arranger/transcriber/lyricist roles
            // never propose the composer.
            var composerCredit = credits.FirstOrDefault(c => c.Role == CreditRole.Composer);
            if (composerCredit.Name is not null)
            {
                var cleaned = CleanComposer(composerCredit.Name);
                if (cleaned is not null)
                {
                    var corroborated = metadataComposer is not null
                        || (_folderHint is not null && Agrees(cleaned, _folderHint));
                    return (cleaned,
                        $"explicit credit '{composerCredit.Name}'" + (metadataComposer is not null ? " + embedded author" : ""),
                        Corroborated: true);
                }
            }

            // Quoted aliases ("Sechs Sonaten für Violine") are series headers,
            // not person credits: the composer pass must not claim them.
            // Separator-marked subtitle lines ("Fortschritt * Progress") are
            // translation glosses, not people either.
            // Composer-style inline lines ("F. Chopin. Op.6, No.1.") split
            // into name + catalogue: the name proposes the composer while
            // the catalogue tail never does.
            // "F. Sor Allegro": name + trailing tempo word, no punctuation.
            // Skip movement headers ("I. Beati mortui", "1. Grave"): the
            // numeral head is a movement marker, not a person initial.
            foreach (var line in _lines)
            {
                if (IsMovementHeader(line))
                {
                    continue;
                }

                if (IsQuotedSeriesHeader(line) || HasSeparatorMark(line))
                {
                    continue;
                }

                if (line.Length >= 2 && line.StartsWith('"'))
                {
                    continue;
                }

                var split = SplitComposerCatalogue(line);
                if (split is not null && CleanComposer(split.Value.Name) is { } splitName)
                {
                    return (splitName, $"composer + catalogue '{line}'", Corroborated: true);
                }

                // Multi-name credits ("Beati mortui Felix Mendelssohn Bartholdy")
                // are movement + composer fused by engraving order: the trailing
                // name agreeing with metadata/folder wins, the movement head does
                // not become a rival.
                var fused = SplitFusedCredit(line, metadataComposer);
                if (fused is not null)
                {
                    return (fused.Value.Name, $"fused credit '{line}'", Corroborated: true);
                }

                var pair = SplitComposerTempo(line);
                if (pair is not null && CleanComposer(pair.Value.Name) is { } pairName)
                {
                    return (pairName, $"composer + tempo '{line}'", Corroborated: true);
                }
            }

            // Bare person-like lines: weak evidence, needs corroboration from
            // the filename, folder hint, or metadata before it may decide.
            foreach (var line in _lines)
            {
                if (IsQuotedSeriesHeader(line) || HasSeparatorMark(line))
                {
                    continue;
                }

                if (line.Length >= 2 && line.StartsWith('"'))
                {
                    continue;
                }

                if (string.Equals(line, title, StringComparison.OrdinalIgnoreCase)
                    || UnwrapWhole(line) is not null
                    || IsPiece(line)
                    || IsCreditLine(line)
                    || !LooksLikeName(line))
                {
                    continue;
                }

                var cleaned = CleanComposer(line);
                if (cleaned is null)
                {
                    continue;
                }

                if (Agrees(cleaned, _fileTitle)
                    || (_folderHint is not null && Agrees(cleaned, _folderHint))
                    || (metadataComposer is not null && Agrees(cleaned, metadataComposer)))
                {
                    return (cleaned, $"person-like '{line}' + corroboration", Corroborated: true);
                }

                // Close runner-up: a second distinct person-like line means the
                // attribution is genuinely ambiguous — abstain. Quoted series
                // headers are collection names, not rival people.
                var rivals = _lines.Count(other =>
                    !string.Equals(other, line, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(other, title, StringComparison.OrdinalIgnoreCase)
                    && UnwrapWhole(other) is null
                    && !IsPiece(other)
                    && !IsCreditLine(other)
                    && !IsQuotedSeriesHeader(other)
                    && LooksLikeName(other)
                    && CleanComposer(other) is not null);
                if (rivals > 0)
                {
                    return (null, $"ambiguous person credits ('{line}' + {rivals} rival(s))", Corroborated: false);
                }

                return (cleaned, $"person-like '{line}' (uncorroborated)", Corroborated: false);
            }

            // Explicit filename byline, usable only when nothing better exists.
            if (_segments.Composer is not null && CleanComposer(_segments.Composer) is { } segmentComposer)
            {
                return (segmentComposer, "filename byline (uncorroborated)", Corroborated: false);
            }

            // Folder hint is supporting evidence only, never a decision alone:
            // keep it out of the composer slot unless a credit agrees with it
            // (handled above). Abstain instead.
            return (null, "no composer evidence", Corroborated: false);
        }

        private bool IsCreditLine(string line)
        {
            if (ClassifyCredit(line, out _) != CreditRole.None || CreditLabel.IsMatch(line))
            {
                return true;
            }

            return _credits.Any(c => string.Equals(c.Name, line, StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeComposerCredit(string line)
        {
            return LooksLikeName(line);
        }

        private static bool Agrees(string left, string right)
        {
            return StaticAgrees(left, right);
        }
    }
}

public readonly record struct ScoreMetadata(string? Title, string? Author, string? Subject);
