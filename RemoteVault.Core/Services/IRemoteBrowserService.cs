namespace RemoteVault.Core.Services;

public interface IRemoteBrowserService
{
    Task<IReadOnlyList<RemoteBrowserEntry>> ListAsync(
        StoredAppConfig config,
        string remotePath,
        CancellationToken cancellationToken = default);
}
