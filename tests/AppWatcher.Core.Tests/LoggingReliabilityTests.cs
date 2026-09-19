using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AppWatcher.Core;
using Xunit;

namespace AppWatcher.Core.Tests;

public sealed class LoggingReliabilityTests
{
    [Fact]
    public async Task InitializationRetriesAreSharedWithMaintenance()
    {
        using var directory = new AuditDirectory(); var clock = new TestClock(); var store = new EventStore(directory.Path, clock);
        var invalid = Path.Combine(directory.Path, "events.db"); Directory.CreateDirectory(invalid);
        await Assert.ThrowsAnyAsync<Exception>(() => store.InitializeAsync(TestContext.Current.CancellationToken));
        Directory.Delete(invalid);
        await Assert.ThrowsAsync<IOException>(() => store.InitializeAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await store.QueryAsync(new EventQuery(), TestContext.Current.CancellationToken));
    }

    private sealed class BrokenStore(string directory) : EventStore(directory)
    {
        public int Attempts;
        public bool Available;
        public override Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Attempts);
            return Available ? Task.CompletedTask : Task.FromException(new IOException("Isolated injected failure"));
        }
        public override Task MaintainAsync(GlobalSettings settings, DateTimeOffset now, CancellationToken token = default) =>
            Task.FromException(new IOException("Isolated maintenance failure"));
    }
    private static EventRecord Record(string details = "{}") => new(DateTimeOffset.UtcNow, null, "Test", AppLogLevel.Information, "TestEvent", "Test", details);

    [Fact]
    public async Task DatabaseFailureIsThrottledAndRetriedAfterOneMinute()
    {
        using var directory = new AuditDirectory(); var clock = new TestClock(); var store = new BrokenStore(directory.Path);
        await using var sink = new ResilientEventSink(store, clock);
        await sink.WriteAsync(Record(), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => File.Exists(Path.Combine(directory.Path, "appwatcher-fallback.log")));
        await sink.WriteAsync(Record(), TestContext.Current.CancellationToken);
        store.Available = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await sink.WriteAsync(Record(), TestContext.Current.CancellationToken);
        await sink.DisposeAsync();
        Assert.InRange(store.Attempts, 2, 3);
    }

    [Fact]
    public async Task RepeatedDatabaseFailuresDoNotRetryEveryEvent()
    {
        using var directory = new AuditDirectory(); var store = new BrokenStore(directory.Path);
        await using var sink = new ResilientEventSink(store, new TestClock());
        for (var i = 0; i < 10; i++) await sink.WriteAsync(Record(), TestContext.Current.CancellationToken);
        await sink.DisposeAsync();
        Assert.Equal(1, store.Attempts);
        Assert.Equal(10, (await File.ReadAllLinesAsync(Path.Combine(directory.Path, "appwatcher-fallback.log"), TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task HostStartsDespiteBrokenDatabaseAndMaintenance()
    {
        using var directory = new AuditDirectory(); var config = new ConfigService(directory.Path);
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [TestWait.Definition()] }, TestContext.Current.CancellationToken);
        var clock = new TestClock(); var runtime = new FakeRuntime(clock); var store = new BrokenStore(directory.Path);
        await using var engine = new SupervisorEngine(PrivilegeLevel.Normal, config, store, runtime, clock);
        await engine.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.Launches);
        Assert.Equal(AppRuntimeState.Healthy, Assert.Single(engine.Snapshot().Applications).State);
        await engine.DisposeAsync();
        Assert.False(runtime.Processes[0].HasExited);
    }

    [Theory]
    [InlineData("--token value")]
    [InlineData("--password=\"value with spaces\"")]
    [InlineData("-passwd 'value with spaces'")]
    [InlineData("--api-key=value")]
    [InlineData("--api_key value")]
    [InlineData("--secret=value")]
    public void MasksSupportedArgumentsInNestedJson(string arguments)
    {
        var json = SecretMask.Json(JsonSerializer.Serialize(new { nested = new[] { new { arguments } } }));
        Assert.DoesNotContain("value", json);
        Assert.Contains("********", json);
    }

    [Fact]
    public async Task DiagnosticArchiveRemovesHistoricalSecretsFromEveryFile()
    {
        using var directory = new AuditDirectory(); using var output = new AuditDirectory();
        const string secret = "LegacyDummySecret918273";
        var config = new ConfigService(directory.Path); var definition = TestWait.Definition();
        definition.Arguments = "--token \"" + secret + "\"";
        await config.SaveAsync(new AppWatcherConfiguration { Applications = [definition] }, TestContext.Current.CancellationToken);
        var store = new EventStore(directory.Path);
        await store.WriteAsync(Record(JsonSerializer.Serialize(new { arguments = definition.Arguments })), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "appwatcher-fallback.log"), "Legacy --password=" + secret, TestContext.Current.CancellationToken);
        var zip = Path.Combine(output.Path, "diagnostics.zip");
        await new DiagnosticPackageBuilder(config).CreateAsync(zip, null, null, TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Contains(archive.Entries, e => e.FullName == "events.db");
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open(); using var data = new MemoryStream();
            await stream.CopyToAsync(data, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(data.ToArray()));
        }
        Assert.Contains(secret, Assert.Single(await store.QueryAsync(new EventQuery(), TestContext.Current.CancellationToken)).DetailsJson);
    }

    [Fact]
    public async Task BrokenDiagnosticDatabaseIsNotCopiedAsRawFile()
    {
        using var directory = new AuditDirectory(); using var output = new AuditDirectory();
        const string secret = "RawFileSecret918273";
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "events.db"), secret, TestContext.Current.CancellationToken);
        var zip = Path.Combine(output.Path, "diagnostics.zip");
        await new DiagnosticPackageBuilder(new ConfigService(directory.Path)).CreateAsync(zip, null, null, TestContext.Current.CancellationToken);
        using var archive = ZipFile.OpenRead(zip);
        Assert.DoesNotContain(archive.Entries, e => e.FullName == "events.db");
        Assert.Contains(archive.Entries, e => e.FullName == "collection-notes.txt");
    }

    [Fact]
    public async Task RetentionRunsHourlyAndCapacityCleanupShrinksDatabase()
    {
        using var directory = new AuditDirectory(); var store = new EventStore(directory.Path); var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(Record() with { TimestampUtc = now.AddDays(-100) }, TestContext.Current.CancellationToken);
        await store.WriteAsync(Record(), TestContext.Current.CancellationToken);
        var settings = new GlobalSettings { EventRetentionDays = 90, EventDatabaseMaxMegabytes = 10 };
        await store.MaintainAsync(settings, now, TestContext.Current.CancellationToken);
        Assert.Single(await store.QueryAsync(new EventQuery(), TestContext.Current.CancellationToken));
        var payload = new string('x', 512 * 1024);
        for (var i = 0; i < 24; i++) await store.WriteAsync(Record(payload), TestContext.Current.CancellationToken);
        await store.MaintainAsync(settings, now, TestContext.Current.CancellationToken);
        Assert.Equal(25, (await store.QueryAsync(new EventQuery(), TestContext.Current.CancellationToken)).Count);
        await store.MaintainAsync(settings, now.AddHours(1), TestContext.Current.CancellationToken);
        Assert.True(new FileInfo(store.DatabasePath).Length <= 10L * 1024 * 1024);
    }

    [Fact]
    public async Task FallbackLogKeepsOnlyTwoOldGenerations()
    {
        using var directory = new AuditDirectory(); var path = Path.Combine(directory.Path, "fallback.log");
        var log = new FallbackLog(path, 200);
        for (var i = 0; i < 10; i++) await log.WriteAsync(new string('x', 110), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path + ".1")); Assert.True(File.Exists(path + ".2")); Assert.False(File.Exists(path + ".3"));
        foreach (var file in Directory.GetFiles(directory.Path, "fallback.log*")) Assert.True(new FileInfo(file).Length <= 200);
    }
}
