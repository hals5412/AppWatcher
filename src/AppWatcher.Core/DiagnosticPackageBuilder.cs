using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AppWatcher.Core;

public sealed class DiagnosticPackageBuilder(ConfigService configService)
{
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
                SecretMask.Json(JsonSerializer.Serialize(status, JsonDefaults.Options)),
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

            await ExportLogsAsync(temp, cancellationToken).ConfigureAwait(false);

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
            app.Arguments = SecretMask.Text(app.Arguments ?? string.Empty);
        }
        return clone;
    }

    private async Task ExportLogsAsync(string directory, CancellationToken token)
    {
        var notes = new List<string>();
        var database = Path.Combine(configService.DataDirectory, "events.db");
        try
        {
            if (File.Exists(database))
            {
                await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
                await source.OpenAsync(token).ConfigureAwait(false);
                await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory, "events.db"), Pooling = false }.ToString());
                await destination.OpenAsync(token).ConfigureAwait(false);
                using (var schema = destination.CreateCommand())
                {
                    schema.CommandText = "CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_utc TEXT NOT NULL, application_id TEXT, application_name TEXT, level INTEGER NOT NULL, event_type TEXT NOT NULL, reason_code TEXT NOT NULL, details_json TEXT NOT NULL);";
                    await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                // 単一SELECTの読み取りスナップショットを、新規DBへマスクして書き出す。
                using var read = source.CreateCommand();
                read.CommandText = "SELECT timestamp_utc, application_id, application_name, level, event_type, reason_code, details_json FROM events ORDER BY id;";
                await using var rows = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                using var transaction = destination.BeginTransaction();
                while (await rows.ReadAsync(token).ConfigureAwait(false))
                {
                    using var insert = destination.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO events(timestamp_utc,application_id,application_name,level,event_type,reason_code,details_json) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6);";
                    for (var i = 0; i < 7; i++)
                    {
                        object value = rows.GetValue(i);
                        if (value is string text) value = i == 6 ? SecretMask.Json(text) : SecretMask.Text(text);
                        insert.Parameters.AddWithValue("$p" + i, value);
                    }
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                transaction.Commit();
            }
            else notes.Add("events.db: unavailable (not found)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"events.db: unavailable ({ex.GetType().Name}); raw database was not copied.");
            var partial = Path.Combine(directory, "events.db");
            if (File.Exists(partial)) File.Delete(partial);
        }
        for (var generation = 0; generation < 3; generation++)
        {
            var name = "appwatcher-fallback.log" + (generation == 0 ? "" : "." + generation);
            var path = Path.Combine(configService.DataDirectory, name);
            try
            {
                if (!File.Exists(path)) continue;
                await using var lease = await FileLease.AcquireAsync(Path.Combine(configService.DataDirectory, "appwatcher-fallback.log.lock"), TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                var content = SecretMask.Text(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
                await File.WriteAllTextAsync(Path.Combine(directory, name), content, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { notes.Add($"{name}: unavailable ({ex.GetType().Name})"); }
        }
        await File.WriteAllLinesAsync(Path.Combine(directory, "collection-notes.txt"), notes, token).ConfigureAwait(false);
    }
}