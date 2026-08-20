using Flipper.Core.Library;

namespace Flipper.Core.Tests;

public sealed class ScoreFactInferenceTests
{
    [Fact]
    public void Infer_UsesUsefulEmbeddedMetadata()
    {
        var facts = ScoreFactInference.Infer(
            "rubbish123",
            new ScoreMetadata("Clair de Lune", "Claude Debussy", "Suite bergamasque"),
            []);

        Assert.Equal("Clair de Lune", facts.Title);
        Assert.Equal("Claude Debussy", facts.Composer);
        Assert.Equal("Suite bergamasque", facts.Subtitle);
    }

    [Fact]
    public void Infer_ReadsWorkComposerAndSubtitleFromPageLines()
    {
        var facts = ScoreFactInference.Infer(
            "Schindlers List - Main Theme Piano Version",
            default,
            ["John Williams", "(Main Theme)", "Schindler's List", "1993"]);

        Assert.Equal("Schindler's List", facts.Title);
        Assert.Equal("John Williams", facts.Composer);
        Assert.Equal("Main Theme", facts.Subtitle);
    }

    [Fact]
    public void Infer_UsesBylineAndParentheticalSubtitleWhenMetadataIsMissing()
    {
        var facts = ScoreFactInference.Infer(
            "Dawn Pride and Prejudice Music by Dario Marianelli",
            default,
            ["Music by", "Dario Marianelli", "Dawn", "(from Pride and Prejudice)"]);

        Assert.Equal("Dawn", facts.Title);
        Assert.Equal("Dario Marianelli", facts.Composer);
        Assert.Equal("from Pride and Prejudice", facts.Subtitle);
    }

    [Fact]
    public void Infer_DoesNotUseStandaloneCreditLabelAsTitle()
    {
        var facts = ScoreFactInference.Infer(
            "rubbish123",
            default,
            ["Music by", "Dario Marianelli", "Dawn"]);

        Assert.Equal("Dawn", facts.Title);
        Assert.Equal("Dario Marianelli", facts.Composer);
    }

    [Fact]
    public void Infer_RejectsJunkMetadataAndFallsBackToCleanFileName()
    {
        var facts = ScoreFactInference.Infer(
            "rubbish123",
            new ScoreMetadata("Untitled1", "Piano", "Copyright 2026"),
            []);

        Assert.Equal("Rubbish 123", facts.Title);
        Assert.Null(facts.Composer);
        Assert.Null(facts.Subtitle);
    }

    [Fact]
    public void HasUsefulPageText_RequiresEnoughLettersAndATitleCandidate()
    {
        Assert.False(ScoreFactInference.HasUsefulPageText("Air", ["Air"]));
        Assert.True(ScoreFactInference.HasUsefulPageText(
            "Schindlers List Main Theme",
            ["John Williams", "Main Theme", "Schindler's List"]));
    }
}
