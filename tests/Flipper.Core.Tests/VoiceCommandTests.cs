using Flipper.Core.Reader;

namespace Flipper.Core.Tests;

public sealed class VoiceCommandTests
{
    [Theory]
    [InlineData("flip", VoiceCommand.Next)]
    [InlineData("TURN", VoiceCommand.Next)]
    [InlineData("next", VoiceCommand.Next)]
    [InlineData("page", VoiceCommand.Next)]
    [InlineData("next page", VoiceCommand.Next)]
    [InlineData("back", VoiceCommand.Back)]
    [InlineData("restart", VoiceCommand.Restart)]
    [InlineData("finish", VoiceCommand.Finish)]
    [InlineData("end", VoiceCommand.Finish)]
    [InlineData("END", VoiceCommand.Finish)]
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
    public void Parse_FinishBeatsNextInSameUtterance()
    {
        Assert.Equal(VoiceCommand.Finish, VoiceCommandParser.Parse("next finish"));
    }
}
