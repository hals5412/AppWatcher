using AppWatcher.Core;
using Microsoft.Win32;

namespace AppWatcher.Elevated;

internal sealed class ElevatedApplicationContext : ApplicationContext
{
    private readonly SupervisorEngine _engine;

    public ElevatedApplicationContext(SupervisorEngine engine)
    {
        _engine = engine;
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _engine.BeginSystemShutdown();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        base.ExitThreadCore();
    }
}
