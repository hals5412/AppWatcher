using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AppWatcher.Core;

internal static class RunningProcessDiscovery
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static IReadOnlyList<RunningProcessInfo> EnumerateCurrentSession()
    {
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var currentProcessId = Environment.ProcessId;
        var found = new Dictionary<string, RunningProcessInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentProcessId || process.SessionId != currentSessionId)
                {
                    continue;
                }

                if (!TryInspectProcess(process.Id, out var executablePath, out var privilege))
                {
                    continue;
                }

                var fileName = Path.GetFileName(executablePath);
                if (fileName.StartsWith("AppWatcher.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(executablePath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = process.ProcessName;
                }

                string windowTitle;
                try
                {
                    windowTitle = process.MainWindowTitle ?? string.Empty;
                }
                catch
                {
                    windowTitle = string.Empty;
                }

                var key = $"{executablePath}\0{privilege}";
                if (found.TryGetValue(key, out var existing))
                {
                    found[key] = existing with
                    {
                        InstanceCount = existing.InstanceCount + 1,
                        WindowTitle = string.IsNullOrWhiteSpace(existing.WindowTitle)
                            ? windowTitle
                            : existing.WindowTitle
                    };
                    continue;
                }

                found[key] = new RunningProcessInfo(
                    process.Id,
                    name,
                    executablePath,
                    privilege,
                    windowTitle,
                    1);
            }
            catch
            {
                // Processes can exit or become inaccessible while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Privilege)
            .ThenBy(item => item.ExecutablePath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool TryInspectProcess(
        int processId,
        out string executablePath,
        out PrivilegeLevel privilege)
    {
        executablePath = string.Empty;
        privilege = PrivilegeLevel.Normal;

        var processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);

        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var pathBuffer = new StringBuilder(32768);
            var pathLength = pathBuffer.Capacity;
            if (!QueryFullProcessImageName(
                    processHandle,
                    0,
                    pathBuffer,
                    ref pathLength))
            {
                return false;
            }

            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                return false;
            }

            try
            {
                var elevation = new TokenElevation();
                if (!GetTokenInformation(
                        tokenHandle,
                        TokenInformationClass.TokenElevation,
                        out elevation,
                        Marshal.SizeOf<TokenElevation>(),
                        out _))
                {
                    return false;
                }

                executablePath = pathBuffer.ToString();
                privilege = elevation.TokenIsElevated != 0
                    ? PrivilegeLevel.Administrator
                    : PrivilegeLevel.Normal;
                return !string.IsNullOrWhiteSpace(executablePath);
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        int flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
