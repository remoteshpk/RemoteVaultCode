using Amazon.S3.Model;

namespace RemoteVault.Core.Services;

public sealed class NativeConnectionTestService : IConnectionTestService
{
    public async Task<ConnectionTestResult> TestAsync(
        string providerId,
        string bucketName,
        string endpointValue,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken = default)
    {
        providerId = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        bucketName = (bucketName ?? string.Empty).Trim();
        endpointValue = (endpointValue ?? string.Empty).Trim();
        accessKey = (accessKey ?? string.Empty).Trim();
        secretKey = (secretKey ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(bucketName))
        {
            return Fail("Bucket or container name is required.");
        }

        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            return Fail("Endpoint is required.");
        }

        if (string.IsNullOrWhiteSpace(accessKey))
        {
            return Fail(providerId == "azure" ? "Storage account is required." : "Access key is required.");
        }

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            return Fail(providerId == "azure" ? "Secret key is required." : "Secret access key is required.");
        }

        try
        {
            return providerId switch
            {
                "azure" => await TestAzureAsync(bucketName, endpointValue, accessKey, secretKey, cancellationToken),
                "remote" or "wasabi" or "aws" or "contabo" or "hetzner" => await TestS3Async(providerId, bucketName, endpointValue, accessKey, secretKey, cancellationToken),
                _ => Fail($"Unsupported provider '{providerId}'.")
            };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult
            {
                IsSuccess = false,
                Message = "Connection failed.",
                Detail = ex.Message
            };
        }
    }

    private static async Task<ConnectionTestResult> TestS3Async(
        string providerId,
        string bucketName,
        string endpointValue,
        string accessKey,
        string secretKey,
        CancellationToken cancellationToken)
    {
        using var client = NativeStorageSupport.CreateS3Client(new StoredAppConfig
        {
            Provider = providerId,
            Endpoint = endpointValue,
            AccessKey = accessKey,
            SecretKey = secretKey
        });

        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucketName,
            MaxKeys = 1
        }, cancellationToken);

        return new ConnectionTestResult
        {
            IsSuccess = true,
            Message = $"Connection OK: RemoteVault:{bucketName}",
            Detail = $"Connection test successful. Bucket reachable, key count sampled: {response.KeyCount}."
        };
    }

    private static async Task<ConnectionTestResult> TestAzureAsync(
        string containerName,
        string endpointValue,
        string accountName,
        string secretKey,
        CancellationToken cancellationToken)
    {
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(new StoredAppConfig
        {
            Provider = "azure",
            Endpoint = endpointValue,
            AccessKey = accountName,
            SecretKey = secretKey
        }, containerName);

        var exists = await containerClient.ExistsAsync(cancellationToken);
        if (!exists.Value)
        {
            return new ConnectionTestResult
            {
                IsSuccess = false,
                Message = "Connection failed.",
                Detail = $"Container '{containerName}' was not found or is not accessible."
            };
        }

        await foreach (var _ in containerClient.GetBlobsAsync(cancellationToken: cancellationToken).AsPages(pageSizeHint: 1))
        {
            break;
        }

        return new ConnectionTestResult
        {
            IsSuccess = true,
            Message = $"Connection OK: RemoteVault:{containerName}",
            Detail = "Connection test successful. Container is reachable."
        };
    }

    private static ConnectionTestResult Fail(string message)
    {
        return new ConnectionTestResult
        {
            IsSuccess = false,
            Message = message,
            Detail = message
        };
    }
}
