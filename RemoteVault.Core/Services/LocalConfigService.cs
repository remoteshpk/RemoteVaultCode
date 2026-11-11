using System.Text.Json;

namespace RemoteVault.Core.Services;

public interface IAppConfigStore
{
    string ConfigPath { get; }

    bool HasUsableConfigurationFile();

    Task<StoredAppConfig?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(StoredAppConfig config, CancellationToken cancellationToken = default);
}

public sealed class LocalConfigService : IAppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ConfigPath => BackupStorageLayout.ConfigPath;

    public bool HasUsableConfigurationFile()
    {
        try
        {
            var config = LoadFromPath(ConfigPath, migrateIfNeeded: true);
            return IsUsableConfiguration(config);
        }
        catch
        {
            return false;
        }
    }

    public async Task<StoredAppConfig?> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await LoadFromPathAsync(ConfigPath, cancellationToken);
    }

    public async Task SaveAsync(StoredAppConfig config, CancellationToken cancellationToken = default)
    {
        var tempPath = BuildUniqueTempPath(ConfigPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var configForStorage = SensitiveConfigProtector.ProtectForStorage(config);

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, configForStorage, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public static async Task<StoredAppConfig?> LoadFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        return await LoadFromPathCoreAsync(path, migrateIfNeeded: true, cancellationToken);
    }

    private static StoredAppConfig? LoadFromPath(string path, bool migrateIfNeeded)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var serializedConfig = JsonSerializer.Deserialize<StoredAppConfig>(json, JsonOptions);
            if (serializedConfig is null)
            {
                return null;
            }

            if (IsUninitializedConfig(serializedConfig))
            {
                return null;
            }

            var migrationNeeded = SensitiveConfigProtector.NeedsMigration(serializedConfig);
            var usableConfig = SensitiveConfigProtector.UnprotectAfterLoad(serializedConfig);

            if (migrateIfNeeded && migrationNeeded)
            {
                SavePath(path, usableConfig);
            }

            return usableConfig;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<StoredAppConfig?> LoadFromPathCoreAsync(string path, bool migrateIfNeeded, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var serializedConfig = JsonSerializer.Deserialize<StoredAppConfig>(json, JsonOptions);
            if (serializedConfig is null)
            {
                return null;
            }

            if (IsUninitializedConfig(serializedConfig))
            {
                return null;
            }

            var migrationNeeded = SensitiveConfigProtector.NeedsMigration(serializedConfig);
            var usableConfig = SensitiveConfigProtector.UnprotectAfterLoad(serializedConfig);

            if (migrateIfNeeded && migrationNeeded)
            {
                await SavePathAsync(path, usableConfig, cancellationToken);
            }

            return usableConfig;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SavePath(string path, StoredAppConfig config)
    {
        var tempPath = BuildUniqueTempPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var configForStorage = SensitiveConfigProtector.ProtectForStorage(config);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(configForStorage, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static async Task SavePathAsync(string path, StoredAppConfig config, CancellationToken cancellationToken)
    {
        var tempPath = BuildUniqueTempPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var configForStorage = SensitiveConfigProtector.ProtectForStorage(config);

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, configForStorage, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static bool IsUsableConfiguration(StoredAppConfig? config)
    {
        return config is not null &&
               !string.IsNullOrWhiteSpace(config.Provider) &&
               !string.IsNullOrWhiteSpace(config.Endpoint) &&
               !string.IsNullOrWhiteSpace(config.BucketName) &&
               !string.IsNullOrWhiteSpace(config.AccessKey) &&
               !string.IsNullOrWhiteSpace(config.SecretKey) &&
               config.BackupFolders is { Count: > 0 } &&
               config.BackupFolders.Any(folder =>
                   !string.IsNullOrWhiteSpace(folder.LocalPath) &&
                   !string.IsNullOrWhiteSpace(folder.RemoteSubfolder));
    }

    private static bool IsUninitializedConfig(StoredAppConfig config)
    {
        return string.IsNullOrWhiteSpace(config.AccessKey) &&
               string.IsNullOrWhiteSpace(config.SecretKey) &&
               string.IsNullOrWhiteSpace(config.BucketName) &&
               string.IsNullOrWhiteSpace(config.Endpoint) &&
               string.IsNullOrWhiteSpace(config.DatabaseBackupRootPath) &&
               config.BackupFolders.Count == 0 &&
               config.ExcludePatterns.Count == 0;
    }

    private static string BuildUniqueTempPath(string path)
    {
        return $"{path}.{Guid.NewGuid():N}.tmp";
    }
}
