using AppWatcher.Core;
using Microsoft.Win32;

namespace AppWatcher.Elevated;

internal sealed class ElevatedApplicationContext : ApplicationContext
{
    private readonly SupervisorEngine _engine;
    private readonly WindowsFormsSynchronizationContext _uiContext = new();
    private bool _exiting;

    public ElevatedApplicationContext(SupervisorEngine engine)
    {
        _engine = engine;
        // IPC shutdown requests complete on a pipe worker thread; marshal ExitThread to the
        // message-loop thread without waking the process on a polling timer.
        _engine.ExitRequestedTask.ContinueWith(
            _ => PostExit(),
            TaskScheduler.Default);
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _engine.BeginSystemShutdown();
    }

    private void PostExit()
    {
        try { _uiContext.Post(_ => ExitThread(), null); }
        catch { /* The message loop has already ended. */ }
    }

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.ExitThreadCore();
    }
}
