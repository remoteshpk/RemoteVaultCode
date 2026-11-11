namespace RemoteVault.Core.Services;

public interface IRestoreExecutionService
{
    Task<RestoreOperationResult> RunAsync(
        RestoreOperationRequest request,
        Action<RestoreWorkerEvent>? onProgress = null,
        CancellationToken cancellationToken = default);
}
