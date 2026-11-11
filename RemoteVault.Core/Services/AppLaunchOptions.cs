namespace RemoteVault.Core.Services;

public sealed class AppLaunchOptions
{
    public bool IsConfigMode { get; init; }

    public bool IsDatabaseConfigMode { get; init; }

    public bool RunScheduledBackup { get; init; }

    public bool RunScheduledDatabaseBackup { get; init; }

    public bool RunScheduledDatabaseFullBackup { get; init; }

    public bool RunScheduledDatabaseDiffBackup { get; init; }

    public bool RunCleanOldVersions { get; init; }

    /// <summary>Versions to keep per file; null when the flag value is missing or not a number.</summary>
    public int? CleanOldVersionsKeep { get; init; }
}
