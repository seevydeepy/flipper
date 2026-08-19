namespace Flipper.Core.Reader;

public enum PageTurnCommand
{
    None,
    Next,
    Back,
    Close
}

public static class PageTurnKeys
{
    public const int Backspace = 0x08;
    public const int Enter = 0x0D;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int PageUp = 0x21;
    public const int PageDown = 0x22;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;

    public static PageTurnCommand FromVirtualKey(int virtualKey, bool isRepeat)
    {
        if (isRepeat)
        {
            return PageTurnCommand.None;
        }

        return virtualKey switch
        {
            Right or Down or PageDown or Space or Enter => PageTurnCommand.Next,
            Left or Up or PageUp or Backspace => PageTurnCommand.Back,
            Escape => PageTurnCommand.Close,
            _ => PageTurnCommand.None
        };
    }
}
