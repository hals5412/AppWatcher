using System.Text.Json.Serialization;

namespace AppWatcher.Core;


[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UiLanguage
{
    Auto,
    Japanese,
    English
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrivilegeLevel
{
    Normal,
    Administrator
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppRuntimeState
{
    Unknown,
    Starting,
    Healthy,
    Stopped,
    Paused,
    Unresponsive,
    Restarting,
    Backoff,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestartPolicy
{
    AnyUnexpectedExit,
    AbnormalExitOnly,
    Never
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChildProcessPolicy
{
    Unmanaged,
    TrackOnly,
    StopWithParent
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppLogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5
}

public sealed class AppWatcherConfiguration
{
    public int SchemaVersion { get; set; } = ConfigurationSchema.CurrentVersion;
    public List<ApplicationDefinition> Applications { get; set; } = [];
    public GlobalSettings Global { get; set; } = new();
}

public sealed class GlobalSettings
{
    public int EventRetentionDays { get; set; } = 90;
    public int EventDatabaseMaxMegabytes { get; set; } = 100;
    public int UiRefreshSeconds { get; set; } = 2;
    public bool CheckForUpdates { get; set; } = false;
    [JsonPropertyName("uiLanguage")]
    public UiLanguage Language { get; set; } = UiLanguage.Auto;
    public Dictionary<string, DashboardColumnLayout> DashboardColumns { get; set; } = [];
}

public sealed class DashboardColumnLayout
{
    public int Width { get; set; }
    public int DisplayIndex { get; set; }
}

public sealed class ApplicationDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public PrivilegeLevel Privilege { get; set; } = PrivilegeLevel.Normal;
    public bool MonitoringEnabled { get; set; } = true;
    public bool StartWithWatcher { get; set; } = true;
    public bool AttachExisting { get; set; } = true;
    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.AnyUnexpectedExit;
    public int RestartDelaySeconds { get; set; } = 5;
    public bool DetectHangs { get; set; } = true;
    public int HangTimeoutSeconds { get; set; } = 60;
    public int HangCheckIntervalSeconds { get; set; } = 10;
    public int StartupGraceSeconds { get; set; } = 15;
    public bool RestartLoopProtectionEnabled { get; set; } = true;
    public int MaxRestarts { get; set; } = 5;
    public int RestartWindowMinutes { get; set; } = 10;
    public int BackoffMinutes { get; set; } = 15;
    public int HealthyResetMinutes { get; set; } = 30;
    public int GracefulShutdownSeconds { get; set; } = 10;
    public bool ForceKillAfterTimeout { get; set; } = true;
    public ChildProcessPolicy ChildProcessPolicy { get; set; } = ChildProcessPolicy.Unmanaged;
    public AppLogLevel LogLevel { get; set; } = AppLogLevel.Information;

    public string EffectiveWorkingDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(WorkingDirectory))
            {
                return WorkingDirectory;
            }

            try
            {
                return Path.GetDirectoryName(ExecutablePath) ?? Environment.CurrentDirectory;
            }
            catch
            {
                return Environment.CurrentDirectory;
            }
        }
    }
}

public sealed record ApplicationSnapshot(
    Guid Id,
    string Name,
    PrivilegeLevel Privilege,
    AppRuntimeState State,
    int? ProcessId,
    DateTimeOffset? ProcessStartedUtc,
    TimeSpan? Uptime,
    int RestartCountInWindow,
    DateTimeOffset? PauseUntilUtc,
    bool PauseIndefinite,
    string LastEvent,
    string? LastReason,
    string ExecutablePath,
    bool MonitoringEnabled,
    bool DetectHangs);

public sealed record RunningProcessInfo(
    int ProcessId,
    string Name,
    string ExecutablePath,
    PrivilegeLevel Privilege,
    string WindowTitle,
    int InstanceCount);

public sealed record HostSnapshot(
    PrivilegeLevel HostPrivilege,
    int HostProcessId,
    long WorkingSetBytes,
    DateTimeOffset HostStartedUtc,
    TimeSpan HostUptime,
    bool MaintenanceActive,
    DateTimeOffset? MaintenanceUntilUtc,
    IReadOnlyList<ApplicationSnapshot> Applications,
    string Version);

public sealed record EventRecord(
    DateTimeOffset TimestampUtc,
    Guid? ApplicationId,
    string? ApplicationName,
    AppLogLevel Level,
    string EventType,
    string ReasonCode,
    string DetailsJson);

public sealed record EventQuery(
    Guid? ApplicationId = null,
    AppLogLevel? MinimumLevel = null,
    DateTimeOffset? SinceUtc = null,
    int Limit = 500);

public sealed record ConfigurationValidationResult(bool Success, IReadOnlyList<string> Messages);
