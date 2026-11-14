using System.Windows.Forms;

namespace Flipper.WinkLab;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["replay", var input, var report])
        {
            try
            {
                TrialReplay.RunAsync(input, report).GetAwaiter().GetResult();
                Console.WriteLine($"Replay saved to {Path.GetFullPath(report)}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        if (args.Length != 0 && args is not ["--output", _])
        {
            Console.Error.WriteLine("Usage: Flipper.WinkLab [--output SESSION_FOLDER] | replay SESSION_OR_ZIP REPORT.json");
            return 2;
        }

        var directory = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Flipper", "WinkLab", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        ApplicationConfiguration.Initialize();
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "trial-*.zip").Any())
        {
            MessageBox.Show("Choose a new recording folder. This folder already contains attempts.",
                "Flipper wink lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }
        Application.Run(new LabWindow(directory));
        return 0;
    }
}
