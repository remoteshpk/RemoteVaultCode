namespace RemoteVault.Core.Services;

public interface IOperationHistoryStore
{
    Task<OperationHistorySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task AppendAsync(OperationHistoryEntry entry, CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);
}
