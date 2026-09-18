using System.Text.Json;
using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class ConfigServiceTests
{
    [Fact]
    public async Task SaveAndLoadRoundTripsConfiguration()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);
        var id = Guid.NewGuid();

        var configuration = CreateConfiguration("RoundTrip", id);
        configuration.Global.EventRetentionDays = 123;
        configuration.Global.Language = UiLanguage.Japanese;

        await service.SaveAsync(configuration, TestContext.Current.CancellationToken);
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(123, loaded.Global.EventRetentionDays);
        Assert.Equal(UiLanguage.Japanese, loaded.Global.Language);

        var app = Assert.Single(loaded.Applications);
        Assert.Equal(id, app.Id);
        Assert.Equal("RoundTrip", app.Name);
        Assert.Equal(PrivilegeLevel.Administrator, app.Privilege);
        Assert.True(app.AttachExisting);
    }

    [Fact]
    public async Task SaveRotatesThreeBackupGenerations()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);

        await service.SaveAsync(CreateConfiguration("First"), TestContext.Current.CancellationToken);
        await service.SaveAsync(CreateConfiguration("Second"), TestContext.Current.CancellationToken);
        await service.SaveAsync(CreateConfiguration("Third"), TestContext.Current.CancellationToken);
        await service.SaveAsync(CreateConfiguration("Fourth"), TestContext.Current.CancellationToken);

        Assert.Equal("Third", await ReadBackupNameAsync(temp.Path, 1));
        Assert.Equal("Second", await ReadBackupNameAsync(temp.Path, 2));
        Assert.Equal("First", await ReadBackupNameAsync(temp.Path, 3));
    }

    [Fact]
    public async Task LoadRecoversFromNewestReadableBackup()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);

        await service.SaveAsync(CreateConfiguration("KnownGood"), TestContext.Current.CancellationToken);
        await service.SaveAsync(CreateConfiguration("Current"), TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "config.json"),
            "{ definitely-not-json",
            TestContext.Current.CancellationToken);

        var recovered = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("KnownGood", Assert.Single(recovered.Applications).Name);

        var fallbackLog = Path.Combine(temp.Path, "appwatcher-fallback.log");
        Assert.True(File.Exists(fallbackLog));
        var log = await File.ReadAllTextAsync(fallbackLog, TestContext.Current.CancellationToken);
        Assert.Contains("ConfigurationRecovery", log);
        Assert.Contains("config.backup-1.json", log);
    }

    [Fact]
    public async Task FirstLoadCreatesDefaultConfiguration()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Empty(loaded.Applications);
        Assert.True(File.Exists(Path.Combine(temp.Path, "config.json")));
    }

    [Fact]
    public void ValidationRejectsMissingNameAndExecutable()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);

        var result = service.Validate(new ApplicationDefinition());

        Assert.False(result.Success);
        Assert.True(result.Messages.Count >= 2);
    }

    [Fact]
    public void ValidationAcceptsExistingExecutableAndWorkingDirectory()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);
        var executable = Environment.ProcessPath;

        Assert.False(string.IsNullOrWhiteSpace(executable));
        Assert.True(File.Exists(executable));

        var definition = new ApplicationDefinition
        {
            Name = "Test process",
            ExecutablePath = executable!,
            WorkingDirectory = Path.GetDirectoryName(executable!) ?? string.Empty
        };

        var result = service.Validate(definition);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Messages));
        Assert.Empty(result.Messages);
    }

    private static AppWatcherConfiguration CreateConfiguration(
        string name,
        Guid? id = null) =>
        new()
        {
            Applications =
            [
                new ApplicationDefinition
                {
                    Id = id ?? Guid.NewGuid(),
                    Name = name,
                    ExecutablePath = @"C:\Apps\Example.exe",
                    WorkingDirectory = @"C:\Apps",
                    Privilege = PrivilegeLevel.Administrator,
                    AttachExisting = true
                }
            ]
        };

    private static async Task<string> ReadBackupNameAsync(
        string directory,
        int generation)
    {
        var path = Path.Combine(
            directory,
            $"config.backup-{generation}.json");

        Assert.True(File.Exists(path), $"Expected backup file: {path}");

        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var configuration = JsonSerializer.Deserialize<AppWatcherConfiguration>(
            json,
            JsonDefaults.Options);

        Assert.NotNull(configuration);
        return Assert.Single(configuration.Applications).Name;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AppWatcher.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the actual assertion result.
            }
        }
    }
}
