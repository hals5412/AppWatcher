using AppWatcher.Core;

namespace AppWatcher.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var guard = new SingleInstanceGuard("Agent");
        if (!guard.IsOwner) return;

        AppPaths.EnsureDataDirectory();
        var config = new ConfigService();
        var loadedConfig = config.LoadAsync().GetAwaiter().GetResult();
        Localization.Apply(loadedConfig.Global.Language);
        ApplicationConfiguration.Initialize();
        var eventStore = new EventStore();
        var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, eventStore);
        var pipeServer = new SupervisorPipeServer(engine);

        try
        {
            engine.StartAsync().GetAwaiter().GetResult();
            pipeServer.Start();
            Application.Run(new TrayApplicationContext(engine));
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
                $"{DateTimeOffset.UtcNow:O}\tCritical\tAgentFatal\t{ex}{Environment.NewLine}");
        }
        catch { }
    }
}
