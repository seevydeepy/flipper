using System.Runtime.Versioning;
using System.Windows.Forms;
using Flipper.Core.Update;

namespace Flipper.Setup;

[SupportedOSPlatform("windows")]
internal static class FirstInstall
{
    public static int Run()
    {
        var setupPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(setupPath))
        {
            NativeDialog.Error("Could not find the setup file path.");
            return 4;
        }

        if (!FirstInstallPaths.TryResolveRid(setupPath, RuntimeRid.Current, out _))
        {
            NativeDialog.Error("No installer for this PC.");
            return 4;
        }

        ApplicationConfiguration.Initialize();
        using var wizard = new InstallWizard();
        Application.Run(wizard);
        return wizard.ExitCode;
    }
}
