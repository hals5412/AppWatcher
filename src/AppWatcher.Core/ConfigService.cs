using System.Text.Json;

namespace AppWatcher.Core;

public sealed class ConfigService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<AppWatcherConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureDataDirectory();
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                var empty = new AppWatcherConfiguration();
                await WriteFileAsync(AppPaths.ConfigFile, empty, cancellationToken).ConfigureAwait(false);
                return empty;
            }

            try
            {
                return await ReadFileAsync(AppPaths.ConfigFile, cancellationToken).ConfigureAwait(false);
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
        AppPaths.EnsureDataDirectory();

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RotateBackups();
            var temp = AppPaths.ConfigFile + ".tmp";
            await WriteFileAsync(temp, configuration, cancellationToken).ConfigureAwait(false);
            File.Move(temp, AppPaths.ConfigFile, true);
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

    private static void RotateBackups()
    {
        if (File.Exists(BackupPath(3))) File.Delete(BackupPath(3));
        if (File.Exists(BackupPath(2))) File.Move(BackupPath(2), BackupPath(3), true);
        if (File.Exists(BackupPath(1))) File.Move(BackupPath(1), BackupPath(2), true);
        if (File.Exists(AppPaths.ConfigFile)) File.Copy(AppPaths.ConfigFile, BackupPath(1), true);
    }

    private static string BackupPath(int generation) => Path.Combine(AppPaths.DataDirectory, $"config.backup-{generation}.json");

    private static void AppendFallbackLog(string message)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            File.AppendAllText(AppPaths.FallbackLog, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Configuration recovery must not fail because diagnostic logging failed.
        }
    }
}
