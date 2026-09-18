using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppWatcher.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SupervisorCommandType
{
    Ping,
    GetSnapshot,
    ReloadConfiguration,
    StartApplication,
    StopApplication,
    RestartApplication,
    PauseApplication,
    ResumeApplication,
    StartMaintenance,
    ResumeMaintenance,
    ShutdownHost
}

public sealed record SupervisorRequest(
    SupervisorCommandType Command,
    Guid? ApplicationId = null,
    int? DurationSeconds = null);

public sealed record SupervisorResponse(
    bool Success,
    string? Error = null,
    HostSnapshot? Snapshot = null);

public static class PipeNames
{
    public static string For(PrivilegeLevel privilege)
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch
        {
            identity = Environment.UserName;
        }

        var safe = new string(identity.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return privilege == PrivilegeLevel.Normal
            ? $"AppWatcher.Agent.{safe}"
            : $"AppWatcher.Elevated.{safe}";
    }
}

public sealed class SupervisorPipeServer : IAsyncDisposable
{
    private readonly SupervisorEngine _engine;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _runTask;

    public SupervisorPipeServer(SupervisorEngine engine)
    {
        _engine = engine;
        _pipeName = PipeNames.For(engine.HostPrivilege);
    }

    public void Start()
    {
        _runTask ??= RunAsync(_lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = CreateServerStream();
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // HandleClientAsync owns and disposes the connected stream.
                _ = HandleClientAsync(server, cancellationToken);
                server = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                break;
            }
            catch (Exception ex)
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }

                TryWritePipeFailure(ex);

                // Avoid a tight failure loop if Windows rejects pipe creation.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        // Elevated Helper runs at high integrity while the dashboard runs at medium
        // integrity. Explicitly grant the current Windows user access and label the
        // pipe as medium integrity so the same user can connect across the UAC boundary.
        var sid = WindowsIdentity.GetCurrent().User
                  ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");

        var security = new PipeSecurity();

        // Use an explicit DACL for the signed-in user, but do not add a mandatory
        // integrity SACL. Windows treats an unlabeled securable object as medium
        // integrity, which allows the medium-integrity dashboard to use the pipe.
        // Adding the SACL here can require privileges unavailable to the normal Agent
        // and can make both IPC servers fail before accepting a connection.
        security.SetSecurityDescriptorSddlForm(
            $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{sid.Value})");

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private void TryWritePipeFailure(Exception ex)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.AppendAllText(
                AppPaths.FallbackLog,
                $"{DateTimeOffset.UtcNow:O}`tError`tPipeServerFailure`tPipe={_pipeName}`t{ex}{Environment.NewLine}");
        }
        catch
        {
            // IPC diagnostics must never terminate supervision.
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        try
        {
            var request = await PipeProtocol.ReadAsync<SupervisorRequest>(server, cancellationToken).ConfigureAwait(false);
            var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);
            await PipeProtocol.WriteAsync(server, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Client disconnected or sent malformed data.
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SupervisorResponse> HandleAsync(SupervisorRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Command)
            {
                case SupervisorCommandType.Ping:
                    return new SupervisorResponse(true);
                case SupervisorCommandType.GetSnapshot:
                    return new SupervisorResponse(true, Snapshot: _engine.Snapshot());
                case SupervisorCommandType.ReloadConfiguration:
                    await _engine.ReloadAsync(cancellationToken).ConfigureAwait(false);
                    return new SupervisorResponse(true, Snapshot: _engine.Snapshot());
                case SupervisorCommandType.StartApplication:
                    await _engine.StartApplicationAsync(RequireApplicationId(request), cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.StopApplication:
                    await _engine.StopApplicationAsync(RequireApplicationId(request), cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.RestartApplication:
                    await _engine.RestartApplicationAsync(RequireApplicationId(request), cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.PauseApplication:
                    await _engine.PauseApplicationAsync(
                        RequireApplicationId(request),
                        request.DurationSeconds is null ? null : TimeSpan.FromSeconds(request.DurationSeconds.Value),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.ResumeApplication:
                    await _engine.ResumeApplicationAsync(RequireApplicationId(request), cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.StartMaintenance:
                    await _engine.SetMaintenanceAsync(
                        request.DurationSeconds is null ? null : TimeSpan.FromSeconds(request.DurationSeconds.Value),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.ResumeMaintenance:
                    await _engine.ResumeMaintenanceAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case SupervisorCommandType.ShutdownHost:
                    await _engine.RequestHostShutdownAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return new SupervisorResponse(true, Snapshot: _engine.Snapshot());
        }
        catch (Exception ex)
        {
            return new SupervisorResponse(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Guid RequireApplicationId(SupervisorRequest request) =>
        request.ApplicationId ?? throw new ArgumentException("ApplicationId is required for this command.");

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}

public sealed class SupervisorClient(PrivilegeLevel privilege)
{
    private readonly string _pipeName = PipeNames.For(privilege);

    public async Task<SupervisorResponse> SendAsync(
        SupervisorRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout.Value);

        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            await PipeProtocol.WriteAsync(pipe, request, linked.Token).ConfigureAwait(false);
            return await PipeProtocol.ReadAsync<SupervisorResponse>(pipe, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SupervisorResponse(false, "Host unavailable or timed out.");
        }
        catch (Exception ex)
        {
            return new SupervisorResponse(false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal static class PipeProtocol
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false };
        return options;
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, CompactOptions);
        if (payload.Length > MaxMessageBytes) throw new InvalidDataException("IPC message is too large.");

        var length = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException("Invalid IPC message length.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, CompactOptions)
               ?? throw new InvalidDataException("IPC message deserialized to null.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
