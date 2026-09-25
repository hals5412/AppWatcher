using System.Diagnostics;

namespace AppWatcher.Core;

// OS依存の操作を分離し、回帰テストでは実際のアプリを起動・終了しない。
public interface IManagedProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    DateTimeOffset StartedUtc { get; }
    event EventHandler? Exited;
    void ObserveExit();
    bool? IsWindowResponsive();
    bool CloseMainWindow();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IProcessRuntime
{
    IManagedProcess? FindExisting(ApplicationDefinition definition);
    IManagedProcess Launch(ApplicationDefinition definition);

    // 定期探索1回分でプロセス一覧を共有し、監視対象ごとに全プロセスを列挙しない。
    IProcessDiscoveryScope CreateDiscoveryScope() => new PassThroughDiscoveryScope(this);
}

public interface IProcessDiscoveryScope : IDisposable
{
    IManagedProcess? FindExisting(ApplicationDefinition definition);
}

internal sealed class PassThroughDiscoveryScope(IProcessRuntime runtime) : IProcessDiscoveryScope
{
    public IManagedProcess? FindExisting(ApplicationDefinition definition) => runtime.FindExisting(definition);
    public void Dispose() { }
}

public sealed class WindowsProcessRuntime : IProcessRuntime
{
    public IManagedProcess? FindExisting(ApplicationDefinition definition)
    {
        var process = new ProcessMatcher().FindExisting(definition);
        return process is null ? null : new ManagedProcess(process);
    }

    public IManagedProcess Launch(ApplicationDefinition definition) =>
        new ManagedProcess(new InteractiveProcessLauncher().Launch(definition));

    public IProcessDiscoveryScope CreateDiscoveryScope() => new SnapshotDiscoveryScope();

    private sealed class SnapshotDiscoveryScope : IProcessDiscoveryScope
    {
        private readonly ProcessMatcher _matcher = new();
        private readonly Lazy<Dictionary<string, List<Process>>> _byName = new(() =>
            Process.GetProcesses()
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase));

        public IManagedProcess? FindExisting(ApplicationDefinition definition)
        {
            var name = Path.GetFileNameWithoutExtension(definition.ExecutablePath);
            if (string.IsNullOrWhiteSpace(name) || !_byName.Value.TryGetValue(name, out var candidates)) return null;
            var process = _matcher.FindMatch(candidates, definition);
            if (process is null) return null;
            // 返したProcessは監視側が所有するため、スコープの破棄対象から外す。
            candidates.Remove(process);
            return new ManagedProcess(process);
        }

        public void Dispose()
        {
            if (!_byName.IsValueCreated) return;
            foreach (var process in _byName.Value.Values.SelectMany(list => list)) process.Dispose();
        }
    }

    private sealed class ManagedProcess(Process process) : IManagedProcess
    {
        public int Id => process.Id;
        public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }
        public int? ExitCode { get { try { return process.ExitCode; } catch { return null; } } }
        public DateTimeOffset StartedUtc { get { try { return process.StartTime.ToUniversalTime(); } catch { return DateTimeOffset.UtcNow; } } }
        public event EventHandler? Exited;
        public void ObserveExit()
        {
            process.Exited += OnExited;
            process.EnableRaisingEvents = true;
            if (HasExited) Exited?.Invoke(this, EventArgs.Empty);
        }
        private void OnExited(object? sender, EventArgs e) => Exited?.Invoke(this, e);
        public bool? IsWindowResponsive()
        {
            process.Refresh();
            var window = process.MainWindowHandle;
            return window == IntPtr.Zero ? null : WindowResponsiveness.IsResponsive(window);
        }
        public bool CloseMainWindow() => process.CloseMainWindow();
        public void Kill() => process.Kill(entireProcessTree: false);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose()
        {
            process.Exited -= OnExited;
            process.Dispose();
        }
    }
}
