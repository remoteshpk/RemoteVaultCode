namespace RemoteVault.Core.Services;

public static class BackupStorageLayout
{
    public static string AppDataRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteVault");

    public static string RootDirectory => Path.Combine(AppDataRootDirectory, "data", "backup-state");

    public static string ConfigPath => Path.Combine(AppDataRootDirectory, "remotevault.config.json");

    public static string DbProfilesPath => Path.Combine(AppDataRootDirectory, "data", "db_profiles.json");

    public static string ManifestRoot => Path.Combine(RootDirectory, "manifests");

    public static string HistoryPath => Path.Combine(RootDirectory, "RemoteVaultBackupHistory.json");

    public static string StatusPath => Path.Combine(RootDirectory, "RemoteVaultBackupStatus.json");

    public static string OperationHistoryPath => Path.Combine(RootDirectory, "RemoteVaultOperationHistory.json");

    public static string GetManifestPath(string bucketName, string remoteSubfolder)
    {
        var bucket = SanitizePathSegment(bucketName, "bucket");
        var subfolder = string.IsNullOrWhiteSpace(remoteSubfolder)
            ? "ROOT"
            : SanitizePathSegment(remoteSubfolder.Replace("/", "_").Replace("\\", "_"), "ROOT");
        return Path.Combine(ManifestRoot, $"{bucket}_{subfolder}", "manifest.db");
    }

    private static string SanitizePathSegment(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string((value ?? string.Empty)
            .Trim()
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
