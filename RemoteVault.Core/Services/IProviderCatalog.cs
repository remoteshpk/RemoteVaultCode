namespace RemoteVault.Core.Services;

public interface IProviderCatalog
{
    IReadOnlyList<ProviderOption> GetProviders();
}
