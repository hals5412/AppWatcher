using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AppWatcher.Core;

// 権限・ユーザー・セッションまで一致した場合だけ同じ監視対象とみなす。
public sealed record ProcessIdentity(string ExecutablePath, PrivilegeLevel Privilege, string? UserSid, int SessionId);

public sealed class ProcessMatcher
{
    private readonly string? _currentUserSid = CurrentUserSid();
    private readonly int _currentSessionId = CurrentSessionId();

    public Process? FindExisting(ApplicationDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ExecutablePath)) return null;

        var processName = Path.GetFileNameWithoutExtension(definition.ExecutablePath);
        if (string.IsNullOrWhiteSpace(processName)) return null;

        foreach (var process in Process.GetProcessesByName(processName))
        {
            var matched = false;
            try
            {
                // QueryFullProcessImageName needs only PROCESS_QUERY_LIMITED_INFORMATION and,
                // unlike MainModule, does not read the target's module list.
                matched = RunningProcessDiscovery.TryGetIdentity(process.Id, process.SessionId, out var identity) &&
                          IsMatch(identity, definition, _currentUserSid, _currentSessionId);
                if (matched) return process;
            }
            catch
            {
                // Processes can exit or become inaccessible while enumerating.
            }
            finally
            {
                if (!matched) process.Dispose();
            }
        }

        return null;
    }

    public static bool IsMatch(ProcessIdentity candidate, ApplicationDefinition definition, string? currentUserSid, int currentSessionId)
    {
        if (candidate.SessionId != currentSessionId) return false;
        if (candidate.Privilege != definition.Privilege) return false;
        if (currentUserSid is not null && !string.Equals(candidate.UserSid, currentUserSid, StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(NormalizePath(candidate.ExecutablePath), NormalizePath(definition.ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string? CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch { return null; }
    }

    private static int CurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }
}

public sealed class InteractiveProcessLauncher
{
    public Process Launch(ApplicationDefinition definition)
    {
        if (!File.Exists(definition.ExecutablePath))
        {
            throw new FileNotFoundException("Executable not found.", definition.ExecutablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = definition.ExecutablePath,
            Arguments = definition.Arguments ?? string.Empty,
            WorkingDirectory = definition.EffectiveWorkingDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };

        // Do not use CREATE_NO_WINDOW, DETACHED_PROCESS, hidden desktops, Job Objects,
        // or service/session-0 launch paths. The child must behave like a normal
        // interactive desktop application and must be free to create its own GUI children.
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
        RaiseInheritedLowPriority(process);
        return process;
    }

    // A host started by an older startup task runs BelowNormal, which the child inherits.
    // Re-registering the task is the real fix; this keeps restarted targets usable meanwhile.
    private static void RaiseInheritedLowPriority(Process process)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            if (current.PriorityClass is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle &&
                process.PriorityClass == current.PriorityClass)
            {
                process.PriorityClass = ProcessPriorityClass.Normal;
            }
        }
        catch
        {
            // The target may have exited or changed its own priority already.
        }
    }
}

public static class WindowResponsiveness
{
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;

    public static bool IsResponsive(IntPtr windowHandle, uint timeoutMilliseconds = 1000)
    {
        if (windowHandle == IntPtr.Zero) return true;
        var result = SendMessageTimeout(
            windowHandle,
            WmNull,
            UIntPtr.Zero,
            IntPtr.Zero,
            SmtoAbortIfHung,
            timeoutMilliseconds,
            out _);
        return result != IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);
}

public static class ProcessDiagnostics
{
    public static object Describe(Process process, ApplicationDefinition definition)
    {
        string? path = null;
        try { path = process.MainModule?.FileName; } catch { }
        DateTime? started = null;
        try { started = process.StartTime.ToUniversalTime(); } catch { }

        return new
        {
            processId = process.Id,
            path,
            sessionId = SafeSessionId(process),
            startedUtc = started,
            launchMode = "Interactive",
            workingDirectory = definition.EffectiveWorkingDirectory,
            privilege = definition.Privilege.ToString(),
            childProcessPolicy = definition.ChildProcessPolicy.ToString()
        };
    }

    private static int? SafeSessionId(Process process)
    {
        try { return process.SessionId; }
        catch { return null; }
    }
}
