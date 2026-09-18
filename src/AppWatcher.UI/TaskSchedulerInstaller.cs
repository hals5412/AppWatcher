using AppWatcher.Core;
using System.Security.Principal;

namespace AppWatcher.UI;

internal sealed record StartupTaskInfo(
    bool Installed,
    bool Enabled,
    int State,
    int LastTaskResult,
    string? ExecutablePath,
    string? WorkingDirectory,
    string? Error = null);

internal sealed record StartupTasksStatus(
    StartupTaskInfo Agent,
    StartupTaskInfo Elevated);

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
            throw new FileNotFoundException(Localization.T("StartupMissingExecutables"));
        }

        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                          ?? throw new PlatformNotSupportedException(Localization.T("TaskSchedulerUnavailable"));
        dynamic service = Activator.CreateInstance(serviceType)
                          ?? throw new InvalidOperationException(Localization.T("TaskSchedulerCreateFailed"));
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
        TryRun(folder, "Agent");
        TryRun(folder, "Elevated");

        return Localization.T("StartupInstallSuccess");
    }

    public static StartupTasksStatus GetStatus()
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null)
            {
                var unavailable = new StartupTaskInfo(
                    false, false, 0, 0, null, null, Localization.T("TaskSchedulerUnavailable"));
                return new StartupTasksStatus(unavailable, unavailable);
            }

            dynamic service = Activator.CreateInstance(serviceType)
                              ?? throw new InvalidOperationException(Localization.T("TaskSchedulerCreateFailed"));
            service.Connect();

            dynamic folder;
            try
            {
                folder = service.GetFolder("\\AppWatcher");
            }
            catch
            {
                var missing = new StartupTaskInfo(false, false, 0, 0, null, null);
                return new StartupTasksStatus(missing, missing);
            }

            return new StartupTasksStatus(
                ReadStatus(folder, "Agent"),
                ReadStatus(folder, "Elevated"));
        }
        catch (Exception ex)
        {
            var failed = new StartupTaskInfo(false, false, 0, 0, null, null, ex.Message);
            return new StartupTasksStatus(failed, failed);
        }
    }

    private static StartupTaskInfo ReadStatus(dynamic folder, string taskName)
    {
        try
        {
            dynamic task = folder.GetTask(taskName);
            string? executablePath = null;
            string? workingDirectory = null;

            try
            {
                dynamic definition = task.Definition;
                dynamic action = definition.Actions.Item(1);
                executablePath = action.Path as string;
                workingDirectory = action.WorkingDirectory as string;
            }
            catch
            {
                // Keep the task status usable even if an unexpected action type is present.
            }

            return new StartupTaskInfo(
                true,
                (bool)task.Enabled,
                (int)task.State,
                (int)task.LastTaskResult,
                executablePath,
                workingDirectory);
        }
        catch
        {
            return new StartupTaskInfo(false, false, 0, 0, null, null);
        }
    }

    public static (bool Agent, bool Elevated) TryStartRegisteredHosts() =>
        (TryStartRegisteredHost(PrivilegeLevel.Normal),
         TryStartRegisteredHost(PrivilegeLevel.Administrator));

    public static bool TryStartRegisteredHost(PrivilegeLevel privilege)
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;
            dynamic service = Activator.CreateInstance(serviceType);
            if (service is null) return false;
            service.Connect();
            dynamic folder = service.GetFolder("\\AppWatcher");
            return TryRun(folder, privilege == PrivilegeLevel.Administrator ? "Elevated" : "Agent");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRun(dynamic folder, string taskName)
    {
        try
        {
            folder.GetTask(taskName).Run(null);
            return true;
        }
        catch
        {
            return false;
        }
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
