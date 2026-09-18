using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppWatcher.Core;

public sealed class ProcessMatcher
{
    public Process? FindExisting(ApplicationDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ExecutablePath)) return null;

        var targetPath = NormalizePath(definition.ExecutablePath);
        var processName = Path.GetFileNameWithoutExtension(definition.ExecutablePath);
        if (string.IsNullOrWhiteSpace(processName)) return null;

        foreach (var process in Process.GetProcessesByName(processName))
        {
            var matched = false;
            try
            {
                var path = process.MainModule?.FileName;
                matched = path is not null && string.Equals(NormalizePath(path), targetPath, StringComparison.OrdinalIgnoreCase);
                if (matched)
                {
                    return process;
                }
            }
            catch
            {
                // Access can fail for a process at a different integrity level.
            }
            finally
            {
                if (!matched) process.Dispose();
            }
        }

        return null;
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
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
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
