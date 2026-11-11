using Amazon.S3.Model;
using Azure.Storage.Blobs.Models;

namespace RemoteVault.Core.Services;

public sealed class NativeRemoteBrowserService : IRemoteBrowserService
{
    public async Task<IReadOnlyList<RemoteBrowserEntry>> ListAsync(
        StoredAppConfig config,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var providerId = (config.Provider ?? string.Empty).Trim().ToLowerInvariant();
        return providerId == "azure"
            ? await ListAzureAsync(config, remotePath, cancellationToken)
            : await ListS3Async(config, remotePath, cancellationToken);
    }

    private static async Task<IReadOnlyList<RemoteBrowserEntry>> ListS3Async(
        StoredAppConfig config,
        string remotePath,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(remotePath, out var bucketName, out var prefix))
        {
            return Array.Empty<RemoteBrowserEntry>();
        }

        var listPrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        using var client = NativeStorageSupport.CreateS3Client(config);
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucketName,
            Prefix = listPrefix,
            Delimiter = "/",
            MaxKeys = 1000
        }, cancellationToken);

        var items = new List<RemoteBrowserEntry>();

        foreach (var commonPrefix in response.CommonPrefixes)
        {
            var relative = commonPrefix[listPrefix.Length..].Trim('/');
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            items.Add(new RemoteBrowserEntry
            {
                Name = relative,
                RelativePath = relative,
                IsDirectory = true,
                ModifiedDisplay = string.Empty
            });
        }

        foreach (var item in response.S3Objects)
        {
            if (string.Equals(item.Key, listPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = item.Key.StartsWith(listPrefix, StringComparison.Ordinal)
                ? item.Key[listPrefix.Length..]
                : item.Key;

            if (string.IsNullOrWhiteSpace(relative) || relative.Contains('/'))
            {
                continue;
            }

            items.Add(new RemoteBrowserEntry
            {
                Name = relative,
                RelativePath = relative,
                IsDirectory = false,
                SizeBytes = item.Size,
                ModifiedDisplay = NativeStorageSupport.FormatModified(item.LastModified)
            });
        }

        return items
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<RemoteBrowserEntry>> ListAzureAsync(
        StoredAppConfig config,
        string remotePath,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(remotePath, out var containerName, out var prefix))
        {
            return Array.Empty<RemoteBrowserEntry>();
        }

        var listPrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        var items = new List<RemoteBrowserEntry>();

        await foreach (BlobHierarchyItem item in containerClient.GetBlobsByHierarchyAsync(
            delimiter: "/",
            prefix: listPrefix,
            cancellationToken: cancellationToken))
        {
            if (item.IsPrefix)
            {
                var relative = item.Prefix[listPrefix.Length..].Trim('/');
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                items.Add(new RemoteBrowserEntry
                {
                    Name = relative,
                    RelativePath = relative,
                    IsDirectory = true,
                    ModifiedDisplay = string.Empty
                });

                continue;
            }

            var blob = item.Blob;
            var relativeBlob = blob.Name.StartsWith(listPrefix, StringComparison.Ordinal)
                ? blob.Name[listPrefix.Length..]
                : blob.Name;

            if (string.IsNullOrWhiteSpace(relativeBlob) || relativeBlob.Contains('/'))
            {
                continue;
            }

            items.Add(new RemoteBrowserEntry
            {
                Name = relativeBlob,
                RelativePath = relativeBlob,
                IsDirectory = false,
                SizeBytes = blob.Properties.ContentLength,
                ModifiedDisplay = NativeStorageSupport.FormatModified(blob.Properties.LastModified)
            });
        }

        return items
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
