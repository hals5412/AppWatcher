using System.Diagnostics;
using AppWatcher.Core;

namespace AppWatcher.UI;

internal static class HostLifecycle
{
    private static readonly SupervisorClient NormalClient = new(PrivilegeLevel.Normal);
    private static readonly SupervisorClient AdminClient = new(PrivilegeLevel.Administrator);

    public static async Task<bool> ShutdownAllAsync(bool closeDashboard = false)
    {
        if (closeDashboard) LifecycleSignals.RequestUiExit();
        // Shut down the elevated helper first while the normal agent is still available.
        await RequestShutdownIfRunningAsync(AdminClient).ConfigureAwait(false);
        await RequestShutdownIfRunningAsync(NormalClient).ConfigureAwait(false);

        var adminStopped = await WaitForOfflineAsync(AdminClient, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var normalStopped = await WaitForOfflineAsync(NormalClient, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return adminStopped && normalStopped;
    }

    public static async Task<bool> RestartHostsAsync()
    {
        if (!await ShutdownAllAsync().ConfigureAwait(false)) return false;

        // Prefer the registered logon tasks. The Elevated task already has highest privileges,
        // so restarting it this way does not create another UAC prompt.
        var (agentStarted, elevatedStarted) = TaskSchedulerInstaller.TryStartRegisteredHosts();
        if (!agentStarted) agentStarted = StartDirect("AppWatcher.Agent.exe", elevated: false);
        if (!elevatedStarted) elevatedStarted = StartDirect("AppWatcher.Elevated.exe", elevated: true);

        if (!agentStarted || !elevatedStarted) return false;

        var normalOnline = await WaitForOnlineAsync(NormalClient, TimeSpan.FromSeconds(7)).ConfigureAwait(false);
        var adminOnline = await WaitForOnlineAsync(AdminClient, TimeSpan.FromSeconds(7)).ConfigureAwait(false);
        return normalOnline && adminOnline;
    }

    private static async Task RequestShutdownIfRunningAsync(SupervisorClient client)
    {
        var ping = await client.SendAsync(
            new SupervisorRequest(SupervisorCommandType.Ping),
            TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);
        if (!ping.Success) return;

        await client.SendAsync(
            new SupervisorRequest(SupervisorCommandType.ShutdownHost),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForOfflineAsync(SupervisorClient client, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ping = await client.SendAsync(
                new SupervisorRequest(SupervisorCommandType.Ping),
                TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            if (!ping.Success) return true;
            await Task.Delay(100).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForOnlineAsync(SupervisorClient client, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ping = await client.SendAsync(
                new SupervisorRequest(SupervisorCommandType.Ping),
                TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);
            if (ping.Success) return true;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return false;
    }

    private static bool StartDirect(string fileName, bool elevated)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = elevated ? "runas" : string.Empty,
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
