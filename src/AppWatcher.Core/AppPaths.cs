namespace AppWatcher.Core;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppWatcher");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public static string EventDatabase => Path.Combine(DataDirectory, "events.db");
    public static string FallbackLog => Path.Combine(DataDirectory, "appwatcher-fallback.log");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);
}
