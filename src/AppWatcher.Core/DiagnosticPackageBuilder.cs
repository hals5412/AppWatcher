using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AppWatcher.Core;

public sealed class DiagnosticPackageBuilder(ConfigService configService)
{
    private static readonly Regex SecretArgumentPattern = new(
        """(?ix)(--?(?:password|passwd|token|api[_-]?key|secret)\s*(?:=|\s)\s*)([^\s"']+|"[^"]*"|'[^']*')""",
        RegexOptions.Compiled);

    public async Task CreateAsync(
        string destinationZip,
        HostSnapshot? normalHost,
        HostSnapshot? elevatedHost,
        CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "AppWatcher-Diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var config = await configService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var sanitized = Sanitize(config);
            await File.WriteAllTextAsync(
                Path.Combine(temp, "config-sanitized.json"),
                JsonSerializer.Serialize(sanitized, JsonDefaults.Options),
                cancellationToken).ConfigureAwait(false);

            var status = new { createdUtc = DateTimeOffset.UtcNow, normalHost, elevatedHost };
            await File.WriteAllTextAsync(
                Path.Combine(temp, "status-snapshot.json"),
                JsonSerializer.Serialize(status, JsonDefaults.Options),
                cancellationToken).ConfigureAwait(false);

            var systemInfo = $"""
                AppWatcher diagnostics
                Created UTC: {DateTimeOffset.UtcNow:O}
                OS: {Environment.OSVersion}
                OS 64-bit: {Environment.Is64BitOperatingSystem}
                Process 64-bit: {Environment.Is64BitProcess}
                .NET: {Environment.Version}
                Processor count: {Environment.ProcessorCount}
                Machine name: [redacted]
                User name: [redacted]
                """;
            await File.WriteAllTextAsync(Path.Combine(temp, "system-info.txt"), systemInfo, cancellationToken).ConfigureAwait(false);

            BackupEventDatabaseIfPossible(Path.Combine(temp, "events.db"));
            CopyIfPossible(AppPaths.FallbackLog, Path.Combine(temp, "appwatcher-fallback.log"));

            if (File.Exists(destinationZip)) File.Delete(destinationZip);
            ZipFile.CreateFromDirectory(temp, destinationZip, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static AppWatcherConfiguration Sanitize(AppWatcherConfiguration config)
    {
        var json = JsonSerializer.Serialize(config, JsonDefaults.Options);
        var clone = JsonSerializer.Deserialize<AppWatcherConfiguration>(json, JsonDefaults.Options) ?? new AppWatcherConfiguration();
        foreach (var app in clone.Applications)
        {
            app.Arguments = SecretArgumentPattern.Replace(app.Arguments ?? string.Empty, "$1********");
        }
        return clone;
    }

    private static void BackupEventDatabaseIfPossible(string destination)
    {
        try
        {
            if (!File.Exists(AppPaths.EventDatabase)) return;
            using var source = new SqliteConnection($"Data Source={AppPaths.EventDatabase};Mode=ReadOnly");
            using var target = new SqliteConnection($"Data Source={destination};Mode=ReadWriteCreate");
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }
        catch
        {
            CopyIfPossible(AppPaths.EventDatabase, destination);
        }
    }

    private static void CopyIfPossible(string source, string destination)
    {
        try
        {
            if (File.Exists(source)) File.Copy(source, destination, true);
        }
        catch
        {
            // A diagnostic package should still be created if a live file cannot be copied.
        }
    }
}
