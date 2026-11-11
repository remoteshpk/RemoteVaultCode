using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;

namespace RemoteVault.Core.Services;

internal static class NativeStorageSupport
{
    public static string NormalizeHost(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
    }

    public static string InferAwsRegion(string endpoint)
    {
        var host = NormalizeHost(endpoint);
        if (host.StartsWith("s3.", StringComparison.OrdinalIgnoreCase) && host.Contains(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = host.Split('.');
            if (parts.Length >= 4)
            {
                return parts[1];
            }
        }

        return string.Empty;
    }

    public static string NormalizeAzureEndpoint(string accountName, string endpointSuffix)
    {
        var suffix = NormalizeHost(endpointSuffix);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = "blob.core.windows.net";
        }

        if (suffix.StartsWith(accountName + ".", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{suffix}";
        }

        return $"https://{accountName}.{suffix}";
    }

    public static bool TryParseRemotePath(string remotePath, out string bucketOrContainer, out string prefix)
    {
        bucketOrContainer = string.Empty;
        prefix = string.Empty;

        var normalized = (remotePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Contains(':'))
        {
            return false;
        }

        var tail = normalized[(normalized.IndexOf(':') + 1)..].Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        var slashIndex = tail.IndexOf('/');
        if (slashIndex < 0)
        {
            bucketOrContainer = tail;
            prefix = string.Empty;
            return true;
        }

        bucketOrContainer = tail[..slashIndex];
        prefix = tail[(slashIndex + 1)..].Trim('/');
        return !string.IsNullOrWhiteSpace(bucketOrContainer);
    }

    public static AmazonS3Client CreateS3Client(StoredAppConfig config)
    {
        var providerId = (config.Provider ?? string.Empty).Trim().ToLowerInvariant();
        var serviceUrl = NormalizeHost(config.Endpoint);
        var region = InferAwsRegion(serviceUrl);

        if (providerId is "remote" or "wasabi")
        {
            serviceUrl = serviceUrl.EndsWith(".remote.al", StringComparison.OrdinalIgnoreCase)
                ? serviceUrl[..^".remote.al".Length] + ".wasabisys.com"
                : serviceUrl;
        }

        var s3Config = new AmazonS3Config
        {
            ServiceURL = $"https://{serviceUrl}",
            ForcePathStyle = true
        };

        if (providerId == "aws")
        {
            s3Config.AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
        }
        else if (providerId == "hetzner")
        {
            s3Config.AuthenticationRegion = serviceUrl.Split('.', 2)[0].ToLowerInvariant();
        }
        else if (providerId == "contabo")
        {
            s3Config.AuthenticationRegion = "us-east-1";
        }
        else
        {
            s3Config.AuthenticationRegion = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
        }

        return new AmazonS3Client(
            new BasicAWSCredentials(config.AccessKey, config.SecretKey),
            s3Config);
    }

    public static BlobContainerClient CreateAzureContainerClient(StoredAppConfig config, string containerName)
    {
        var endpoint = NormalizeAzureEndpoint(config.AccessKey, config.Endpoint);
        var serviceUri = new Uri(endpoint);
        var credential = new Azure.Storage.StorageSharedKeyCredential(config.AccessKey, config.SecretKey);
        var serviceClient = new BlobServiceClient(serviceUri, credential);
        return serviceClient.GetBlobContainerClient(containerName);
    }

    public static string FormatModified(DateTimeOffset? value)
    {
        return value.HasValue
            ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : string.Empty;
    }
}
