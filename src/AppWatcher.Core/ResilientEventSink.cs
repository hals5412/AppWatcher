using System.Threading.Channels;

namespace AppWatcher.Core;

public sealed class ResilientEventSink : IEventSink, IAsyncDisposable
{
    private readonly EventStore _store;
    private readonly TimeProvider _time;
    private readonly FallbackLog _fallback;
    private readonly Channel<EventRecord> _queue = Channel.CreateBounded<EventRecord>(new BoundedChannelOptions(1024)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private DateTimeOffset _retryAfter;
    private long _dropped;

    public ResilientEventSink(EventStore store, TimeProvider? time = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        _fallback = new FallbackLog(Path.Combine(store.DataDirectory, "appwatcher-fallback.log"));
        _worker = Task.Run(DrainAsync);
    }

    public Task WriteAsync(EventRecord record, CancellationToken cancellationToken = default)
    {
        if (!_queue.Writer.TryWrite(SecretMask.Record(record))) Interlocked.Increment(ref _dropped);
        return Task.CompletedTask;
    }

    private async Task DrainAsync()
    {
        await foreach (var record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0) await _fallback.WriteAsync($"EventQueueOverflow: {dropped} records omitted.").ConfigureAwait(false);
            if (_time.GetUtcNow() >= _retryAfter)
            {
                try { await _store.WriteAsync(record).ConfigureAwait(false); continue; }
                catch (Exception) { _retryAfter = _time.GetUtcNow().AddMinutes(1); }
            }
            await _fallback.WriteAsync($"{record.TimestampUtc:O}\t{record.Level}\t{record.ApplicationName}\t{record.EventType}\t{record.ReasonCode}\t{record.DetailsJson}").ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }
}
