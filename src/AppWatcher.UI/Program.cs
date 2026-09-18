using System.Diagnostics;
using AppWatcher.Core;

namespace AppWatcher.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppPaths.EnsureDataDirectory();
        var configService = new ConfigService();
        var config = configService.LoadAsync().GetAwaiter().GetResult();
        Localization.Apply(config.Global.Language);
        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new System.Drawing.Font("Segoe UI", 10F));

        if (args.Any(a => string.Equals(a, "--install-startup", StringComparison.OrdinalIgnoreCase)))
        {
            RunStartupInstaller();
            return;
        }

        if (args.Any(a => string.Equals(a, "--shutdown", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = HostLifecycle.ShutdownAllAsync(closeDashboard: true).GetAwaiter().GetResult() ? 0 : 1;
            return;
        }

        if (args.Any(a => string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = HostLifecycle.RestartHostsAsync().GetAwaiter().GetResult() ? 0 : 1;
            return;
        }

        using var guard = new SingleInstanceGuard("UI");
        if (!guard.IsOwner) return;
        Application.Run(new MainForm());
    }

    public static void RequestStartupInstallation(IWin32Window owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = "--install-startup",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC was cancelled by the user.
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, Localization.T("StartupSetupTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RunStartupInstaller()
    {
        try
        {
            var result = TaskSchedulerInstaller.InstallAndStart();
            MessageBox.Show(result, Localization.T("StartupSetupTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Localization.F("StartupInstallFailed", ex),
                Localization.T("StartupSetupTitle"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
