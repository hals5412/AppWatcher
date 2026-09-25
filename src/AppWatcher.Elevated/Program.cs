using AppWatcher.Core;

namespace AppWatcher.Elevated;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var guard = new SingleInstanceGuard("Elevated");
        if (!guard.IsOwner) return;

        CrashLogging.Install("Elevated");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLogging.Write("Elevated", "ThreadException", e.Exception);

        AppPaths.EnsureDataDirectory();
        var config = new ConfigService();
        var eventStore = new EventStore();
        var engine = new SupervisorEngine(PrivilegeLevel.Administrator, config, eventStore);
        var pipeServer = new SupervisorPipeServer(engine);
        var watchdog = new PeerHostWatchdog(
            PrivilegeLevel.Normal,
            () => engine.IsShuttingDown || engine.ExitRequested,
            engine.Events);

        try
        {
            var loadedConfig = config.LoadAsync().GetAwaiter().GetResult();
            Localization.Apply(loadedConfig.Global.Language);
            ApplicationConfiguration.Initialize();
            engine.StartAsync().GetAwaiter().GetResult();
            pipeServer.Start();
            watchdog.Start();
            Application.Run(new ElevatedApplicationContext(engine));
        }
        catch (Exception ex)
        {
            TryWriteFatal(ex);
        }
        finally
        {
            watchdog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void TryWriteFatal(Exception ex)
    {
        try
        {
            new AppWatcher.Core.FallbackLog(AppPaths.FallbackLog).WriteAsync($"{DateTimeOffset.UtcNow:O}\tCritical\tElevatedFatal\t{ex}{Environment.NewLine}").GetAwaiter().GetResult();
        }
        catch { }
    }
}
