namespace RemoteVault.Core.Services;

public interface IBackupStateStore
{
    Task<BackupStateSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<BackupHistoryEntry> history,
        BackupStatusSnapshot status,
        CancellationToken cancellationToken = default);
}
