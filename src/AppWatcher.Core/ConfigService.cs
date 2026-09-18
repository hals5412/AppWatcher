using System.Text.Json;

namespace AppWatcher.Core;

public sealed class ConfigService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _dataDirectory;

    public ConfigService(string? dataDirectory = null)
    {
        _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? AppPaths.DataDirectory
            : Path.GetFullPath(dataDirectory);
    }

    private string ConfigFile => Path.Combine(_dataDirectory, "config.json");
    private string FallbackLog => Path.Combine(_dataDirectory, "appwatcher-fallback.log");

    public async Task<AppWatcherConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureDataDirectory();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(ConfigFile))
            {
                var empty = new AppWatcherConfiguration();
                await WriteFileAsync(ConfigFile, empty, cancellationToken).ConfigureAwait(false);
                return empty;
            }

            try
            {
                return await ReadFileAsync(ConfigFile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception primaryEx)
            {
                for (var i = 1; i <= 3; i++)
                {
                    var backup = BackupPath(i);
                    if (!File.Exists(backup))
                    {
                        continue;
                    }

                    try
                    {
                        var recovered = await ReadFileAsync(backup, cancellationToken).ConfigureAwait(false);
                        AppendFallbackLog($"ConfigurationRecovery: recovered config from {backup}. Primary error: {primaryEx.Message}");
                        return recovered;
                    }
                    catch
                    {
                        // Try the next backup.
                    }
                }

                throw;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(AppWatcherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureDataDirectory();

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RotateBackups();
            var temp = ConfigFile + ".tmp";
            await WriteFileAsync(temp, configuration, cancellationToken).ConfigureAwait(false);
            File.Move(temp, ConfigFile, true);
        }
        finally
        {
            Gate.Release();
        }
    }

    public ConfigurationValidationResult Validate(ApplicationDefinition definition)
    {
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            messages.Add(Localization.T("ValidationApplicationNameRequired"));
        }

        if (string.IsNullOrWhiteSpace(definition.ExecutablePath))
        {
            messages.Add(Localization.T("ValidationExecutablePathRequired"));
        }
        else if (!File.Exists(definition.ExecutablePath))
        {
            messages.Add(Localization.F("ValidationExecutableMissing", definition.ExecutablePath));
        }

        if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory) && !Directory.Exists(definition.WorkingDirectory))
        {
            messages.Add(Localization.F("ValidationWorkingDirectoryMissing", definition.WorkingDirectory));
        }

        if (definition.RestartDelaySeconds < 0)
        {
            messages.Add(Localization.T("ValidationRestartDelayNegative"));
        }

        if (definition.DetectHangs)
        {
            if (definition.HangCheckIntervalSeconds < 1)
            {
                messages.Add(Localization.T("ValidationHangInterval"));
            }
            if (definition.HangTimeoutSeconds < definition.HangCheckIntervalSeconds)
            {
                messages.Add(Localization.T("ValidationHangTimeout"));
            }
        }

        if (definition.RestartLoopProtectionEnabled)
        {
            if (definition.MaxRestarts < 1)
            {
                messages.Add(Localization.T("ValidationMaxRestarts"));
            }
            if (definition.RestartWindowMinutes < 1 || definition.BackoffMinutes < 1)
            {
                messages.Add(Localization.T("ValidationRestartWindowBackoff"));
            }
        }

        return new ConfigurationValidationResult(messages.Count == 0, messages);
    }

    private void EnsureDataDirectory() => Directory.CreateDirectory(_dataDirectory);

    private static async Task<AppWatcherConfiguration> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<AppWatcherConfiguration>(stream, JsonDefaults.Options, cancellationToken)
            .ConfigureAwait(false);
        return config ?? throw new InvalidDataException("Configuration file is empty.");
    }

    private static async Task WriteFileAsync(string path, AppWatcherConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, configuration, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void RotateBackups()
    {
        if (File.Exists(BackupPath(3))) File.Delete(BackupPath(3));
        if (File.Exists(BackupPath(2))) File.Move(BackupPath(2), BackupPath(3), true);
        if (File.Exists(BackupPath(1))) File.Move(BackupPath(1), BackupPath(2), true);
        if (File.Exists(ConfigFile)) File.Copy(ConfigFile, BackupPath(1), true);
    }

    private string BackupPath(int generation) =>
        Path.Combine(_dataDirectory, $"config.backup-{generation}.json");

    private void AppendFallbackLog(string message)
    {
        try
        {
            EnsureDataDirectory();
            File.AppendAllText(FallbackLog, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Configuration recovery must not fail because diagnostic logging failed.
        }
    }
}
