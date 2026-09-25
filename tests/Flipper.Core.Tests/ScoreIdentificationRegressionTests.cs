using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreIdentificationRegressionTests
{
    private static ScoreFacts Infer(string fileName, ScoreMetadata metadata, params string[] lines)
    {
        return ScoreFactInference.Infer(fileName, metadata, lines);
    }

    [Fact]
    public void Tempo_DoesNotDisplaceCorroboratedTitle_OrBecomeComposer()
    {
        var facts = Infer(
            "Moonlight Sonata.pdf",
            default,
            ["Moonlight Sonata", "Composed by Ludwig van Beethoven", "Allegro con brio"]);

        Assert.Equal("Moonlight Sonata", facts.Title);
        Assert.Equal("Ludwig van Beethoven", facts.Composer);
    }

    [Fact]
    public void ExplicitPrintedCredit_BeatsGenericPdfAuthorMetadata()
    {
        var facts = Infer(
            "Moonlight Sonata scan.pdf",
            new ScoreMetadata("Sheet Music", "Someone Uploader", null),
            ["Moonlight Sonata", "Composed by Ludwig van Beethoven"]);

        Assert.Equal("Moonlight Sonata", facts.Title);
        Assert.Equal("Ludwig van Beethoven", facts.Composer);
    }

    [Fact]
    public void ArrangerOnlyEvidence_DoesNotInventComposer()
    {
        var facts = Infer("Air.pdf", default, ["Air on the G String", "Arranged by John Smith"]);

        Assert.Equal("Air on the G String", facts.Title);
        Assert.Null(facts.Composer);
    }

    [Fact]
    public void FilenameByline_ExtractsTitleAndComposer()
    {
        var facts = Infer("Dawn - Music by Dario Marianelli.pdf", default, []);

        Assert.Equal("Dawn", facts.Title);
        Assert.Equal("Dario Marianelli", facts.Composer);
    }

    [Fact]
    public void SplitLineCredit_Parses()
    {
        var facts = Infer("dawn.pdf", default, ["Music by", "Dario Marianelli", "Dawn"]);

        Assert.Equal("Dario Marianelli", facts.Composer);
    }

    [Fact]
    public void LowercaseCredit_ParsesRegardlessOfCase()
    {
        var facts = Infer("dawn.pdf", default, ["dawn", "music by dario marianelli"]);

        Assert.Equal("Dario Marianelli", facts.Composer, ignoreCase: true);
    }

    [Fact]
    public void UppercaseComposer_IsAccepted()
    {
        var facts = Infer("Clair.pdf", default, ["Clair de Lune", "Music by CLAUDE DEBUSSY"]);

        Assert.Equal("Claude Debussy", facts.Composer, ignoreCase: true);
        Assert.Equal("Clair de Lune", facts.Title);
    }

    [Fact]
    public void Photocopy_StaysIntact()
    {
        Assert.Equal("Photocopy", ScoreFactInference.CleanFileName("Photocopy.pdf"));
    }

    [Fact]
    public void CatalogueOpusAndKeys_Survive()
    {
        var facts = Infer(
            "12 - Etudes, Op. 10.pdf",
            default,
            ["12 Études, Op. 10", "Composed by Frédéric Chopin"]);

        Assert.Equal("12 Études, Op. 10", facts.Title);
        Assert.Equal("Frédéric Chopin", facts.Composer);
    }

    [Fact]
    public void AccentedNames_KeepDisplaySpelling()
    {
        var facts = Infer("Dvorak Humoresque.pdf", default, ["Humoresque", "Antonín Dvořák"]);

        Assert.Equal("Antonín Dvořák", facts.Composer);
    }

    [Fact]
    public void TitleIsNeverRecycledAsComposer()
    {
        var facts = Infer(
            "Schindlers List Main Theme Piano Version.pdf",
            default,
            ["Schindler's List", "Main Theme"]);

        Assert.Equal("Schindler's List", facts.Title);
        Assert.NotEqual("Schindler's List", facts.Composer);
    }

    [Fact]
    public void InlineComposerCatalogue_SplitsNameFromWork()
    {
        var facts = Infer(
            "Mazurka - Op6 - No1.pdf",
            default,
            ["Mazurka.", "F. Chopin. Op.6, No.1.", "À Mlle la Comtesse PAULINE PLATER."]);

        Assert.Equal("Mazurka.", facts.Title);
        Assert.Equal("F. Chopin", facts.Composer);
    }

    [Fact]
    public void InlineComposerTempo_SplitsNameFromDirection()
    {
        // "F. Sor Allegro." is one engraved credit row: the name proposes the
        // composer while the tempo tail never does.
        var facts = Infer("Grande.pdf", default, ["Grande Sonate.", "F. Sor Allegro.", "Op. 22"]);

        Assert.Equal("F. Sor", facts.Composer);
    }

    [Fact]
    public void QuotedSeriesHeader_IsNeitherTitleNorComposer()
    {
        var facts = Infer(
            "Bwv - 1003 1.pdf",
            default,
            ["Sonata II BWV 1003", "\"Sechs Sonaten für Violine\"", "Composed by Johann Sebastian Bach"]);

        Assert.Equal("Sonata II BWV 1003", facts.Title);
        Assert.Equal("Johann Sebastian Bach", facts.Composer);
    }

    [Fact]
    public void MovementHeader_DoesNotProposeComposer()
    {
        var facts = Infer(
            "Op115 1.pdf",
            new ScoreMetadata("Zwei geistliche Chöre", "Felix Mendelssohn Bartholdy", null),
            ["Zwei geistliche Chöre", "I.", "Beati mortui Felix Mendelssohn Bartholdy", "op. 115"]);

        Assert.Equal("Felix Mendelssohn Bartholdy", facts.Composer);
    }

    [Fact]
    public void RomanNumeralEngraving_IsNotAHeading()
    {
        var facts = Infer(
            "GiulianiOp100No14.pdf",
            new ScoreMetadata("24 Studies for the Guitar", "Mauro Giuliani", null),
            ["24 Studies for the Guitar", "Mauro Giuliani", "Op. 100", "No. 14. Caprice", "III"]);

        Assert.NotEqual("III", facts.Title);
        Assert.NotEqual("III", facts.Composer);
    }

    [Fact]
    public void Decision_ExplainsItself()
    {
        var decision = ScoreFactInference.InferWithEvidence(
            "Moonlight Sonata.pdf",
            default,
            ["Moonlight Sonata", "Composed by Ludwig van Beethoven", "Allegro con brio"]);

        Assert.Equal("Moonlight Sonata", decision.Facts.Title);
        Assert.True(decision.TitleVerified);
        Assert.Contains("Moonlight Sonata", decision.TitleEvidence);
        Assert.Contains("Ludwig van Beethoven", decision.ComposerEvidence);
    }

    [Fact]
    public void FilenameFallback_IsMarkedUnverified()
    {
        var decision = ScoreFactInference.InferWithEvidence("Photocopy.pdf", default, []);

        Assert.Equal("Photocopy", decision.Facts.Title);
        Assert.False(decision.TitleVerified);
    }

    [Fact]
    public void BoilerplateAndMetadataAlone_DoNotVerifyTitle()
    {
        var decision = ScoreFactInference.InferWithEvidence("scan0042.pdf",
            new ScoreMetadata("Sheet Music", "Someone Uploader", null),
            ["Published edition plate volume twelve", "John Williams"]);
        Assert.False(decision.TitleVerified);
        Assert.Equal("Scan 0042", decision.Facts.Title);
        Assert.Null(decision.Facts.Composer);
        Assert.False(ScoreFactInference.InferWithEvidence("Dawn - Music by Dario Marianelli.pdf", default, []).TitleVerified);
        Assert.False(ScoreFactInference.InferWithEvidence("scan.pdf",
            new ScoreMetadata("Dawn", "John Williams", null), []).TitleVerified);
    }

    [Fact]
    public void LoneNameWithoutRoleOrCorroboration_DoesNotBecomeComposer()
    {
        var decision = ScoreFactInference.InferWithEvidence("Dawn.pdf", default, ["Dawn", "John Williams"]);
        Assert.True(decision.TitleVerified);
        Assert.Null(decision.Facts.Composer);
    }

    [Fact]
    public void CorroboratedNameAfterUnrelatedName_Wins()
    {
        var decision = ScoreFactInference.InferWithEvidence("Dawn.pdf",
            new ScoreMetadata(null, "John Williams", null),
            ["Dawn", "Someone Else", "John Williams"]);
        Assert.Equal("John Williams", decision.Facts.Composer);
        var unsupported = ScoreFactInference.InferWithEvidence("Dawn.pdf",
            new ScoreMetadata(null, "An Uploader", null),
            ["Dawn", "Someone Else", "John Williams"]);
        Assert.Null(unsupported.Facts.Composer);
    }

    [Fact]
    public void RichLayout_IdentifiesProminentHeadingWithOpaqueFilename()
    {
        ScoreTextLine[] lines = [
            new(2, "Dawn of a new day", .2, .08, .6, .04, null, null, false, ScoreTextSource.Ocr),
            new(2, "Music by John Williams", .6, .18, .3, .015, null, null, false, ScoreTextSource.Ocr)];
        var decision = ScoreFactInference.InferRichWithEvidence("scan0042.pdf", default, lines);
        Assert.True(decision.TitleVerified);
        Assert.Equal("Dawn of a new day", decision.Facts.Title);
        Assert.Equal("John Williams", decision.Facts.Composer);
        Assert.Contains("heading", decision.TitleEvidence);
        Assert.Equal("Dawn of a new day", ScoreFactInference.InferRich("scan0042.pdf", default, lines).Title);
    }

    [Fact]
    public void EquallyProminentDifferentHeadings_Abstain()
    {
        ScoreTextLine[] lines = [
            new(1, "Dawn of a new day", .15, .08, .7, .04, 24, "Bold", true, ScoreTextSource.Embedded),
            new(1, "Night on the river", .15, .18, .7, .04, 24, "Bold", true, ScoreTextSource.Embedded),
            new(1, "Music by John Williams", .6, .3, .3, .015, 10, "Roman", false, ScoreTextSource.Embedded)];
        var decision = ScoreFactInference.InferRichWithEvidence("scan0042.pdf", default, lines);
        Assert.False(decision.TitleVerified);
        Assert.Equal("Scan 0042", decision.Facts.Title);
        Assert.NotNull(decision.TitleRunnerUp);
    }

}
