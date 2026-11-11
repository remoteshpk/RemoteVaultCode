using Amazon.S3.Model;
using Azure.Storage.Blobs.Models;

namespace RemoteVault.Core.Services;

public sealed class NativeVersionCleanupService : IVersionCleanupService
{
    public const int MinimumKeep = 5;
    public const int MaximumKeep = 100;

    private readonly IOperationHistoryStore _operationHistoryStore;

    public NativeVersionCleanupService(IOperationHistoryStore operationHistoryStore)
    {
        _operationHistoryStore = operationHistoryStore;
    }

    public Task<VersionCleanupResult> PreviewAsync(
        string configPath,
        int keepVersions,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(configPath, keepVersions, preview: true, onProgress, cancellationToken);

    public Task<VersionCleanupResult> RunAsync(
        string configPath,
        int keepVersions,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(configPath, keepVersions, preview: false, onProgress, cancellationToken);

    private async Task<VersionCleanupResult> ExecuteAsync(
        string configPath,
        int keepVersions,
        bool preview,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var keep = Math.Clamp(keepVersions, MinimumKeep, MaximumKeep);

        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return new VersionCleanupResult { IsSuccess = false, ExitCode = -1, Message = $"Config file not found: {configPath}" };
        }

        var config = await LocalConfigService.LoadFromPathAsync(configPath, cancellationToken);
        if (config is null)
        {
            return new VersionCleanupResult { IsSuccess = false, ExitCode = -2, Message = "Configuration is missing or invalid." };
        }

        var bucketName = (config.BucketName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            return new VersionCleanupResult { IsSuccess = false, ExitCode = -3, Message = "No bucket/container is configured." };
        }

        var prefixes = BuildPrefixes(config);
        if (prefixes.Count == 0)
        {
            return new VersionCleanupResult { IsSuccess = false, ExitCode = -3, Message = "No backup folders are configured; nothing to clean." };
        }

        var isAzure = string.Equals(config.Provider, "azureblob", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase);

        var filesExamined = 0;
        var versionsDeleted = 0;
        var failedDeletions = 0;
        long bytesFreed = 0;

        try
        {
            foreach (var prefix in prefixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var verb = preview ? "Checking" : "Cleaning";
                onProgress?.Invoke(string.IsNullOrEmpty(prefix)
                    ? $"{verb} old versions in '{bucketName}' (keeping {keep} per file)..."
                    : $"{verb} old versions under '{bucketName}/{prefix}' (keeping {keep} per file)...");

                var (examined, deleted, failed, bytes) = isAzure
                    ? await CleanAzurePrefixAsync(config, bucketName, prefix, keep, preview, onProgress, cancellationToken)
                    : await CleanS3PrefixAsync(config, bucketName, prefix, keep, preview, onProgress, cancellationToken);

                filesExamined += examined;
                versionsDeleted += deleted;
                failedDeletions += failed;
                bytesFreed += bytes;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!preview)
            {
                await AppendHistoryAsync(config, bucketName, keep, filesExamined, versionsDeleted, failedDeletions, error: ex.Message, cancellationToken);
            }

            return new VersionCleanupResult
            {
                IsSuccess = false,
                ExitCode = 1,
                Message = preview ? $"Could not check old versions: {ex.Message}" : $"Version cleanup failed: {ex.Message}",
                FilesExamined = filesExamined,
                VersionsDeleted = versionsDeleted,
                FailedDeletions = failedDeletions,
                BytesFreed = bytesFreed,
                IsPreview = preview
            };
        }

        if (preview)
        {
            return new VersionCleanupResult
            {
                IsSuccess = true,
                ExitCode = 0,
                Message = versionsDeleted == 0
                    ? $"Nothing to clean up: no file has more than {keep} version(s)."
                    : $"{versionsDeleted} old version(s) across {filesExamined} file(s) can be deleted, keeping the newest {keep} per file.",
                FilesExamined = filesExamined,
                VersionsDeleted = versionsDeleted,
                BytesFreed = bytesFreed,
                IsPreview = true
            };
        }

        await AppendHistoryAsync(config, bucketName, keep, filesExamined, versionsDeleted, failedDeletions, error: null, cancellationToken);

        var success = failedDeletions == 0;
        var message = success
            ? $"Deleted {versionsDeleted} old version(s) across {filesExamined} file(s), keeping the newest {keep} per file."
            : $"Deleted {versionsDeleted} old version(s), but {failedDeletions} deletion(s) failed.";
        onProgress?.Invoke(message);

        return new VersionCleanupResult
        {
            IsSuccess = success,
            ExitCode = success ? 0 : 1,
            Message = message,
            FilesExamined = filesExamined,
            VersionsDeleted = versionsDeleted,
            FailedDeletions = failedDeletions,
            BytesFreed = bytesFreed
        };
    }

    private async Task AppendHistoryAsync(
        StoredAppConfig config,
        string bucketName,
        int keep,
        int filesExamined,
        int versionsDeleted,
        int failedDeletions,
        string? error,
        CancellationToken cancellationToken)
    {
        var status = error is not null || failedDeletions > 0
            ? (versionsDeleted > 0 ? "partial" : "failed")
            : "success";

        await _operationHistoryStore.AppendAsync(new OperationHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Category = "maintenance",
            Action = "clean-versions",
            Status = status,
            Summary = $"Version cleanup keeping the newest {keep} version(s) per file.",
            Source = $"{filesExamined} file(s)",
            Target = bucketName,
            Provider = config.Provider ?? string.Empty,
            TotalCount = filesExamined,
            SuccessCount = versionsDeleted,
            FailedCount = failedDeletions,
            Detail = error is null
                ? $"Deleted {versionsDeleted} old version(s)."
                : $"Deleted {versionsDeleted} old version(s) before failing: {error}"
        }, cancellationToken);
    }

    private static List<string> BuildPrefixes(StoredAppConfig config)
    {
        var prefixes = (config.BackupFolders ?? [])
            .Where(folder => !string.IsNullOrWhiteSpace(folder.LocalPath))
            .Select(folder => NormalizeRemotePrefix(folder.RemoteSubfolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A folder backed up to the bucket root covers every key, so one
        // root-level pass replaces all narrower prefixes.
        return prefixes.Any(string.IsNullOrEmpty) ? [string.Empty] : prefixes;
    }

    private static async Task<(int Examined, int Deleted, int Failed, long Bytes)> CleanS3PrefixAsync(
        StoredAppConfig config,
        string bucketName,
        string prefix,
        int keep,
        bool preview,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        using var client = NativeStorageSupport.CreateS3Client(config);
        var listPrefix = string.IsNullOrEmpty(prefix) ? null : prefix + "/";
        var versionsByKey = new Dictionary<string, List<S3ObjectVersion>>(StringComparer.Ordinal);

        string? keyMarker = null;
        string? versionMarker = null;
        do
        {
            var response = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                Prefix = listPrefix,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
                MaxKeys = 1000
            }, cancellationToken);

            foreach (var version in response.Versions)
            {
                if (version.IsDeleteMarker)
                    continue;

                if (!versionsByKey.TryGetValue(version.Key, out var list))
                {
                    list = [];
                    versionsByKey[version.Key] = list;
                }

                list.Add(version);
            }

            keyMarker = response.NextKeyMarker;
            versionMarker = response.NextVersionIdMarker;
            if (!response.IsTruncated)
                break;
        }
        while (true);

        var examined = 0;
        var deleted = 0;
        var failed = 0;
        long bytes = 0;

        foreach (var (key, versions) in versionsByKey)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            var stale = versions
                .OrderByDescending(version => version.IsLatest)
                .ThenByDescending(version => version.LastModified)
                .Skip(keep);

            foreach (var version in stale)
            {
                var size = Convert.ToInt64(version.Size);
                if (preview)
                {
                    deleted++;
                    bytes += size;
                    continue;
                }

                try
                {
                    await client.DeleteObjectAsync(new DeleteObjectRequest
                    {
                        BucketName = bucketName,
                        Key = key,
                        VersionId = version.VersionId
                    }, cancellationToken);
                    deleted++;
                    bytes += size;
                    onProgress?.Invoke($"Deleted old version of: {key} ({version.LastModified:yyyy-MM-dd HH:mm:ss})");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    onProgress?.Invoke($"Failed to delete version of {key}: {ex.Message}");
                }
            }
        }

        return (examined, deleted, failed, bytes);
    }

    private static async Task<(int Examined, int Deleted, int Failed, long Bytes)> CleanAzurePrefixAsync(
        StoredAppConfig config,
        string containerName,
        string prefix,
        int keep,
        bool preview,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        var listPrefix = string.IsNullOrEmpty(prefix) ? null : prefix + "/";
        var versionsByName = new Dictionary<string, List<BlobItem>>(StringComparer.Ordinal);

        await foreach (var blob in containerClient.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.Version,
            prefix: listPrefix,
            cancellationToken: cancellationToken))
        {
            if (!versionsByName.TryGetValue(blob.Name, out var list))
            {
                list = [];
                versionsByName[blob.Name] = list;
            }

            list.Add(blob);
        }

        var examined = 0;
        var deleted = 0;
        var failed = 0;
        long bytes = 0;

        foreach (var (name, versions) in versionsByName)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            // Azure blob version ids are RFC 3339 timestamps, so ordinal
            // descending order is newest-first.
            var stale = versions
                .OrderByDescending(blob => blob.IsLatestVersion == true)
                .ThenByDescending(blob => blob.VersionId, StringComparer.Ordinal)
                .Skip(keep);

            foreach (var blob in stale)
            {
                if (blob.IsLatestVersion == true || string.IsNullOrWhiteSpace(blob.VersionId))
                    continue;

                var size = blob.Properties?.ContentLength ?? 0;
                if (preview)
                {
                    deleted++;
                    bytes += size;
                    continue;
                }

                try
                {
                    await containerClient.GetBlobClient(name)
                        .WithVersion(blob.VersionId)
                        .DeleteAsync(cancellationToken: cancellationToken);
                    deleted++;
                    bytes += size;
                    onProgress?.Invoke($"Deleted old version of: {name} (v{blob.VersionId})");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    onProgress?.Invoke($"Failed to delete version of {name}: {ex.Message}");
                }
            }
        }

        return (examined, deleted, failed, bytes);
    }

    private static string NormalizeRemotePrefix(string? value)
    {
        return (value ?? string.Empty).Trim().Replace("\\", "/").Trim('/');
    }
}
