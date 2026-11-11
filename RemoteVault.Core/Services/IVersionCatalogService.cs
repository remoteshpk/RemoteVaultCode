namespace RemoteVault.Core.Services;

public interface IVersionCatalogService
{
    Task<IReadOnlyList<RestoreVersionItem>> ListVersionsAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        CancellationToken cancellationToken = default);
}
