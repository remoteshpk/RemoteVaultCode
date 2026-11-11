namespace RemoteVault.Core.Services;

public interface IDatabaseBackupService
{
    Task<DatabaseBackupLoadResult> LoadProfilesAsync(
        string configPath,
        CancellationToken cancellationToken = default);

    Task<DatabaseBackupSaveResult> SaveProfilesAsync(
        string configPath,
        IReadOnlyList<DatabaseBackupProfilePayload> profiles,
        CancellationToken cancellationToken = default);

    Task<DatabaseBackupDiscoverResult> DiscoverDatabasesAsync(
        string configPath,
        DatabaseBackupProfilePayload profile,
        CancellationToken cancellationToken = default);

    Task<DatabaseBackupRunResult> RunNowAsync(
        DatabaseBackupRunRequest request,
        Action<DatabaseBackupWorkerEvent>? onProgress = null,
        CancellationToken cancellationToken = default);
}
