namespace RemoteVault.Core.Services;

public interface IBackupExecutionService
{
    Task<BackupExecutionResult> RunAsync(
        BackupExecutionRequest request,
        Action<BackupProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default);
}
