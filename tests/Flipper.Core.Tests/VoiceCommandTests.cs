using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class VoiceCommandTests
{
    [Theory]
    [InlineData("flip", VoiceCommand.Next)]
    [InlineData("next", VoiceCommand.Next)]
    [InlineData("page", VoiceCommand.Next)]
    [InlineData("next page", VoiceCommand.Next)]
    [InlineData("back", VoiceCommand.Back)]
    [InlineData("previous", VoiceCommand.Back)]
    [InlineData("restart", VoiceCommand.Restart)]
    [InlineData("beginning", VoiceCommand.Restart)]
    [InlineData("finish", VoiceCommand.Finish)]
    public void Parse_KnownKeyword_MapsToCommand(string text, VoiceCommand expected)
    {
        Assert.Equal(expected, VoiceCommandParser.Parse(text));
    }

    [Fact]
    public void Parse_Blank_IsNone()
    {
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse(null));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse(""));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("   "));
    }

    [Fact]
    public void Parse_EndInsideAnotherWord_IsNone()
    {
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("friend"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("pages"));
    }

    [Fact]
    public void Parse_PrunedNearWords_AreNone()
    {
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("turn"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("end"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("start"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("begin"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("first"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("again"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("reset"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("backward"));
        Assert.Equal(VoiceCommand.None, VoiceCommandParser.Parse("rewind"));
    }

    [Fact]
    public void Parse_FinishBeatsNextInSameUtterance()
    {
        Assert.Equal(VoiceCommand.Finish, VoiceCommandParser.Parse("next finish"));
    }
}
