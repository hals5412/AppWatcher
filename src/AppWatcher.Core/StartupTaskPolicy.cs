namespace AppWatcher.Core;

// Task Scheduler の既定優先度 7 は BelowNormal で、監視対象として起動した子プロセスにも引き継がれる。
public static class StartupTaskPolicy
{
    // 4〜6 は Normal 優先度クラスに対応する。
    public const int TaskPriority = 5;

    // Priority 要素を省略した既存タスクは既定値 7 として扱われる。
    public const int TaskSchedulerDefaultPriority = 7;

    public static bool LaunchesTargetsAtNormalPriority(int taskPriority) => taskPriority <= 6;
}
