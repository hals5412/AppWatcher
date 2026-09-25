using Microsoft.Data.Sqlite;

namespace AppWatcher.Core;

public interface IEventSink
{
    Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default);
}

public class EventStore : IEventSink
{
    private readonly SemaphoreSlim InitGate = new(1, 1);
    private volatile bool _initialized;
    private DateTimeOffset _nextInitializationUtc;
    private readonly TimeProvider _time;

    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "events.db");
    public EventStore(string? dataDirectory = null, TimeProvider? time = null)
    {
        DataDirectory = dataDirectory ?? AppPaths.DataDirectory;
        _time = time ?? TimeProvider.System;
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        DefaultTimeout = 1
    }.ToString();

    public virtual async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await InitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            if (_time.GetUtcNow() < _nextInitializationUtc)
                throw new IOException("Database initialization is cooling down after a failure.");
            _nextInitializationUtc = _time.GetUtcNow().AddMinutes(1);
            Directory.CreateDirectory(DataDirectory);

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=1000;
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
            _nextInitializationUtc = default;
        }
        finally
        {
            InitGate.Release();
        }
    }

    public virtual async Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var command = connection.CreateCommand();
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
        using var command = connection.CreateCommand();

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
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM events WHERE timestamp_utc < $threshold;";
        command.Parameters.AddWithValue("$threshold", thresholdUtc.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public virtual async Task MaintainAsync(GlobalSettings settings, DateTimeOffset now, CancellationToken token = default)
    {
        FileStream lease;
        try
        {
            lease = await FileLease.AcquireAsync(Path.Combine(DataDirectory, "events.maintenance.lock"), TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // AgentとElevatedが同時に起動すると、もう一方がすでに整理している。失敗ではないので何もしない。
            return;
        }
        await using var _ = lease;
        var stamp = Path.Combine(DataDirectory, "events.maintenance.timestamp");
        if (File.Exists(stamp) && DateTimeOffset.TryParse(await File.ReadAllTextAsync(stamp, token).ConfigureAwait(false), out var previous)
            && now - previous < TimeSpan.FromHours(1)) return;
        await PurgeOlderThanAsync(now.AddDays(-Math.Max(1, settings.EventRetentionDays)), token).ConfigureAwait(false);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(token).ConfigureAwait(false);
        async Task<long> Scalar(string sql)
        {
            using var query = connection.CreateCommand();
            query.CommandText = sql;
            return Convert.ToInt64(await query.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        var pageSize = await Scalar("PRAGMA page_size;").ConfigureAwait(false);
        var limit = Math.Max(10L, settings.EventDatabaseMaxMegabytes) * 1024 * 1024;
        var used = (await Scalar("PRAGMA page_count;").ConfigureAwait(false) - await Scalar("PRAGMA freelist_count;").ConfigureAwait(false)) * pageSize;
        var physical = new FileInfo(DatabasePath).Length + (File.Exists(DatabasePath + "-wal") ? new FileInfo(DatabasePath + "-wal").Length : 0);
        if (used > limit || physical > limit)
        {
            while (used > limit * 9 / 10)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM events WHERE id IN (SELECT id FROM events ORDER BY timestamp_utc, id LIMIT 1000);";
                if (await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0) break;
                used = (await Scalar("PRAGMA page_count;").ConfigureAwait(false) - await Scalar("PRAGMA freelist_count;").ConfigureAwait(false)) * pageSize;
            }
            using var compact = connection.CreateCommand();
            compact.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);";
            await compact.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(stamp, now.ToString("O"), token).ConfigureAwait(false);
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
            SecretMask.Json(json));
    }
}
