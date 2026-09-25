namespace AppWatcher.Core;

// 登録済みの自動起動タスクを実行する。Elevatedタスクは最上位の特権で登録済みなので、
// 通常権限のプロセスから実行してもUACの確認は出ない。
public static class StartupTaskRunner
{
    public const string FolderPath = "\\AppWatcher";

    public static string TaskName(PrivilegeLevel privilege) =>
        privilege == PrivilegeLevel.Administrator ? "Elevated" : "Agent";

    public static bool TryRun(PrivilegeLevel privilege)
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;

            dynamic? service = Activator.CreateInstance(serviceType);
            if (service is null) return false;

            service.Connect();
            dynamic folder = service.GetFolder(FolderPath);
            folder.GetTask(TaskName(privilege)).Run(null);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
