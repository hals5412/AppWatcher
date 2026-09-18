using AppWatcher.Core;
using Microsoft.Win32;

namespace AppWatcher.Elevated;

internal sealed class ElevatedApplicationContext : ApplicationContext
{
    private readonly SupervisorEngine _engine;
    private readonly System.Windows.Forms.Timer _exitTimer;

    public ElevatedApplicationContext(SupervisorEngine engine)
    {
        _engine = engine;
        _exitTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _exitTimer.Tick += (_, _) =>
        {
            if (_engine.ExitRequested) ExitThread();
        };
        _exitTimer.Start();
        SystemEvents.SessionEnding += OnSessionEnding;
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _engine.BeginSystemShutdown();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionEnding -= OnSessionEnding;
        _exitTimer.Stop();
        _exitTimer.Dispose();
        base.ExitThreadCore();
    }
}
