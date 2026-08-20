using System.Runtime.InteropServices;

namespace Flipper.Setup;

internal static class NativeDialog
{
    private const uint MbOk = 0;
    private const uint MbYesNo = 0x4;
    private const uint MbIconError = 0x10;
    private const uint MbIconQuestion = 0x20;
    private const uint MbIconInformation = 0x40;
    private const int IdYes = 6;

    public static void Info(string text) => MessageBoxW(IntPtr.Zero, text, "Carousel", MbOk | MbIconInformation);

    public static void Error(string text) => MessageBoxW(IntPtr.Zero, text, "Carousel", MbOk | MbIconError);

    public static bool YesNo(string text) => MessageBoxW(IntPtr.Zero, text, "Carousel", MbYesNo | MbIconQuestion) == IdYes;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
