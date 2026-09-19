using System.Text.Json;

namespace AppWatcher.Core;

public sealed class ConfigService
{
    public string DataDirectory => _dataDirectory;
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
        await using var lease = await FileLease.AcquireAsync(Path.Combine(_dataDirectory, "config.lock"), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ファイルハンドルの破棄でプロセス間ロックを解放する。
        }
    }

    public async Task SaveAsync(AppWatcherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureDataDirectory();

        await using var lease = await FileLease.AcquireAsync(Path.Combine(_dataDirectory, "config.lock"), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        try
        {
            ConfigurationMigrator.PrepareForSave(configuration);
            await RotateReadableBackupsAsync(cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(
                configuration,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ファイルハンドルの破棄でプロセス間ロックを解放する。
        }
    }

    private async Task<AppWatcherConfiguration> LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigFile))
        {
            var empty = new AppWatcherConfiguration();
            ConfigurationMigrator.PrepareForSave(empty);
            await WriteAtomicallyAsync(empty, cancellationToken).ConfigureAwait(false);
            return empty;
        }

        ConfigurationReadResult primary;
        try
        {
            primary = await ConfigurationMigrator.ReadAsync(
                ConfigFile,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnsupportedConfigurationSchemaException)
        {
            // Never silently fall back to an older backup when the primary file
            // belongs to a newer AppWatcher version. Doing so could discard settings.
            throw;
        }
        catch (Exception primaryEx) when (primaryEx is JsonException or InvalidDataException)
        {
            return await RecoverFromBackupAsync(
                primaryEx,
                cancellationToken).ConfigureAwait(false);
        }

        if (primary.WasMigrated)
        {
            await PersistMigratedConfigurationAsync(
                primary,
                cancellationToken).ConfigureAwait(false);
        }

        return primary.Configuration;
    }

    public async Task<AppWatcherConfiguration> UpdateAsync(Action<AppWatcherConfiguration> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        EnsureDataDirectory();
        await using var lease = await FileLease.AcquireAsync(Path.Combine(_dataDirectory, "config.lock"), TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        var current = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
        update(current);
        ConfigurationMigrator.PrepareForSave(current);
        await RotateReadableBackupsAsync(cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(current, cancellationToken).ConfigureAwait(false);
        return current;
    }

    private async Task RotateReadableBackupsAsync(CancellationToken token)
    {
        if (!File.Exists(ConfigFile)) return;
        try { await ConfigurationMigrator.ReadAsync(ConfigFile, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return; }
        RotateBackups();
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

    private async Task<AppWatcherConfiguration> RecoverFromBackupAsync(
        Exception primaryException,
        CancellationToken cancellationToken)
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
                var recovered = await ConfigurationMigrator.ReadAsync(
                    backup,
                    cancellationToken).ConfigureAwait(false);

                AppendFallbackLog(
                    $"ConfigurationRecovery: recovered config from {backup}. Primary error: {primaryException.Message}");

                if (recovered.WasMigrated)
                {
                    AppendFallbackLog(
                        $"ConfigurationMigration: backup schema v{recovered.SourceSchemaVersion} was migrated in memory to v{ConfigurationSchema.CurrentVersion}.");
                }

                return recovered.Configuration;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or UnsupportedConfigurationSchemaException)
            {
                // Try the next backup.
            }
        }

        throw primaryException;
    }

    private async Task PersistMigratedConfigurationAsync(
        ConfigurationReadResult migrated,
        CancellationToken cancellationToken)
    {
        // Rotate first so backup-1 is the exact pre-migration configuration.
        RotateBackups();
        await WriteAtomicallyAsync(
            migrated.Configuration,
            cancellationToken).ConfigureAwait(false);

        AppendFallbackLog(
            $"ConfigurationMigration: upgraded config schema v{migrated.SourceSchemaVersion} to v{ConfigurationSchema.CurrentVersion}. Original preserved as {BackupPath(1)}.");
    }

    private async Task WriteAtomicallyAsync(
        AppWatcherConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var temp = ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteFileAsync(
                temp,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(ConfigFile)) File.Replace(temp, ConfigFile, null); else File.Move(temp, ConfigFile);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // A stale temp file must not mask the original configuration error.
            }
        }
    }

    private void EnsureDataDirectory() => Directory.CreateDirectory(_dataDirectory);

    private static async Task WriteFileAsync(
        string path,
        AppWatcherConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);

        await JsonSerializer.SerializeAsync(
            stream,
            configuration,
            JsonDefaults.Options,
            cancellationToken).ConfigureAwait(false);
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
            new AppWatcher.Core.FallbackLog(FallbackLog).WriteAsync($"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}").GetAwaiter().GetResult();
        }
        catch
        {
            // Configuration recovery/migration must not fail because logging failed.
        }
    }
}
