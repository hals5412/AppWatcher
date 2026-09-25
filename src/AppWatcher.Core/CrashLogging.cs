namespace AppWatcher.Core;

// 捕捉されなかった例外をfallbackログへ残し、異常終了の原因を後から追えるようにする。
public static class CrashLogging
{
    public static void Install(string component)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(component, "UnhandledException", e.ExceptionObject as Exception, e.IsTerminating ? "Critical" : "Error");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(component, "UnobservedTaskException", e.Exception, "Error");
            e.SetObserved();
        };
    }

    public static void Write(string component, string kind, Exception? exception, string level = "Error")
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            new FallbackLog(AppPaths.FallbackLog)
                .WriteAsync($"{DateTimeOffset.UtcNow:O}\t{level}\t{component}{kind}\t{exception}")
                .GetAwaiter().GetResult();
        }
        catch
        {
            // ログ障害で終了処理を妨げない。
        }
    }
}
