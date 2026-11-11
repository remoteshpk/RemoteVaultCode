namespace RemoteVault.Core.Services;

public interface IConnectionTestService
{
    Task<ConnectionTestResult> TestAsync(
        string providerId,
        string bucketName,
        string endpointValue,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default);
}
