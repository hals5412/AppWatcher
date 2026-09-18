using System.Security.Principal;

namespace AppWatcher.Core;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsOwner { get; }

    public SingleInstanceGuard(string componentName)
    {
        string sid;
        try { sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { sid = Environment.UserName; }
        var name = $"Local\\AppWatcher.{componentName}.{sid}";
        _mutex = new Mutex(true, name, out var createdNew);
        IsOwner = createdNew;
    }

    public void Dispose()
    {
        if (IsOwner)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
