using System.Runtime.InteropServices;

namespace Flipper.Setup;

internal static class ExplorerHost
{
    public static bool OpenedFromExplorer()
    {
        var list = new uint[2];
        var count = GetConsoleProcessList(list, (uint)list.Length);
        return count <= 1;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
