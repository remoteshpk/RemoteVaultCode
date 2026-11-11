using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace RemoteVault.Core.Services;

public sealed class NativeRestoreExecutionService : IRestoreExecutionService
{
    private readonly IVersionCatalogService _versionCatalogService;
    private readonly IOperationHistoryStore _operationHistoryStore;

    public NativeRestoreExecutionService(IVersionCatalogService versionCatalogService, IOperationHistoryStore operationHistoryStore)
    {
        _versionCatalogService = versionCatalogService;
        _operationHistoryStore = operationHistoryStore;
    }

    public async Task<RestoreOperationResult> RunAsync(
        RestoreOperationRequest request,
        Action<RestoreWorkerEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ConfigPath) || !File.Exists(request.ConfigPath))
        {
            return Fail($"Config file not found: {request.ConfigPath}", -1);
        }

        var config = await LoadConfigAsync(request.ConfigPath, cancellationToken);
        if (config is null)
        {
            return Fail("Configuration is missing or invalid.", -2);
        }

        try
        {
            return (request.Command ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "restore_selected" => await RestoreSelectedAsync(config, request, onProgress, cancellationToken),
                "restore_version" => await RestoreVersionAsync(config, request, onProgress, cancellationToken),
                "restore_by_timestamp" => await RestoreByTimestampAsync(config, request, onProgress, cancellationToken),
                "list_versions" => await ListVersionsAsync(config, request, cancellationToken),
                _ => Fail($"Unsupported restore command: {request.Command}", -3)
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, 10);
        }
    }

    private async Task<RestoreOperationResult> RestoreSelectedAsync(
        StoredAppConfig config,
        RestoreOperationRequest request,
        Action<RestoreWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        var tasks = request.Tasks?.Where(task => !string.IsNullOrWhiteSpace(task.RemoteItemPath) && !string.IsNullOrWhiteSpace(task.LocalTarget)).ToList()
            ?? [];

        var success = 0;
        var failed = 0;
        var index = 0;

        foreach (var task in tasks)
        {
            index++;
            Emit(onProgress, "status", new RestoreWorkerPayload
            {
                Remote = task.RemoteItemPath,
                Local = task.LocalTarget,
                Index = index,
                Total = tasks.Count
            });

            try
            {
                if (task.IsDirectory)
                {
                    var restored = await RestoreDirectoryCurrentAsync(config, task.RemoteItemPath, task.LocalTarget, cancellationToken);
                    if (restored > 0)
                    {
                        success++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                else
                {
                    await RestoreCurrentFileAsync(config, task.RemoteItemPath, task.LocalTarget, cancellationToken);
                    success++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Emit(onProgress, "fatal", message: ex.Message);
            }
        }

        Emit(onProgress, "completed", success: success, failed: failed, total: tasks.Count);
        var result = new RestoreOperationResult
        {
            IsSuccess = failed == 0,
            ExitCode = failed == 0 ? 0 : 1,
            Message = failed == 0 ? "Restore completed successfully." : "Restore finished with errors.",
            SuccessCount = success,
            FailedCount = failed,
            TotalCount = tasks.Count
        };
        await _operationHistoryStore.AppendAsync(
            BuildRestoreEntry(
                config,
                "restore_selected",
                result.Message,
                tasks.Count,
                success,
                failed,
                tasks.Count == 0 ? string.Empty : tasks[0].RemoteItemPath,
                // Selected-item restores carry the destination per task, not on the request.
                !string.IsNullOrWhiteSpace(request.DestinationRoot)
                    ? request.DestinationRoot
                    : tasks.Count == 0 ? string.Empty : tasks[0].LocalTarget),
            cancellationToken);
        return result;
    }

    private async Task<RestoreOperationResult> RestoreVersionAsync(
        StoredAppConfig config,
        RestoreOperationRequest request,
        Action<RestoreWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        var destinationRoot = (request.DestinationRoot ?? string.Empty).Trim();
        var parentRemotePath = (request.ParentRemotePath ?? string.Empty).Trim();
        var fileName = (request.FileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(destinationRoot) || string.IsNullOrWhiteSpace(parentRemotePath) || string.IsNullOrWhiteSpace(fileName))
        {
            return Fail("Restore version requires destination, parent remote path, and file name.", -4);
        }

        var relativePath = BuildRelativePathFromRoot(
            request.RootRemote ?? string.Empty,
            parentRemotePath,
            fileName);

        var outputPath = BuildVersionRestoreOutputPath(destinationRoot, relativePath, request.DateTime, request.IsCurrent == true);
        Emit(onProgress, "status", new RestoreWorkerPayload
        {
            Remote = $"{parentRemotePath.TrimEnd('/')}/{fileName}",
            Local = outputPath,
            Index = 1,
            Total = 1
        });

        if (string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            await RestoreAzureVersionAsync(config, parentRemotePath, fileName, outputPath, request.VersionId, cancellationToken);
        }
        else
        {
            await RestoreS3VersionAsync(config, parentRemotePath, fileName, outputPath, request.VersionId, cancellationToken);
        }

        TryApplyTimestamp(outputPath, request.DateTime);
        Emit(onProgress, "completed", success: 1, failed: 0, total: 1, destination: outputPath);
        var result = new RestoreOperationResult
        {
            IsSuccess = true,
            ExitCode = 0,
            Message = $"Version restored to: {outputPath}",
            SuccessCount = 1,
            FailedCount = 0,
            TotalCount = 1,
            DestinationPath = outputPath
        };
        await _operationHistoryStore.AppendAsync(
            BuildRestoreEntry(
                config,
                "restore_version",
                result.Message,
                1,
                1,
                0,
                $"{parentRemotePath.TrimEnd('/')}/{fileName}",
                outputPath),
            cancellationToken);
        return result;
    }

    private async Task<RestoreOperationResult> RestoreByTimestampAsync(
        StoredAppConfig config,
        RestoreOperationRequest request,
        Action<RestoreWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationRoot) || string.IsNullOrWhiteSpace(request.CutoffTimestamp))
        {
            return Fail("Timestamp restore requires destination and cutoff timestamp.", -5);
        }

        var cutoff = DateTime.Parse(request.CutoffTimestamp);
        var rootRemote = BuildRootRemote(config, request.BaseEntry);
        var total = 0;
        var success = 0;
        var failed = 0;

        foreach (var folderRemote in request.FolderRemotes ?? [])
        {
            var items = string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase)
                ? await SelectAzureFolderVersionsAsync(config, folderRemote, rootRemote, request.DestinationRoot!, cutoff, cancellationToken)
                : await SelectS3FolderVersionsAsync(config, folderRemote, rootRemote, request.DestinationRoot!, cutoff, cancellationToken);

            foreach (var item in items)
            {
                total++;
                Emit(onProgress, "status", new RestoreWorkerPayload
                {
                    Remote = item.RemotePath,
                    Local = item.LocalTarget,
                    Index = total,
                    Total = null
                });

                try
                {
                    await DownloadSelectedVersionAsync(config, item, cancellationToken);
                    TryApplyTimestamp(item.LocalTarget, item.DisplayTime);
                    success++;
                }
                catch (Exception ex)
                {
                    failed++;
                    Emit(onProgress, "fatal", message: ex.Message);
                }
            }
        }

        foreach (var fileItem in request.FileItems ?? [])
        {
            total++;
            try
            {
                var selected = string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase)
                    ? await SelectAzureFileVersionAsync(config, fileItem.ParentRemote, fileItem.FileName, rootRemote, request.DestinationRoot!, fileItem.RelativePath, cutoff, cancellationToken)
                    : await SelectS3FileVersionAsync(config, fileItem.ParentRemote, fileItem.FileName, rootRemote, request.DestinationRoot!, fileItem.RelativePath, cutoff, cancellationToken);

                Emit(onProgress, "status", new RestoreWorkerPayload
                {
                    Remote = selected.RemotePath,
                    Local = selected.LocalTarget,
                    Index = total,
                    Total = null
                });

                await DownloadSelectedVersionAsync(config, selected, cancellationToken);
                TryApplyTimestamp(selected.LocalTarget, selected.DisplayTime);
                success++;
            }
            catch (Exception ex)
            {
                failed++;
                Emit(onProgress, "fatal", message: ex.Message);
            }
        }

        Emit(onProgress, "completed", success: success, failed: failed, total: total);
        var result = new RestoreOperationResult
        {
            IsSuccess = failed == 0,
            ExitCode = failed == 0 ? 0 : 1,
            Message = failed == 0 ? "Timestamp restore completed successfully." : "Timestamp restore finished with errors.",
            SuccessCount = success,
            FailedCount = failed,
            TotalCount = total
        };
        await _operationHistoryStore.AppendAsync(
            BuildRestoreEntry(
                config,
                "restore_by_timestamp",
                result.Message,
                total,
                success,
                failed,
                rootRemote,
                request.DestinationRoot ?? string.Empty),
            cancellationToken);
        return result;
    }

    private async Task<RestoreOperationResult> ListVersionsAsync(
        StoredAppConfig config,
        RestoreOperationRequest request,
        CancellationToken cancellationToken)
    {
        var items = await _versionCatalogService.ListVersionsAsync(
            config,
            request.ParentRemotePath ?? string.Empty,
            request.FileName ?? string.Empty,
            cancellationToken);

        return new RestoreOperationResult
        {
            IsSuccess = true,
            ExitCode = 0,
            Message = items.Count == 0 ? "No versions found." : "Versions loaded.",
            VersionItems = items
        };
    }

    private static async Task<StoredAppConfig?> LoadConfigAsync(string path, CancellationToken cancellationToken)
    {
        return await LocalConfigService.LoadFromPathAsync(path, cancellationToken);
    }

    private static OperationHistoryEntry BuildRestoreEntry(
        StoredAppConfig config,
        string action,
        string summary,
        int total,
        int success,
        int failed,
        string source,
        string destination)
    {
        var status = failed == 0 ? "success" : (success > 0 ? "partial" : "failed");
        return new OperationHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Category = "restore",
            Action = action,
            Status = status,
            Summary = summary,
            Source = source,
            Target = destination,
            Provider = config.Provider ?? string.Empty,
            TotalCount = total,
            SuccessCount = success,
            FailedCount = failed,
            Detail = $"Destination: {destination}"
        };
    }

    private static string BuildRootRemote(StoredAppConfig config, RestoreBaseEntry? baseEntry)
    {
        var remoteName = string.IsNullOrWhiteSpace(config.RemoteName) ? "RemoteVault" : config.RemoteName.Trim();
        var bucket = config.BucketName.Trim();
        var subFolder = (baseEntry?.SubFolder ?? string.Empty).Trim().Trim('/');
        return string.IsNullOrWhiteSpace(subFolder)
            ? $"{remoteName}:{bucket}"
            : $"{remoteName}:{bucket}/{subFolder}";
    }

    private static string BuildRelativePathFromRoot(string rootRemote, string parentRemotePath, string fileName)
    {
        var remoteFile = $"{parentRemotePath.TrimEnd('/')}/{fileName}".Replace("\\", "/");
        var normalizedRoot = (rootRemote ?? string.Empty).Trim().TrimEnd('/');
        return remoteFile.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)
            ? remoteFile[(normalizedRoot.Length + 1)..]
            : fileName;
    }

    private static string BuildVersionRestoreOutputPath(string destinationRoot, string relativePath, string? displayTime, bool isCurrent)
    {
        var relative = relativePath.Replace("/", Path.DirectorySeparatorChar.ToString());
        if (isCurrent || string.IsNullOrWhiteSpace(displayTime))
        {
            var output = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            return output;
        }

        var dir = Path.GetDirectoryName(relative) ?? string.Empty;
        var name = Path.GetFileName(relative);
        var extension = Path.GetExtension(name);
        var baseName = name[..Math.Max(0, name.Length - extension.Length)];
        var safe = displayTime.Replace(":", string.Empty).Replace(" ", "-");
        var stampedName = $"{baseName}-v{safe}{extension}";
        var outputPath = string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(destinationRoot, stampedName)
            : Path.Combine(destinationRoot, dir, stampedName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        return outputPath;
    }

    private static async Task RestoreCurrentFileAsync(StoredAppConfig config, string remoteItemPath, string localTarget, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localTarget)!);
        if (string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAzureBlobAsync(config, remoteItemPath, localTarget, null, cancellationToken);
        }
        else
        {
            await DownloadS3ObjectAsync(config, remoteItemPath, localTarget, null, cancellationToken);
        }
    }

    private static async Task<int> RestoreDirectoryCurrentAsync(StoredAppConfig config, string remoteItemPath, string localTarget, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localTarget);
        var restored = 0;

        if (string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in await ListAzureCurrentItemsAsync(config, remoteItemPath, cancellationToken))
            {
                var relative = item.RemotePath[(remoteItemPath.TrimEnd('/').Length + 1)..];
                var output = Path.Combine(localTarget, relative.Replace("/", Path.DirectorySeparatorChar.ToString()));
                await DownloadAzureBlobAsync(config, item.RemotePath, output, null, cancellationToken);
                restored++;
            }
        }
        else
        {
            foreach (var item in await ListS3CurrentItemsAsync(config, remoteItemPath, cancellationToken))
            {
                var relative = item.RemotePath[(remoteItemPath.TrimEnd('/').Length + 1)..];
                var output = Path.Combine(localTarget, relative.Replace("/", Path.DirectorySeparatorChar.ToString()));
                await DownloadS3ObjectAsync(config, item.RemotePath, output, null, cancellationToken);
                restored++;
            }
        }

        return restored;
    }

    private static async Task RestoreS3VersionAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        string outputPath,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var remote = $"{parentRemotePath.TrimEnd('/')}/{fileName}";
        await DownloadS3ObjectAsync(config, remote, outputPath, string.IsNullOrWhiteSpace(versionId) ? null : versionId, cancellationToken);
    }

    private static async Task RestoreAzureVersionAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        string outputPath,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var remote = $"{parentRemotePath.TrimEnd('/')}/{fileName}";
        await DownloadAzureBlobAsync(config, remote, outputPath, string.IsNullOrWhiteSpace(versionId) ? null : versionId, cancellationToken);
    }

    private static async Task DownloadSelectedVersionAsync(StoredAppConfig config, SelectedRestoreItem item, CancellationToken cancellationToken)
    {
        if (string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            await DownloadAzureBlobAsync(config, item.RemotePath, item.LocalTarget, item.VersionId, cancellationToken);
        }
        else
        {
            await DownloadS3ObjectAsync(config, item.RemotePath, item.LocalTarget, item.VersionId, cancellationToken);
        }
    }

    private static async Task DownloadS3ObjectAsync(
        StoredAppConfig config,
        string remotePath,
        string outputPath,
        string? versionId,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(remotePath, out var bucketName, out var key))
        {
            throw new InvalidOperationException($"Invalid remote path '{remotePath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var client = NativeStorageSupport.CreateS3Client(config);
        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            VersionId = versionId
        }, cancellationToken);
        await using var output = File.Create(outputPath);
        await response.ResponseStream.CopyToAsync(output, cancellationToken);
    }

    private static async Task DownloadAzureBlobAsync(
        StoredAppConfig config,
        string remotePath,
        string outputPath,
        string? versionId,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(remotePath, out var containerName, out var blobName))
        {
            throw new InvalidOperationException($"Invalid remote path '{remotePath}'.");
        }

        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        BlobClient blobClient = string.IsNullOrWhiteSpace(versionId)
            ? containerClient.GetBlobClient(blobName)
            : containerClient.GetBlobClient(blobName).WithVersion(versionId);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await blobClient.DownloadToAsync(outputPath, cancellationToken);
    }

    private static async Task<List<SelectedRestoreItem>> SelectS3FolderVersionsAsync(
        StoredAppConfig config,
        string folderRemote,
        string rootRemote,
        string destinationRoot,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(folderRemote, out var bucketName, out var prefix))
        {
            return [];
        }

        var prefixValue = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        using var client = NativeStorageSupport.CreateS3Client(config);
        var grouped = new Dictionary<string, S3ObjectVersion>(StringComparer.Ordinal);

        string? keyMarker = null;
        string? versionMarker = null;
        do
        {
            var response = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                Prefix = prefixValue,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
                MaxKeys = 1000
            }, cancellationToken);

            foreach (var version in response.Versions.Where(v => v.LastModified.ToLocalTime() <= cutoff))
            {
                if (!grouped.TryGetValue(version.Key, out var existing) || version.LastModified > existing.LastModified)
                {
                    grouped[version.Key] = version;
                }
            }

            keyMarker = response.NextKeyMarker;
            versionMarker = response.NextVersionIdMarker;
            if (!response.IsTruncated)
            {
                break;
            }
        }
        while (true);

        return grouped.Values
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var remotePath = $"{config.RemoteName}:{bucketName}/{item.Key}";
                var relative = remotePath[(rootRemote.TrimEnd('/').Length + 1)..];
                return new SelectedRestoreItem
                {
                    RemotePath = remotePath,
                    VersionId = item.VersionId,
                    RelativePath = relative,
                    LocalTarget = Path.Combine(destinationRoot, relative.Replace("/", Path.DirectorySeparatorChar.ToString())),
                    DisplayTime = item.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                };
            })
            .ToList();
    }

    private static async Task<List<SelectedRestoreItem>> SelectAzureFolderVersionsAsync(
        StoredAppConfig config,
        string folderRemote,
        string rootRemote,
        string destinationRoot,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(folderRemote, out var containerName, out var prefix))
        {
            return [];
        }

        var prefixValue = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        var grouped = new Dictionary<string, BlobItem>(StringComparer.Ordinal);

        await foreach (var blob in containerClient.GetBlobsAsync(states: BlobStates.Version, prefix: prefixValue, cancellationToken: cancellationToken))
        {
            var modified = blob.Properties.LastModified?.ToLocalTime();
            if (!modified.HasValue || modified.Value > cutoff)
            {
                continue;
            }

            if (!grouped.TryGetValue(blob.Name, out var existing) ||
                (blob.Properties.LastModified ?? DateTimeOffset.MinValue) > (existing.Properties.LastModified ?? DateTimeOffset.MinValue))
            {
                grouped[blob.Name] = blob;
            }
        }

        return grouped.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var remotePath = $"{config.RemoteName}:{containerName}/{item.Name}";
                var relative = remotePath[(rootRemote.TrimEnd('/').Length + 1)..];
                return new SelectedRestoreItem
                {
                    RemotePath = remotePath,
                    VersionId = item.VersionId,
                    RelativePath = relative,
                    LocalTarget = Path.Combine(destinationRoot, relative.Replace("/", Path.DirectorySeparatorChar.ToString())),
                    DisplayTime = NativeStorageSupport.FormatModified(item.Properties.LastModified)
                };
            })
            .ToList();
    }

    private static async Task<SelectedRestoreItem> SelectS3FileVersionAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        string rootRemote,
        string destinationRoot,
        string relativePath,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(parentRemotePath, out var bucketName, out var prefix))
        {
            throw new InvalidOperationException($"Invalid remote path '{parentRemotePath}'.");
        }

        var key = string.IsNullOrWhiteSpace(prefix) ? fileName : $"{prefix}/{fileName}";
        using var client = NativeStorageSupport.CreateS3Client(config);
        string? keyMarker = null;
        string? versionMarker = null;
        S3ObjectVersion? selected = null;

        do
        {
            var response = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucketName,
                Prefix = key,
                KeyMarker = keyMarker,
                VersionIdMarker = versionMarker,
                MaxKeys = 100
            }, cancellationToken);

            foreach (var version in response.Versions.Where(v => string.Equals(v.Key, key, StringComparison.Ordinal)))
            {
                if (version.LastModified.ToLocalTime() > cutoff)
                {
                    continue;
                }

                if (selected is null || version.LastModified > selected.LastModified)
                {
                    selected = version;
                }
            }

            keyMarker = response.NextKeyMarker;
            versionMarker = response.NextVersionIdMarker;
            if (!response.IsTruncated)
            {
                break;
            }
        }
        while (true);

        if (selected is null)
        {
            throw new InvalidOperationException($"No version of '{fileName}' exists on or before {cutoff:yyyy-MM-dd HH:mm:ss}.");
        }

        return new SelectedRestoreItem
        {
            RemotePath = $"{config.RemoteName}:{bucketName}/{key}",
            VersionId = selected.VersionId,
            RelativePath = relativePath,
            DisplayTime = selected.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            LocalTarget = Path.Combine(destinationRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()))
        };
    }

    private static async Task<SelectedRestoreItem> SelectAzureFileVersionAsync(
        StoredAppConfig config,
        string parentRemotePath,
        string fileName,
        string rootRemote,
        string destinationRoot,
        string relativePath,
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(parentRemotePath, out var containerName, out var prefix))
        {
            throw new InvalidOperationException($"Invalid remote path '{parentRemotePath}'.");
        }

        var key = string.IsNullOrWhiteSpace(prefix) ? fileName : $"{prefix}/{fileName}";
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        BlobItem? selected = null;

        await foreach (var blob in containerClient.GetBlobsAsync(states: BlobStates.Version, prefix: key, cancellationToken: cancellationToken))
        {
            if (!string.Equals(blob.Name, key, StringComparison.Ordinal))
            {
                continue;
            }

            var modified = blob.Properties.LastModified?.ToLocalTime();
            if (!modified.HasValue || modified.Value > cutoff)
            {
                continue;
            }

            if (selected is null ||
                (blob.Properties.LastModified ?? DateTimeOffset.MinValue) > (selected.Properties.LastModified ?? DateTimeOffset.MinValue))
            {
                selected = blob;
            }
        }

        if (selected is null)
        {
            throw new InvalidOperationException($"No version of '{fileName}' exists on or before {cutoff:yyyy-MM-dd HH:mm:ss}.");
        }

        return new SelectedRestoreItem
        {
            RemotePath = $"{config.RemoteName}:{containerName}/{key}",
            VersionId = selected.VersionId,
            RelativePath = relativePath,
            DisplayTime = NativeStorageSupport.FormatModified(selected.Properties.LastModified),
            LocalTarget = Path.Combine(destinationRoot, relativePath.Replace("/", Path.DirectorySeparatorChar.ToString()))
        };
    }

    private static async Task<List<SelectedRestoreItem>> ListS3CurrentItemsAsync(StoredAppConfig config, string folderRemote, CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(folderRemote, out var bucketName, out var prefix))
        {
            return [];
        }

        var prefixValue = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        using var client = NativeStorageSupport.CreateS3Client(config);
        var items = new List<SelectedRestoreItem>();
        string? token = null;

        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucketName,
                Prefix = prefixValue,
                ContinuationToken = token,
                MaxKeys = 1000
            }, cancellationToken);

            foreach (var item in response.S3Objects)
            {
                if (item.Key.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                items.Add(new SelectedRestoreItem
                {
                    RemotePath = $"{config.RemoteName}:{bucketName}/{item.Key}",
                    DisplayTime = item.LastModified.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            token = response.NextContinuationToken;
            if (!response.IsTruncated)
            {
                break;
            }
        }
        while (true);

        return items;
    }

    private static async Task<List<SelectedRestoreItem>> ListAzureCurrentItemsAsync(StoredAppConfig config, string folderRemote, CancellationToken cancellationToken)
    {
        if (!NativeStorageSupport.TryParseRemotePath(folderRemote, out var containerName, out var prefix))
        {
            return [];
        }

        var prefixValue = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/') + "/";
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, containerName);
        var items = new List<SelectedRestoreItem>();

        await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefixValue, cancellationToken: cancellationToken))
        {
            items.Add(new SelectedRestoreItem
            {
                RemotePath = $"{config.RemoteName}:{containerName}/{blob.Name}",
                DisplayTime = NativeStorageSupport.FormatModified(blob.Properties.LastModified)
            });
        }

        return items;
    }

    private static void TryApplyTimestamp(string path, string? displayTime)
    {
        if (string.IsNullOrWhiteSpace(displayTime) || !File.Exists(path))
        {
            return;
        }

        if (DateTime.TryParse(displayTime, out var parsed))
        {
            File.SetLastWriteTime(path, parsed);
            File.SetLastAccessTime(path, parsed);
        }
    }

    private static RestoreOperationResult Fail(string message, int exitCode)
    {
        return new RestoreOperationResult
        {
            IsSuccess = false,
            ExitCode = exitCode,
            Message = message
        };
    }

    private static void Emit(
        Action<RestoreWorkerEvent>? callback,
        string eventName,
        RestoreWorkerPayload? payload = null,
        bool? ok = null,
        string? message = null,
        int? success = null,
        int? failed = null,
        int? total = null,
        string? destination = null)
    {
        callback?.Invoke(new RestoreWorkerEvent
        {
            EventName = eventName,
            Payload = payload ?? new RestoreWorkerPayload(),
            Ok = ok,
            Message = message,
            Success = success,
            Failed = failed,
            Total = total,
            Destination = destination
        });
    }

    private sealed class SelectedRestoreItem
    {
        public string RemotePath { get; init; } = string.Empty;

        public string? VersionId { get; init; }

        public string RelativePath { get; init; } = string.Empty;

        public string LocalTarget { get; init; } = string.Empty;

        public string? DisplayTime { get; init; }
    }
}
