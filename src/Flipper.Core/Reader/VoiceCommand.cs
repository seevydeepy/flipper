namespace Flipper.Core.Reader;

public enum VoiceCommand
{
    None,
    Next,
    Back,
    Restart,
    Finish
}

public static class VoiceCommandParser
{
    public static VoiceCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return VoiceCommand.None;
        }

        var normalised = text.Trim().Replace('_', ' ').ToLowerInvariant();
        if (HasWord(normalised, "finish"))
        {
            return VoiceCommand.Finish;
        }

        if (HasWord(normalised, "restart")
            || HasWord(normalised, "beginning"))
        {
            return VoiceCommand.Restart;
        }

        if (HasWord(normalised, "back")
            || HasWord(normalised, "previous"))
        {
            return VoiceCommand.Back;
        }

        if (HasWord(normalised, "flip")
            || HasWord(normalised, "next")
            || HasWord(normalised, "page"))
        {
            return VoiceCommand.Next;
        }

        return VoiceCommand.None;
    }

    private static bool HasWord(string text, string word)
    {
        var start = 0;
        while (start <= text.Length - word.Length)
        {
            var index = text.IndexOf(word, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var beforeOk = index == 0 || !char.IsLetter(text[index - 1]);
            var after = index + word.Length;
            var afterOk = after == text.Length || !char.IsLetter(text[after]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }
}
