using System.Runtime.Versioning;
using Flipper.Core.Update;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (TryUninstallArgs(args, out var quiet, out var waitPid, out var invalid))
        {
            if (invalid)
            {
                PrintUsage();
                return 1;
            }

            return Uninstall.Run(quiet, waitPid);
        }

        if (InPlaceInstaller.TryParseArgs(args, out var target, out var zip, out var waitForPid, out var timeoutSec))
        {
            if (waitForPid is int pid && !InPlaceInstaller.WaitForProcess(pid, TimeSpan.FromSeconds(timeoutSec)))
            {
                return 2;
            }

            if (!InPlaceInstaller.Extract(zip, target))
            {
                return 3;
            }

            return RegisteredInstall.TryRefresh(target, zip) ? 0 : 3;
        }

        if (args.Length == 0)
        {
            return FirstInstall.Run();
        }

        PrintUsage();
        return 1;
    }

    private static bool TryUninstallArgs(string[] args, out bool quiet, out int? waitPid, out bool invalid)
    {
        quiet = false;
        waitPid = null;
        invalid = false;
        if (args.Length == 0 || args[0] != "--uninstall")
        {
            return false;
        }

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quiet":
                    quiet = true;
                    break;
                case "--wait-pid":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var pid))
                    {
                        invalid = true;
                        return true;
                    }

                    waitPid = pid;
                    break;
                default:
                    invalid = true;
                    return true;
            }
        }

        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Carousel.Setup.exe --target <dir> --zip <payload.zip> [--wait-pid <pid>] [--timeout-sec 60]");
        Console.WriteLine("Carousel.Setup.exe --uninstall");
        Console.WriteLine("With no arguments, open the Carousel setup wizard.");
    }
}
