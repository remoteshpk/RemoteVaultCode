using RemoteVault.Contracts.Dtos;

namespace RemoteVault.Core.Services;

public interface IAppStateService
{
    Task<AppSummaryDto> LoadSummaryAsync(CancellationToken cancellationToken = default);
}
