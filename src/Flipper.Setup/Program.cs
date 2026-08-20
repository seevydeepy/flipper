using Flipper.Core.Update;

namespace Flipper.Setup;

public static class Program
{
    public static int Main(string[] args)
    {
        if (InPlaceInstaller.TryParseArgs(args, out var target, out var zip, out var waitPid, out var timeoutSec))
        {
            if (waitPid is int pid && !InPlaceInstaller.WaitForProcess(pid, TimeSpan.FromSeconds(timeoutSec)))
            {
                return 2;
            }

            return InPlaceInstaller.Extract(zip, target) ? 0 : 3;
        }

        if (args.Length == 0)
        {
            return FirstInstall.Run();
        }

        Console.WriteLine("Carousel.Setup.exe --target <dir> --zip <payload.zip> [--wait-pid <pid>] [--timeout-sec 60]");
        Console.WriteLine("With no arguments, install Carousel next to this file.");
        return 1;
    }
}
