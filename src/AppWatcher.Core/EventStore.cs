using Microsoft.Data.Sqlite;

namespace AppWatcher.Core;

public interface IEventSink
{
    Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default);
}

public sealed class EventStore : IEventSink
{
    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private volatile bool _initialized;

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = AppPaths.EventDatabase,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await InitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            AppPaths.EnsureDataDirectory();

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp_utc TEXT NOT NULL,
                    application_id TEXT NULL,
                    application_name TEXT NULL,
                    level INTEGER NOT NULL,
                    event_type TEXT NOT NULL,
                    reason_code TEXT NOT NULL,
                    details_json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_events_timestamp ON events(timestamp_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_events_app_timestamp ON events(application_id, timestamp_utc DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            InitGate.Release();
        }
    }

    public async Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events(timestamp_utc, application_id, application_name, level, event_type, reason_code, details_json)
            VALUES($timestamp, $appId, $appName, $level, $eventType, $reason, $details);
            """;
        command.Parameters.AddWithValue("$timestamp", record.TimestampUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$appId", record.ApplicationId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$appName", record.ApplicationName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$level", (int)record.Level);
        command.Parameters.AddWithValue("$eventType", record.EventType);
        command.Parameters.AddWithValue("$reason", record.ReasonCode);
        command.Parameters.AddWithValue("$details", record.DetailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EventRecord>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var clauses = new List<string>();
        var command = connection.CreateCommand();

        if (query.ApplicationId is not null)
        {
            clauses.Add("application_id = $appId");
            command.Parameters.AddWithValue("$appId", query.ApplicationId.Value.ToString("D"));
        }
        if (query.MinimumLevel is not null)
        {
            clauses.Add("level >= $level");
            command.Parameters.AddWithValue("$level", (int)query.MinimumLevel.Value);
        }
        if (query.SinceUtc is not null)
        {
            clauses.Add("timestamp_utc >= $since");
            command.Parameters.AddWithValue("$since", query.SinceUtc.Value.UtcDateTime.ToString("O"));
        }

        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        var limit = Math.Clamp(query.Limit, 1, 5000);
        command.CommandText = $"""
            SELECT timestamp_utc, application_id, application_name, level, event_type, reason_code, details_json
            FROM events
            {where}
            ORDER BY timestamp_utc DESC
            LIMIT {limit};
            """;

        var results = new List<EventRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var timestamp = DateTimeOffset.Parse(reader.GetString(0));
            Guid? appId = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            var appName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var level = (AppLogLevel)reader.GetInt32(3);
            results.Add(new EventRecord(
                timestamp,
                appId,
                appName,
                level,
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }

        return results;
    }

    public async Task PurgeOlderThanAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM events WHERE timestamp_utc < $threshold;";
        command.Parameters.AddWithValue("$threshold", thresholdUtc.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ResilientEventSink(EventStore store) : IEventSink
{
    public async Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                AppPaths.EnsureDataDirectory();
                var line = $"{DateTimeOffset.UtcNow:O}\t{record.Level}\t{record.ApplicationName}\t{record.EventType}\t{record.ReasonCode}\t{ex.GetType().Name}: {ex.Message}{Environment.NewLine}";
                await File.AppendAllTextAsync(AppPaths.FallbackLog, line, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Monitoring must continue even if both primary and fallback logging fail.
            }
        }
    }
}

public static class EventRecordFactory
{
    public static EventRecord Create(
        ApplicationDefinition? app,
        AppLogLevel level,
        string eventType,
        string reasonCode,
        object? details = null)
    {
        var json = details is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(details, JsonDefaults.Options);
        return new EventRecord(
            DateTimeOffset.UtcNow,
            app?.Id,
            app?.Name,
            level,
            eventType,
            reasonCode,
            json);
    }
}
