using System.Diagnostics;
using AppWatcher.Core;

namespace AppWatcher.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        AppPaths.EnsureDataDirectory();

        if (args.Any(a => string.Equals(a, "--install-startup", StringComparison.OrdinalIgnoreCase)))
        {
            RunStartupInstaller();
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
            MessageBox.Show(owner, ex.Message, "AppWatcher startup setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void RunStartupInstaller()
    {
        try
        {
            var result = TaskSchedulerInstaller.InstallAndStart();
            MessageBox.Show(result, "AppWatcher startup setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Startup task installation failed.\n\n{ex}",
                "AppWatcher startup setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
