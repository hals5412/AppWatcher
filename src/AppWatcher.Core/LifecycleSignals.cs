using System.Security.Principal;

namespace AppWatcher.Core;

public static class LifecycleSignals
{
    private static string UiExitEventName => $"Local\\AppWatcher.UI.Exit.{CurrentIdentityKey()}";

    public static EventWaitHandle CreateUiExitEvent() =>
        new(false, EventResetMode.AutoReset, UiExitEventName);

    public static void RequestUiExit()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(UiExitEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No dashboard is currently open.
        }
        catch
        {
            // UI signalling is best-effort and must never block host shutdown.
        }
    }

    private static string CurrentIdentityKey()
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch
        {
            identity = Environment.UserName;
        }

        return new string(identity.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
    }
}
