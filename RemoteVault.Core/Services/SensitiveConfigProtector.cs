using System.Security.Cryptography;
using System.Text;

namespace RemoteVault.Core.Services;

/// <summary>
/// Keeps the storage credentials (access key and secret key) DPAPI-encrypted on disk.
/// Everything else in the config file is plain JSON and may be edited by hand; a credential
/// entered as plain text is accepted on load and encrypted the next time the file is written.
/// </summary>
internal static class SensitiveConfigProtector
{
    private const string Prefix = "rv-dpapi:";
    private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("RemoteVault.Desktop.Config.v1");

    public static bool NeedsMigration(StoredAppConfig? config)
    {
        return config is not null &&
               (IsPlainText(config.AccessKey) || IsPlainText(config.SecretKey));
    }

    public static StoredAppConfig ProtectForStorage(StoredAppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return CloneWithCredentials(config, Protect(config.AccessKey), Protect(config.SecretKey));
    }

    public static StoredAppConfig UnprotectAfterLoad(StoredAppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return CloneWithCredentials(config, Unprotect(config.AccessKey), Unprotect(config.SecretKey));
    }

    private static bool IsPlainText(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && !value.StartsWith(Prefix, StringComparison.Ordinal);
    }

    private static string Protect(string? value)
    {
        var normalized = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        var plaintext = Encoding.UTF8.GetBytes(normalized);
        var protectedBytes = ProtectedData.Protect(plaintext, AdditionalEntropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string? value)
    {
        var normalized = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(normalized[Prefix.Length..]);
            var plaintext = ProtectedData.Unprotect(protectedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Encrypted by another Windows user or machine, or damaged. Treat the credential
            // as missing so the rest of the config still loads and the user can re-enter it.
            return string.Empty;
        }
    }

    private static StoredAppConfig CloneWithCredentials(StoredAppConfig config, string accessKey, string secretKey)
    {
        return new StoredAppConfig
        {
            AppName = config.AppName,
            Version = config.Version,
            Created = config.Created,
            Provider = config.Provider,
            RemoteName = config.RemoteName,
            BucketName = config.BucketName,
            Endpoint = config.Endpoint,
            AccessKey = accessKey,
            SecretKey = secretKey,
            EnableDatabaseBackups = config.EnableDatabaseBackups,
            EnableDatabaseRestore = config.EnableDatabaseRestore,
            DatabaseBackupRootPath = config.DatabaseBackupRootPath,
            MysqldumpPath = config.MysqldumpPath,
            PgDumpPath = config.PgDumpPath,
            SqlcmdPath = config.SqlcmdPath,
            FileBackupSchedule = CloneSchedule(config.FileBackupSchedule),
            DatabaseBackupSchedule = CloneSchedule(config.DatabaseBackupSchedule),
            DatabaseFullBackupSchedule = CloneSchedule(config.DatabaseFullBackupSchedule),
            DatabaseDiffBackupSchedule = CloneSchedule(config.DatabaseDiffBackupSchedule),
            BackupFolders = (config.BackupFolders ?? new List<StoredBackupFolder>()).Select(folder => new StoredBackupFolder
            {
                LocalPath = folder.LocalPath,
                RemoteSubfolder = folder.RemoteSubfolder
            }).ToList(),
            ExcludePatterns = (config.ExcludePatterns ?? new List<string>()).ToList()
        };
    }

    private static StoredBackupSchedule CloneSchedule(StoredBackupSchedule? schedule)
    {
        return new StoredBackupSchedule
        {
            Enabled = schedule?.Enabled == true,
            Frequency = schedule?.Frequency ?? "daily",
            DailyTime = schedule?.DailyTime ?? "02:00",
            IntervalHours = schedule?.IntervalHours is > 0 ? schedule.IntervalHours : 6,
            BackupType = string.Equals(schedule?.BackupType, "diff", StringComparison.OrdinalIgnoreCase) ? "diff" : "full",
            Weekday = string.IsNullOrWhiteSpace(schedule?.Weekday) ? "monday" : schedule.Weekday,
            DayOfMonth = schedule?.DayOfMonth is >= 1 and <= 31 ? schedule.DayOfMonth : 1
        };
    }
}
