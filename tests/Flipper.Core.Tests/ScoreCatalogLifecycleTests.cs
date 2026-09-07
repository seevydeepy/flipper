using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreCatalogLifecycleTests
{
    private static ScoreEntry Entry(string root, string relativeFolder, string fileName, long length = 10)
    {
        var full = Path.Combine(root, relativeFolder, fileName);
        return new ScoreEntry(
            Path.GetFileNameWithoutExtension(fileName),
            relativeFolder,
            full,
            full,
            length,
            DateTime.UtcNow,
            HasCatalogEntry: true);
    }

    private static CatalogMergeCandidate Generated(
        string title, string? composer, long length, DateTime stamp, int version = ScoreFacts.CurrentExtractorVersion)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, stamp);
        var info = new FileInfo(path);
        var actualStamp = File.GetLastWriteTimeUtc(path);
        return new CatalogMergeCandidate(
            new ScoreFacts { Title = title, Composer = composer },
            SourcePath: path,
            Length: info.Length,
            LastWriteUtc: actualStamp,
            Provenance: CatalogProvenance.ForGenerated(
                version, info.Length, actualStamp,
                new ScoreFacts { Title = title, Composer = composer },
                ExtractionStatus.Complete));
    }

    [Fact]
    public void Merge_InsertsProvenance_WithNewKeys()
    {
        using var root = new TempDir();
        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate>
            {
                ["New.pdf"] = Generated("New Title", "Composer", 10, DateTime.UtcNow)
            });

        Assert.Equal(CatalogMergeStatus.Inserted, result.Status);
        var entry = ScoreCatalog.LoadEntry(root.Path, "New.pdf");
        Assert.NotNull(entry);
        Assert.False(entry!.IsLegacy);
        Assert.Equal(ScoreFieldOrigin.Generated, entry.OriginOf("title"));
        Assert.Equal("New Title", entry.Facts.Title);
    }

    [Fact]
    public void Merge_RefreshesGeneratedFields_OnSourceChange()
    {
        using var root = new TempDir();
        var stamp = DateTime.UtcNow.AddMinutes(-10);
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("Old", "Old C", 10, stamp) });

        var changed = stamp.AddMinutes(5);
        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("New", "New C", 20, changed) });

        Assert.Equal(CatalogMergeStatus.Inserted, result.Status);
        var entry = ScoreCatalog.LoadEntry(root.Path, "A.pdf");
        Assert.Equal("New", entry!.Facts.Title);
        Assert.Equal("New C", entry.Facts.Composer);
    }

    [Fact]
    public void Merge_RefreshesFieldsIndependently()
    {
        using var root = new TempDir();
        var stamp = DateTime.UtcNow.AddMinutes(-10);
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("Old", null, 10, stamp) });

        var changed = stamp.AddMinutes(5);
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("Old", "Found C", 20, changed) });

        var entry = ScoreCatalog.LoadEntry(root.Path, "A.pdf");
        Assert.Equal("Old", entry!.Facts.Title);
        Assert.Equal("Found C", entry.Facts.Composer);
    }

    [Fact]
    public void Merge_PreservesManualCorrections_IncludingClearedFields()
    {
        using var root = new TempDir();
        var stamp = DateTime.UtcNow.AddMinutes(-10);
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("Old", "Old C", 10, stamp) });
        Assert.True(ScoreCatalog.TryCorrect(root.Path, "A.pdf", "Curated", null, null, clearComposer: true));

        var changed = stamp.AddMinutes(5);
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("New", "New C", 20, changed) });

        var entry = ScoreCatalog.LoadEntry(root.Path, "A.pdf");
        Assert.Equal("Curated", entry!.Facts.Title);
        Assert.Null(entry.Facts.Composer);
        Assert.Equal(ScoreFieldOrigin.Manual, entry.OriginOf("title"));
        Assert.Equal(ScoreFieldOrigin.Manual, entry.OriginOf("composer"));
    }

    [Fact]
    public void Merge_NeverOverwritesLegacyEntries()
    {
        using var root = new TempDir();
        File.WriteAllText(
            Path.Combine(root.Path, ScoreCatalog.FileName),
            "{\"A.pdf\":{\"title\":\"Curated\"}}");
        var stamp = DateTime.UtcNow;

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("Auto", "Auto C", 10, stamp) });

        Assert.Equal(CatalogMergeStatus.NoChanges, result.Status);
        var entry = ScoreCatalog.LoadEntry(root.Path, "A.pdf");
        Assert.NotNull(entry);
        Assert.True(entry!.IsLegacy);
        Assert.Equal("Curated", entry.Facts.Title);
    }

    [Fact]
    public void NeedsReanalysis_CoversChangedUpgradedRetryAndUnresolved()
    {
        using var root = new TempDir();
        var stamp = DateTime.UtcNow.AddMinutes(-30);

        // Missing key always qualifies.
        Assert.True(ScoreCatalog.NeedsReanalysis(root.Path, Entry(root.Path, string.Empty, "Missing.pdf")));

        // Legacy entries never qualify here (review path is separate).
        File.WriteAllText(Path.Combine(root.Path, ScoreCatalog.FileName), "{\"L.pdf\":{\"title\":\"Curated\"}}");
        Assert.False(ScoreCatalog.NeedsReanalysis(root.Path, Entry(root.Path, string.Empty, "L.pdf")));

        // Unchanged + complete + current extractor: no reanalysis.
        File.WriteAllText(Path.Combine(root.Path, ScoreCatalog.FileName), "{}");
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("T", "C", 10, stamp) });
        Assert.False(ScoreCatalog.NeedsReanalysis(
            root.Path, Entry(root.Path, string.Empty, "A.pdf", 10) with { LastWriteUtc = stamp }));

        // Changed source qualifies.
        Assert.True(ScoreCatalog.NeedsReanalysis(root.Path, Entry(root.Path, string.Empty, "A.pdf", 11)));

        // Unresolved composer qualifies even when the source is unchanged.
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["B.pdf"] = Generated("T", null, 10, stamp) });
        Assert.True(ScoreCatalog.NeedsReanalysis(
            root.Path, Entry(root.Path, string.Empty, "B.pdf", 10) with { LastWriteUtc = stamp }));
    }

    [Fact]
    public void Backoff_IsBounded_ThenParks()
    {
        var now = DateTime.UtcNow;
        Assert.True(CatalogProvenance.BackoffAfter(1, now) <= now.AddMinutes(2));
        Assert.True(CatalogProvenance.BackoffAfter(5, now) <= now.AddHours(13));
        Assert.Null(CatalogProvenance.BackoffAfter(CatalogProvenance.MaxAttempts, now));
        Assert.Null(CatalogProvenance.BackoffAfter(CatalogProvenance.MaxAttempts + 5, now));
    }

    [Fact]
    public void Preview_ShowsRefresh_ButNotManualOrLegacy()
    {
        using var root = new TempDir();
        var stamp = DateTime.UtcNow.AddMinutes(-10);
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = Generated("Old", "C", 10, stamp) });
        Assert.True(ScoreCatalog.TryCorrect(root.Path, "A.pdf", null, "Curated C", null));
        var catalogPath = Path.Combine(root.Path, ScoreCatalog.FileName);
        var raw = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        raw["L.pdf"] = System.Text.Json.Nodes.JsonNode.Parse("{\"title\":\"Legacy\"}");
        File.WriteAllText(catalogPath, raw.ToJsonString());

        var changed = stamp.AddMinutes(5);
        var preview = ScoreCatalog.PreviewReanalysis(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate>
            {
                ["A.pdf"] = Generated("New", "New C", 20, changed),
                ["B.pdf"] = Generated("B Title", null, 5, changed),
                ["L.pdf"] = Generated("L Auto", null, 5, changed)
            });

        Assert.Contains(preview.Changes, c => c.Key == "A.pdf" && c.After.Title == "New");
        Assert.DoesNotContain(preview.Changes, c => c.Key == "L.pdf");
        Assert.Contains(preview.Preserved, key => key == "A.pdf");
        Assert.Contains(preview.Preserved, key => key == "L.pdf");
    }

    [Fact]
    public void Merge_FillsUnresolvedFields_WithoutSourceChange()
    {
        // NeedsReanalysis schedules unresolved fields; the merge must persist
        // the fill (preview and apply share the predicate). Same content,
        // same extractor, no failure — only the missing composer arrives.
        // The temp-file helper snapshots its own stamp, so reuse the first
        // candidate's fingerprint for the second to model "same PDF, better
        // extraction": same SourcePath/Length/LastWriteUtc, same version.
        using var root = new TempDir();
        var first = Generated("Old", null, 10, DateTime.UtcNow.AddMinutes(-10));
        ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate> { ["A.pdf"] = first });
        var entry = ScoreCatalog.LoadEntry(root.Path, "A.pdf");
        Assert.Equal(ScoreFieldOrigin.Unresolved, entry!.OriginOf("composer"));

        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate>
            {
                ["A.pdf"] = new CatalogMergeCandidate(
                    new ScoreFacts { Title = "Old", Composer = "New C" },
                    first.SourcePath,
                    first.Length,
                    first.LastWriteUtc,
                    CatalogProvenance.ForGenerated(
                        first.Provenance!.ExtractorVersion,
                        first.Length,
                        first.LastWriteUtc,
                        new ScoreFacts { Title = "Old", Composer = "New C" },
                        ExtractionStatus.Complete))
            });

        entry = ScoreCatalog.LoadEntry(root.Path, "A.pdf");
        Assert.Equal("New C", entry!.Facts.Composer);
        Assert.Equal(ScoreFieldOrigin.Generated, entry.OriginOf("composer"));
        Assert.Equal("Old", entry.Facts.Title);
        Assert.Equal(CatalogMergeStatus.Inserted, result.Status);
    }

    [Fact]
    public void Correct_PartialLegacyCorrection_ProtectsSiblingFields()
    {
        using var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "Sub"));
        var pdfPath = Path.Combine(root.Path, "Sub", "Piece.pdf");
        File.WriteAllBytes(pdfPath, new byte[10]);
        File.WriteAllText(
            Path.Combine(root.Path, ScoreCatalog.FileName),
            "{\"Sub\\\\Piece.pdf\":{\"title\":\"Curated\",\"composer\":\"Inherited Maestro\"}}");

        Assert.True(ScoreCatalog.TryCorrect(root.Path, "Sub\\Piece.pdf", "Curated", null, null));

        var entry = ScoreCatalog.LoadEntry(root.Path, "Sub\\Piece.pdf");
        Assert.NotNull(entry);
        Assert.False(entry!.IsLegacy);
        Assert.Equal(ScoreFieldOrigin.Manual, entry.OriginOf("title"));
        Assert.Equal(ScoreFieldOrigin.Legacy, entry.OriginOf("composer"));
        Assert.Equal("Inherited Maestro", entry.Facts.Composer);

        // A later refresh must not touch the inherited sibling field, even
        // when the extractor now claims a different composer.
        var info = new FileInfo(pdfPath);
        var result = ScoreCatalog.TryMergeMissing(
            root.Path,
            new Dictionary<string, CatalogMergeCandidate>
            {
                ["Sub\\Piece.pdf"] = new CatalogMergeCandidate(
                    new ScoreFacts { Title = "Auto", Composer = "Auto C" },
                    info.FullName,
                    info.Length,
                    info.LastWriteTimeUtc,
                    CatalogProvenance.ForGenerated(
                        ScoreFacts.CurrentExtractorVersion,
                        info.Length,
                        info.LastWriteTimeUtc,
                        new ScoreFacts { Title = "Auto", Composer = "Auto C" },
                        ExtractionStatus.Complete))
            });

        entry = ScoreCatalog.LoadEntry(root.Path, "Sub\\Piece.pdf");
        Assert.Equal("Inherited Maestro", entry!.Facts.Composer);
        Assert.Equal(ScoreFieldOrigin.Legacy, entry.OriginOf("composer"));
        Assert.Equal(CatalogMergeStatus.NoChanges, result.Status);
    }
}
