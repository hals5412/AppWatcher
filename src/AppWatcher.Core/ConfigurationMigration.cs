using System.Text.Json;

namespace AppWatcher.Core;

public static class ConfigurationSchema
{
    // Schema 1 is the format used by AppWatcher alpha.1 through alpha.14.
    // Schema 2 introduces an explicit migration pipeline and canonicalizes
    // collections, strings and application IDs after deserialization.
    public const int CurrentVersion = 2;
    public const int OldestSupportedVersion = 1;
}

public sealed class UnsupportedConfigurationSchemaException(
    int schemaVersion,
    int supportedVersion)
    : Exception(
        $"Configuration schema version {schemaVersion} is newer than this AppWatcher build supports ({supportedVersion}).")
{
    public int SchemaVersion { get; } = schemaVersion;
    public int SupportedVersion { get; } = supportedVersion;
}

internal sealed record ConfigurationReadResult(
    AppWatcherConfiguration Configuration,
    int SourceSchemaVersion,
    bool WasMigrated);

internal static class ConfigurationMigrator
{
    public static async Task<ConfigurationReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Configuration root must be a JSON object.");
        }

        var sourceVersion = ReadSchemaVersion(document.RootElement);
        if (sourceVersion > ConfigurationSchema.CurrentVersion)
        {
            throw new UnsupportedConfigurationSchemaException(
                sourceVersion,
                ConfigurationSchema.CurrentVersion);
        }

        if (sourceVersion < ConfigurationSchema.OldestSupportedVersion)
        {
            throw new InvalidDataException(
                $"Configuration schema version {sourceVersion} is too old to migrate.");
        }

        var configuration = document.RootElement.Deserialize<AppWatcherConfiguration>(
            JsonDefaults.Options)
            ?? throw new InvalidDataException("Configuration file is empty.");

        var version = sourceVersion;
        while (version < ConfigurationSchema.CurrentVersion)
        {
            version = version switch
            {
                1 => MigrateV1ToV2(configuration),
                _ => throw new InvalidDataException(
                    $"No configuration migration exists from schema version {version}.")
            };
        }

        NormalizeCurrent(configuration);
        configuration.SchemaVersion = ConfigurationSchema.CurrentVersion;

        return new ConfigurationReadResult(
            configuration,
            sourceVersion,
            sourceVersion != ConfigurationSchema.CurrentVersion);
    }

    public static void PrepareForSave(AppWatcherConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        NormalizeCurrent(configuration);
        configuration.SchemaVersion = ConfigurationSchema.CurrentVersion;
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(
                    "schemaVersion",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var version))
            {
                throw new InvalidDataException(
                    "Configuration schemaVersion must be an integer.");
            }

            return version;
        }

        // Very early/pre-versioned files are interpreted as schema 1 because
        // schema 1 is the first persisted AppWatcher configuration format.
        return 1;
    }

    private static int MigrateV1ToV2(AppWatcherConfiguration configuration)
    {
        NormalizeCurrent(configuration);
        configuration.SchemaVersion = 2;
        return 2;
    }

    private static void NormalizeCurrent(AppWatcherConfiguration configuration)
    {
        configuration.Applications ??= [];
        configuration.Global ??= new GlobalSettings();
        configuration.Global.DashboardColumns ??= [];

        foreach (var application in configuration.Applications)
        {
            if (application.Id == Guid.Empty)
            {
                application.Id = Guid.NewGuid();
            }

            application.Name ??= string.Empty;
            application.ExecutablePath ??= string.Empty;
            application.Arguments ??= string.Empty;
            application.WorkingDirectory ??= string.Empty;
        }
    }
}
