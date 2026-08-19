using Flipper.Core.Update;

namespace Flipper.Setup;

public static class Program
{
    public static int Main(string[] args)
    {
        if (!InPlaceInstaller.TryParseArgs(args, out var target, out var zip, out var waitPid, out var timeoutSec))
        {
            return 1;
        }

        if (waitPid is int pid && !InPlaceInstaller.WaitForProcess(pid, TimeSpan.FromSeconds(timeoutSec)))
        {
            return 2;
        }

        return InPlaceInstaller.Extract(zip, target) ? 0 : 3;
    }
}
