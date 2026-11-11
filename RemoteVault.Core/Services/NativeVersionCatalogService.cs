using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs.Models;

namespace RemoteVault.Core.Services;

public sealed class NativeVersionCatalogService : IVersionCatalogService
{
    public async Task<IReadOnlyList<RestoreVersionItem>> ListVersionsAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var providerId = (config.Provider ?? string.Empty).Trim().ToLowerInvariant();
        return providerId == "azure"
            ? await ListAzureVersionsAsync(config, parentRemotePath, fileName, cancellationToken)
            : await ListS3VersionsAsync(config, parentRemotePath, fileName, cancellationToken);
    }

    private static async Task<IReadOnlyList<RestoreVersionItem>> ListS3VersionsAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(parentRemotePath, out var bucketName, out var prefix))
        {
            return Array.Empty<RestoreVersionItem>();
        }

        var objectKey = string.IsNullOrWhiteSpace(prefix) ? fileName : $"{prefix}/{fileName}";
        var items = new List<RestoreVersionItem>();

        using var client = NativeStorageSupport.CreateS3Client(config);
        string? keyMarker = null;
        string? versionMarker = null;
        var syntheticIndex = 0;

        do
        {
            var response = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                Prefix = objectKey,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
                MaxKeys = 100
            }, cancellationToken);

            foreach (var version in response.Versions
                .Where(item => string.Equals(item.Key, objectKey, StringComparison.Ordinal)))
            {
                var displayTime = NativeStorageSupport.FormatModified(version.LastModified);
                var isCurrent = version.IsLatest;
                var syntheticName = isCurrent
                    ? fileName
                    : BuildSyntheticVersionName(fileName, version.LastModified.ToUniversalTime(), syntheticIndex++);

                items.Add(new RestoreVersionItem
                {
                    Name = syntheticName,
                    VersionName = isCurrent ? "Current" : $"v{displayTime}",
                    DateTime = displayTime,
                    SizeKb = (int)Math.Round(version.Size / 1024d, MidpointRounding.AwayFromZero),
                    IsCurrent = isCurrent,
                    VersionId = version.VersionId,
                    Kind = "s3"
                });
            }

            keyMarker = response.NextKeyMarker;
            versionMarker = response.NextVersionIdMarker;
            if (!response.IsTruncated)
            {
                break;
            }
        }
        while (true);

        return items
            .OrderByDescending(item => item.DateTime, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<IReadOnlyList<RestoreVersionItem>> ListAzureVersionsAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(parentRemotePath, out var containerName, out var prefix))
        {
            return Array.Empty<RestoreVersionItem>();
        }

        var blobName = string.IsNullOrWhiteSpace(prefix) ? fileName : $"{prefix}/{fileName}";
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        var items = new List<RestoreVersionItem>();

        await foreach (BlobItem blob in containerClient.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.Version,
            prefix: blobName,
            cancellationToken: cancellationToken))
        {
            if (!string.Equals(blob.Name, blobName, StringComparison.Ordinal))
            {
                continue;
            }

            var isCurrent = blob.IsLatestVersion == true || string.IsNullOrWhiteSpace(blob.VersionId);
            var displayTime = NativeStorageSupport.FormatModified(blob.Properties.LastModified);
            items.Add(new RestoreVersionItem
            {
                Name = isCurrent ? fileName : (blob.VersionId ?? fileName),
                VersionName = isCurrent ? "Current" : $"Version {displayTime}",
                DateTime = displayTime,
                SizeKb = blob.Properties.ContentLength.HasValue
                    ? (int)Math.Round(blob.Properties.ContentLength.Value / 1024d, MidpointRounding.AwayFromZero)
                    : null,
                IsCurrent = isCurrent,
                VersionId = blob.VersionId,
                Kind = "azure"
            });
        }

        return items
            .OrderByDescending(item => item.DateTime, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildSyntheticVersionName(string fileName, DateTime utcDateTime, int index)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = fileName[..Math.Max(0, fileName.Length - extension.Length)];
        return $"{baseName}-v{utcDateTime:yyyy-MM-dd-HHmmss}-{index:000}{extension}";
    }
}
