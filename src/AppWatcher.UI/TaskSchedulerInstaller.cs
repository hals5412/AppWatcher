using System.Security.Principal;

namespace AppWatcher.UI;

internal static class TaskSchedulerInstaller
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLua = 0;
    private const int TaskRunLevelHighest = 1;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;

    public static string InstallAndStart()
    {
        var baseDir = AppContext.BaseDirectory;
        var agent = Path.Combine(baseDir, "AppWatcher.Agent.exe");
        var elevated = Path.Combine(baseDir, "AppWatcher.Elevated.exe");

        if (!File.Exists(agent) || !File.Exists(elevated))
        {
            throw new FileNotFoundException(
                "AppWatcher.Agent.exe and AppWatcher.Elevated.exe must be in the same folder as AppWatcher.UI.exe. " +
                "Run this from a published AppWatcher folder, not from an individual project bin directory.");
        }

        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                          ?? throw new PlatformNotSupportedException("Windows Task Scheduler COM service is unavailable.");
        dynamic service = Activator.CreateInstance(serviceType)
                          ?? throw new InvalidOperationException("Could not create Task Scheduler COM service.");
        service.Connect();

        dynamic root = service.GetFolder("\\");
        dynamic folder;
        try
        {
            folder = service.GetFolder("\\AppWatcher");
        }
        catch
        {
            root.CreateFolder("AppWatcher");
            folder = service.GetFolder("\\AppWatcher");
        }

        var user = WindowsIdentity.GetCurrent().Name;
        Register(service, folder, "Agent", agent, baseDir, user, highest: false);
        Register(service, folder, "Elevated", elevated, baseDir, user, highest: true);

        // Start them through Task Scheduler so the exact configured security context is used now as well.
        try { folder.GetTask("Agent").Run(null); } catch { }
        try { folder.GetTask("Elevated").Run(null); } catch { }

        return "Startup tasks were installed for the current Windows user.\n\n" +
               "• AppWatcher Agent: interactive normal-user token\n" +
               "• AppWatcher Elevated: interactive highest-privilege token\n\n" +
               "Both run only in your logged-on desktop session, avoiding Session 0 so child GUI applications can appear normally.";
    }

    private static void Register(dynamic service, dynamic folder, string name, string executable, string workingDirectory, string user, bool highest)
    {
        dynamic task = service.NewTask(0);

        task.RegistrationInfo.Description = highest
            ? "AppWatcher elevated helper. Runs in the interactive desktop session with highest privileges."
            : "AppWatcher monitoring agent. Runs in the interactive desktop session.";

        task.Principal.UserId = user;
        task.Principal.LogonType = TaskLogonInteractiveToken;
        task.Principal.RunLevel = highest ? TaskRunLevelHighest : TaskRunLevelLua;

        task.Settings.Enabled = true;
        task.Settings.StartWhenAvailable = true;
        task.Settings.Hidden = false;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = 2; // IgnoreNew

        dynamic trigger = task.Triggers.Create(TaskTriggerLogon);
        trigger.UserId = user;
        trigger.Enabled = true;

        dynamic action = task.Actions.Create(TaskActionExec);
        action.Path = executable;
        action.WorkingDirectory = workingDirectory;

        folder.RegisterTaskDefinition(
            name,
            task,
            TaskCreateOrUpdate,
            user,
            null,
            TaskLogonInteractiveToken,
            null);
    }
}
