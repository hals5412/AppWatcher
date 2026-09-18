using AppWatcher.Core;

namespace AppWatcher.Elevated;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var guard = new SingleInstanceGuard("Elevated");
        if (!guard.IsOwner) return;

        ApplicationConfiguration.Initialize();
        AppPaths.EnsureDataDirectory();

        var config = new ConfigService();
        var eventStore = new EventStore();
        var engine = new SupervisorEngine(PrivilegeLevel.Administrator, config, eventStore);
        var pipeServer = new SupervisorPipeServer(engine);

        try
        {
            engine.StartAsync().GetAwaiter().GetResult();
            pipeServer.Start();
            Application.Run(new ElevatedApplicationContext(engine));
        }
        catch (Exception ex)
        {
            TryWriteFatal(ex);
        }
        finally
        {
            pipeServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void TryWriteFatal(Exception ex)
    {
        try
        {
            File.AppendAllText(AppPaths.FallbackLog,
                $"{DateTimeOffset.UtcNow:O}\tCritical\tElevatedFatal\t{ex}{Environment.NewLine}");
        }
        catch { }
    }
}
