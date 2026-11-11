using RemoteVault.Contracts.Dtos;

namespace RemoteVault.Core.Services;

public sealed class AppStateService(
    IAppConfigStore configStore,
    IBackupStateStore backupStateStore) : IAppStateService
{
    public async Task<AppSummaryDto> LoadSummaryAsync(CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        var backupState = await backupStateStore.LoadAsync(cancellationToken);

        var storageDisplay = BuildStorageDisplay(config);
        var lastBackupDisplay = BuildLastBackupDisplay(backupState);

        return new AppSummaryDto
        {
            HealthState = BuildHealthState(config, backupState),
            LastBackupDisplay = lastBackupDisplay,
            NextBackupDisplay = BuildNextBackupDisplay(config),
            StorageDisplay = storageDisplay
        };
    }

    private static string BuildHealthState(StoredAppConfig? config, BackupStateSnapshot backupState)
    {
        if (config is null)
        {
            return "Configuration required";
        }

        if (backupState.Status is null)
        {
            return "Ready for first backup";
        }

        return backupState.Status.Success
            ? "Protection ready"
            : "Attention needed";
    }

    private static string BuildLastBackupDisplay(BackupStateSnapshot backupState)
    {
        if (backupState.Status is null || string.IsNullOrWhiteSpace(backupState.Status.Time))
        {
            return "No backups recorded";
        }

        return backupState.Status.Success
            ? $"{backupState.Status.Time} (Success)"
            : $"{backupState.Status.Time} (Issues detected)";
    }

    private static string BuildStorageDisplay(StoredAppConfig? config)
    {
        if (config is null)
        {
            return "Not configured";
        }

        var provider = string.IsNullOrWhiteSpace(config.Provider) ? "Storage" : config.Provider;
        var bucket = string.IsNullOrWhiteSpace(config.BucketName) ? "No bucket" : config.BucketName;
        return $"{provider} / {bucket}";
    }

    private static string BuildNextBackupDisplay(StoredAppConfig? config)
    {
        var schedule = config?.FileBackupSchedule;
        if (schedule is null || !schedule.Enabled)
        {
            return "No scheduler configured";
        }

        if (string.Equals(schedule.Frequency, "hourly", StringComparison.OrdinalIgnoreCase))
        {
            return $"Every {Math.Clamp(schedule.IntervalHours, 1, 24)} hour(s)";
        }

        return $"Daily at {schedule.DailyTime}";
    }
}
