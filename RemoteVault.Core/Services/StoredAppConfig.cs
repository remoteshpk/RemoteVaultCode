namespace RemoteVault.Core.Services;

public sealed class StoredAppConfig
{
    public string AppName { get; init; } = "RemoteVault";

    public string Version { get; init; } = "ModernWinUI";

    public string? Created { get; init; }

    public string Provider { get; init; } = "remote";

    public string RemoteName { get; init; } = "RemoteVault";

    public string BucketName { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    public bool EnableDatabaseBackups { get; init; }

    public bool EnableDatabaseRestore { get; init; }

    public string DatabaseBackupRootPath { get; init; } = string.Empty;

    public string MysqldumpPath { get; init; } = string.Empty;

    public string PgDumpPath { get; init; } = string.Empty;

    public string SqlcmdPath { get; init; } = string.Empty;

    public StoredBackupSchedule FileBackupSchedule { get; init; } = new();

    public StoredBackupSchedule DatabaseBackupSchedule { get; init; } = new();

    public StoredBackupSchedule DatabaseFullBackupSchedule { get; init; } = new();

    public StoredBackupSchedule DatabaseDiffBackupSchedule { get; init; } = new();

    public List<StoredBackupFolder> BackupFolders { get; init; } = new();

    public List<string> ExcludePatterns { get; init; } = new();
}

public sealed class StoredBackupFolder
{
    public string LocalPath { get; init; } = string.Empty;

    public string RemoteSubfolder { get; init; } = string.Empty;
}

public sealed class StoredBackupSchedule
{
    public bool Enabled { get; init; }

    public string Frequency { get; init; } = "daily";

    public string DailyTime { get; init; } = "02:00";

    public int IntervalHours { get; init; } = 6;

    public string BackupType { get; init; } = "full";

    public string Weekday { get; init; } = "monday";

    public int DayOfMonth { get; init; } = 1;
}
