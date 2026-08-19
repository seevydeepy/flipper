using System.Runtime.InteropServices;

namespace Flipper.Core.Update;

public static class RuntimeRid
{
    public static string? Current
    {
        get
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null
            };
        }
    }
}
