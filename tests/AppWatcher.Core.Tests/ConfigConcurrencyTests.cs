using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class ConfigConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentProcessesSerializeFirstLoadMigrationAndUpdates(bool migrate)
    {
        using var directory = new AuditDirectory();
        if (migrate) await File.WriteAllTextAsync(Path.Combine(directory.Path, "config.json"), "{\"schemaVersion\":1,\"applications\":[],\"global\":{}}", TestContext.Current.CancellationToken);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AppWatcher.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = typeof(ConfigConcurrencyTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var worker = Path.Combine(root.FullName, "tests", "AppWatcher.TestWorker", "bin", configuration, "net10.0-windows10.0.19041.0", "AppWatcher.TestWorker.dll");
        Process Start(string mode)
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(worker); info.ArgumentList.Add(directory.Path); info.ArgumentList.Add(mode);
            return Process.Start(info)!;
        }
        using var first = Start("retention"); using var second = Start("refresh");
        await Task.WhenAll(first.WaitForExitAsync(TestContext.Current.CancellationToken), second.WaitForExitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, first.ExitCode); Assert.Equal(0, second.ExitCode);
        var config = await new ConfigService(directory.Path).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(110, config.Global.EventRetentionDays);
        Assert.Equal(22, config.Global.UiRefreshSeconds);
        foreach (var file in Directory.GetFiles(directory.Path, "*.json"))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        }
    }

    [Fact]
    public async Task SavingRecoveredConfigurationKeepsGoodBackup()
    {
        using var directory = new AuditDirectory(); var service = new ConfigService(directory.Path);
        await service.SaveAsync(new AppWatcherConfiguration(), TestContext.Current.CancellationToken);
        await service.UpdateAsync(c => c.Global.EventRetentionDays = 50, TestContext.Current.CancellationToken);
        var backup = await File.ReadAllTextAsync(Path.Combine(directory.Path, "config.backup-1.json"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "config.json"), "broken", TestContext.Current.CancellationToken);
        await service.UpdateAsync(c => c.Global.UiRefreshSeconds = 7, TestContext.Current.CancellationToken);
        Assert.Equal(backup, await File.ReadAllTextAsync(Path.Combine(directory.Path, "config.backup-1.json"), TestContext.Current.CancellationToken));
        Assert.Equal(7, (await service.LoadAsync(TestContext.Current.CancellationToken)).Global.UiRefreshSeconds);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLockDoesNotRecoverOrModifyConfig()
    {
        using var directory = new AuditDirectory(); var service = new ConfigService(directory.Path);
        await service.LoadAsync(TestContext.Current.CancellationToken);
        var path = Path.Combine(directory.Path, "config.json");
        var before = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        using var lease = new FileStream(Path.Combine(directory.Path, "config.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancel = new CancellationTokenSource();
        var read = service.LoadAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(before, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(directory.Path, "appwatcher-fallback.log")));
    }
}
