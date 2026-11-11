using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Azure;
using Azure.Storage.Blobs.Models;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteVault.Core.Services;

public sealed class NativeBackupExecutionService : IBackupExecutionService
{
    // Content hash stamped on every uploaded object so later runs can detect
    // unchanged files even when the local manifest is gone (e.g. after reinstall).
    // Azure metadata keys must be valid C# identifiers, hence the underscore variant.
    private const string S3HashMetadataKey = "rv-md5";
    private const string AzureHashMetadataKey = "rv_md5";

    private readonly IBackupStateStore _backupStateStore;
    private readonly IOperationHistoryStore _operationHistoryStore;

    public NativeBackupExecutionService(IBackupStateStore backupStateStore, IOperationHistoryStore operationHistoryStore)
    {
        _backupStateStore = backupStateStore;
        _operationHistoryStore = operationHistoryStore;
    }

    public async Task<BackupExecutionResult> RunAsync(
        BackupExecutionRequest request,
        Action<BackupProgressEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ConfigPath) || !File.Exists(request.ConfigPath))
        {
            return new BackupExecutionResult
            {
                IsSuccess = false,
                Message = $"Config file not found: {request.ConfigPath}",
                ExitCode = -1
            };
        }

        var config = await LoadConfigAsync(request.ConfigPath, cancellationToken);
        if (config is null)
        {
            return new BackupExecutionResult
            {
                IsSuccess = false,
                Message = "Configuration is missing or invalid.",
                ExitCode = -2
            };
        }

        var sources = BuildSources(config, request.SelectedLocalPaths);
        if (sources.Count == 0)
        {
            Emit(onProgress, "finished", new BackupProgressPayload { Ok = false }, ok: false, message: "No backup sources matched the request.");
            return new BackupExecutionResult
            {
                IsSuccess = false,
                Message = "No backup sources matched the request.",
                ExitCode = -3
            };
        }

        var snapshot = await _backupStateStore.LoadAsync(cancellationToken);
        var history = snapshot.History.ToList();
        var overallSuccess = true;
        var runHistory = new List<BackupHistoryEntry>();

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scanResult = await ScanAsync(source, config.ExcludePatterns, onProgress, cancellationToken);

            Emit(onProgress, "start_entry", new BackupProgressPayload
            {
                LocalPath = source.LocalPath,
                RemoteDest = source.RemotePath,
                Index = source.Index,
                TotalSources = sources.Count,
                FilesTotal = scanResult.ChangedFiles.Count,
                BytesTotal = scanResult.BytesTotal
            });

            if (scanResult.Error is not null)
            {
                overallSuccess = false;
                history.Add(new BackupHistoryEntry
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    LocalPath = source.LocalPath,
                    RemoteDest = source.RemotePath,
                    Changed = 0,
                    Result = "Failed",
                    Message = scanResult.Error
                });
                runHistory.Add(history[^1]);

                Emit(onProgress, "end_entry", new BackupProgressPayload
                {
                    LocalPath = source.LocalPath,
                    RemoteDest = source.RemotePath,
                    Result = "Failed",
                    Message = scanResult.Error,
                    ChangedFiles = Array.Empty<string>()
                });
                continue;
            }

            if (scanResult.ChangedFiles.Count == 0)
            {
                history.Add(new BackupHistoryEntry
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    LocalPath = source.LocalPath,
                    RemoteDest = source.RemotePath,
                    Changed = 0,
                    Result = "OK",
                    Message = "No changes"
                });
                runHistory.Add(history[^1]);

                Emit(onProgress, "end_entry", new BackupProgressPayload
                {
                    LocalPath = source.LocalPath,
                    RemoteDest = source.RemotePath,
                    Result = "OK",
                    ChangedFiles = Array.Empty<string>()
                });
                continue;
            }

            var uploadedFiles = new List<BackupFileCandidate>();
            var skippedFiles = new List<BackupFileCandidate>();
            var sourceSucceeded = true;

            foreach (var file in scanResult.ChangedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    Emit(onProgress, "rclone_stats", new BackupProgressPayload
                    {
                        Message = $"Uploading: {file.RelativePath}"
                    });

                    var wasUploaded = await UploadFileAsync(config, source, file, cancellationToken);
                    if (wasUploaded)
                    {
                        uploadedFiles.Add(file);

                        Emit(onProgress, "rclone_stats", new BackupProgressPayload
                        {
                            Message = $"Uploaded: {file.RelativePath}"
                        });
                    }
                    else
                    {
                        skippedFiles.Add(file);

                        Emit(onProgress, "rclone_stats", new BackupProgressPayload
                        {
                            Message = $"Skipped (identical in remote storage): {file.RelativePath}"
                        });
                    }
                }
                catch (Exception ex)
                {
                    sourceSucceeded = false;
                    overallSuccess = false;
                    Emit(onProgress, "rclone_stats", new BackupProgressPayload
                    {
                        Message = $"Failed: {file.RelativePath} ({ex.Message})"
                    });
                }
            }

            // Skipped files are already present remotely, so record them in the
            // manifest too — otherwise every subsequent run would re-check them.
            var completedFiles = uploadedFiles.Concat(skippedFiles).ToList();
            if (completedFiles.Count > 0)
            {
                using var manifestStore = new NativeBackupManifestStore(scanResult.ManifestPath);
                manifestStore.MarkUploadedMany(
                    completedFiles.Select(file => new ManifestUploadedRow
                    {
                        RelativePath = file.RelativePath,
                        Size = file.Size,
                        ModifiedTimeNs = file.ModifiedTimeNs
                    }),
                    scanResult.RunId);
            }

            var resultText = sourceSucceeded ? "OK" : "Failed";
            history.Add(new BackupHistoryEntry
            {
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                LocalPath = source.LocalPath,
                RemoteDest = source.RemotePath,
                Changed = uploadedFiles.Count,
                Result = resultText,
                Message = sourceSucceeded
                    ? skippedFiles.Count > 0
                        ? $"Backup completed ({skippedFiles.Count} file(s) already up to date in remote storage)"
                        : "Backup completed"
                    : "Some files failed to upload"
            });
            runHistory.Add(history[^1]);

            Emit(onProgress, "end_entry", new BackupProgressPayload
            {
                LocalPath = source.LocalPath,
                RemoteDest = source.RemotePath,
                Result = resultText,
                ChangedFiles = uploadedFiles.Select(file => file.RelativePath).ToArray()
            });
        }

        var status = new BackupStatusSnapshot
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Success = overallSuccess
        };

        await _backupStateStore.SaveAsync(history, status, cancellationToken);
        await _operationHistoryStore.AppendAsync(BuildOperationEntry(config, sources.Count, runHistory, overallSuccess), cancellationToken);

        Emit(onProgress, "finished", new BackupProgressPayload
        {
            Ok = overallSuccess
        }, ok: overallSuccess, message: overallSuccess ? "Backup completed successfully." : "Backup completed with errors.");

        return new BackupExecutionResult
        {
            IsSuccess = overallSuccess,
            ExitCode = overallSuccess ? 0 : 1,
            Message = overallSuccess
                ? "Backup completed successfully."
                : "Backup completed with errors."
        };
    }

    private static OperationHistoryEntry BuildOperationEntry(
        StoredAppConfig config,
        int sourceCount,
        IReadOnlyList<BackupHistoryEntry> runHistory,
        bool overallSuccess)
    {
        var changedTotal = runHistory.Sum(item => item.Changed);
        var failedCount = runHistory.Count(item => !string.Equals(item.Result, "OK", StringComparison.OrdinalIgnoreCase));
        var successCount = runHistory.Count - failedCount;
        var status = overallSuccess ? "success" : (successCount > 0 ? "partial" : "failed");
        var summary = overallSuccess
            ? $"Backup completed for {sourceCount} source(s)."
            : $"Backup completed with issues across {sourceCount} source(s).";

        return new OperationHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Category = "backup",
            Action = "run",
            Status = status,
            Summary = summary,
            Source = $"{sourceCount} source(s)",
            Target = string.IsNullOrWhiteSpace(config.BucketName) ? "No target" : config.BucketName,
            Provider = config.Provider ?? string.Empty,
            TotalCount = sourceCount,
            SuccessCount = successCount,
            FailedCount = failedCount,
            Detail = $"Changed files: {changedTotal}"
        };
    }

    private static async Task<StoredAppConfig?> LoadConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        return await LocalConfigService.LoadFromPathAsync(configPath, cancellationToken);
    }

    private static List<BackupSourceContext> BuildSources(StoredAppConfig config, IReadOnlyList<string> selectedLocalPaths)
    {
        var selected = new HashSet<string>(
            selectedLocalPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeLocalPath),
            StringComparer.OrdinalIgnoreCase);

        var remoteName = string.IsNullOrWhiteSpace(config.RemoteName) ? "RemoteVault" : config.RemoteName.Trim();
        var bucketName = (config.BucketName ?? string.Empty).Trim();
        var sources = new List<BackupSourceContext>();

        if (string.IsNullOrWhiteSpace(bucketName))
        {
            return sources;
        }

        var index = 0;
        foreach (var folder in config.BackupFolders ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder.LocalPath))
            {
                continue;
            }

            var localPath = NormalizeLocalPath(folder.LocalPath);
            if (selected.Count > 0 && !selected.Contains(localPath))
            {
                continue;
            }

            index++;
            var subfolder = NormalizeRemotePrefix(folder.RemoteSubfolder);
            var remotePath = string.IsNullOrWhiteSpace(subfolder)
                ? $"{remoteName}:{bucketName}"
                : $"{remoteName}:{bucketName}/{subfolder}";

            sources.Add(new BackupSourceContext
            {
                Index = index,
                LocalPath = localPath,
                BucketOrContainer = bucketName,
                RemotePrefix = subfolder,
                RemotePath = remotePath
            });
        }

        return sources;
    }

    private static Task<BackupScanResult> ScanAsync(
        BackupSourceContext source,
        IReadOnlyList<string>? excludePatterns,
        Action<BackupProgressEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            // Directory.Exists returns false for unreachable network shares and
            // access-denied paths, not just missing folders — treat all of them
            // as a failed source instead of an empty (successful) scan.
            if (!Directory.Exists(source.LocalPath))
                return new BackupScanResult { Error = $"Source folder not found or not accessible: {source.LocalPath}" };

            try
            {
                var manifestPath = BackupStorageLayout.GetManifestPath(source.BucketOrContainer, source.RemotePrefix);
                using var manifestStore = new NativeBackupManifestStore(manifestPath);
                var runId = manifestStore.BeginRun();

                var changedFiles = new List<BackupFileCandidate>();
                var batch = new List<ManifestScanRow>();
                long totalBytes = 0;
                var fileIndex = 0;

                // DirectoryInfo.EnumerateFiles returns FileInfo with metadata already populated,
                // avoiding a second OS stat call per file that new FileInfo(path) would trigger.
                foreach (var fileInfo in new DirectoryInfo(source.LocalPath).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(source.LocalPath, fileInfo.FullName).Replace("\\", "/");
                    if (IsExcluded(relativePath, excludePatterns))
                        continue;

                    batch.Add(new ManifestScanRow
                    {
                        RelativePath = relativePath,
                        Size = fileInfo.Length,
                        ModifiedTimeNs = fileInfo.LastWriteTimeUtc.Ticks * 100,
                        LastSeenRun = runId
                    });

                    if (batch.Count >= 1000)
                        ProcessManifestBatch(manifestStore, batch, source, changedFiles, ref totalBytes, ref fileIndex, onProgress);
                }

                ProcessManifestBatch(manifestStore, batch, source, changedFiles, ref totalBytes, ref fileIndex, onProgress);
                manifestStore.EndRun(runId);

                return new BackupScanResult
                {
                    ChangedFiles = changedFiles,
                    BytesTotal = totalBytes,
                    ManifestPath = manifestPath,
                    RunId = runId
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A network share can drop mid-enumeration; surface it as a
                // failed source rather than letting the whole run crash.
                return new BackupScanResult { Error = $"Failed to read source folder {source.LocalPath}: {ex.Message}" };
            }
        }, cancellationToken);
    }

    /// <returns>
    /// <see langword="true"/> when the file was uploaded; <see langword="false"/> when an
    /// identical object already exists in remote storage and the upload was skipped.
    /// </returns>
    private static async Task<bool> UploadFileAsync(
        StoredAppConfig config,
        BackupSourceContext source,
        BackupFileCandidate file,
        CancellationToken cancellationToken)
    {
        if (string.Equals(config.Provider, "azureblob", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            return await UploadAzureAsync(config, source, file, cancellationToken);
        }

        return await UploadS3Async(config, source, file, cancellationToken);
    }

    private static async Task<bool> UploadS3Async(
        StoredAppConfig config,
        BackupSourceContext source,
        BackupFileCandidate file,
        CancellationToken cancellationToken)
    {
        using var client = NativeStorageSupport.CreateS3Client(config);

        string? localMd5 = null;
        try
        {
            var head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = source.BucketOrContainer,
                Key = file.RemoteKey
            }, cancellationToken);

            if (head.ContentLength == file.Size)
            {
                localMd5 = await ComputeMd5Async(file.FullPath, cancellationToken);

                var storedHash = head.Metadata[S3HashMetadataKey];
                if (!string.IsNullOrEmpty(storedHash) &&
                    string.Equals(storedHash, localMd5, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Objects uploaded before hash stamping existed: a single-part
                // S3 ETag is the MD5 of the content (multipart ETags contain '-').
                var etag = head.ETag?.Trim('"');
                if (!string.IsNullOrEmpty(etag) && !etag.Contains('-') &&
                    string.Equals(etag, localMd5, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }

        localMd5 ??= await ComputeMd5Async(file.FullPath, cancellationToken);

        using var utility = new TransferUtility(client);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = source.BucketOrContainer,
            Key = file.RemoteKey,
            FilePath = file.FullPath
        };
        request.Metadata.Add(S3HashMetadataKey, localMd5);
        await utility.UploadAsync(request, cancellationToken);
        return true;
    }

    private static async Task<bool> UploadAzureAsync(
        StoredAppConfig config,
        BackupSourceContext source,
        BackupFileCandidate file,
        CancellationToken cancellationToken)
    {
        var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, source.BucketOrContainer);
        var blobClient = containerClient.GetBlobClient(file.RemoteKey);

        string? localMd5 = null;
        try
        {
            BlobProperties properties = (await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
            if (properties.ContentLength == file.Size)
            {
                localMd5 = await ComputeMd5Async(file.FullPath, cancellationToken);

                if (properties.Metadata.TryGetValue(AzureHashMetadataKey, out var storedHash) &&
                    string.Equals(storedHash, localMd5, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (properties.ContentHash is { Length: > 0 } &&
                    string.Equals(Convert.ToHexString(properties.ContentHash), localMd5, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
        }

        localMd5 ??= await ComputeMd5Async(file.FullPath, cancellationToken);

        await using var stream = File.OpenRead(file.FullPath);
        await blobClient.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = GetContentType(file.FullPath)
            },
            Metadata = new Dictionary<string, string>
            {
                [AzureHashMetadataKey] = localMd5
            }
        }, cancellationToken);
        return true;
    }

    private static async Task<string> ComputeMd5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await MD5.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static void ProcessManifestBatch(
        NativeBackupManifestStore manifestStore,
        List<ManifestScanRow> batch,
        BackupSourceContext source,
        List<BackupFileCandidate> changedFiles,
        ref long totalBytes,
        ref int fileIndex,
        Action<BackupProgressEvent>? onProgress)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var uploadedState = manifestStore.GetManyUploaded(batch.Select(row => row.RelativePath).ToArray());

        foreach (var row in batch)
        {
            var shouldUpload = !uploadedState.TryGetValue(row.RelativePath, out var previous) ||
                               previous.Size != row.Size ||
                               previous.ModifiedTimeNs != row.ModifiedTimeNs;

            if (!shouldUpload)
            {
                continue;
            }

            fileIndex++;
            totalBytes += row.Size;

            changedFiles.Add(new BackupFileCandidate
            {
                FullPath = Path.Combine(source.LocalPath, row.RelativePath.Replace("/", Path.DirectorySeparatorChar.ToString())),
                RelativePath = row.RelativePath,
                RemoteKey = BuildRemoteKey(source.RemotePrefix, row.RelativePath),
                Size = row.Size,
                ModifiedTimeNs = row.ModifiedTimeNs
            });

            if (fileIndex % 250 == 0)
            {
                Emit(onProgress, "file", new BackupProgressPayload
                {
                    Phase = "scan",
                    LocalPath = source.LocalPath,
                    RemoteDest = source.RemotePath,
                    RelPath = row.RelativePath,
                    Index = fileIndex,
                    FilesTotal = fileIndex,
                    BytesDone = totalBytes,
                    BytesTotal = totalBytes
                });
            }
        }

        manifestStore.UpsertScanMany(batch);
        batch.Clear();
    }

    private static bool IsExcluded(string relativePath, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0)
        {
            return false;
        }

        var normalized = relativePath.Replace("\\", "/");
        return patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Any(pattern => GlobMatch(normalized, pattern.Trim()));
    }

    private static bool GlobMatch(string value, string pattern)
    {
        var normalizedPattern = pattern.Replace("\\", "/");
        var regexPattern = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", ".") + "$";

        return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeLocalPath(string value)
    {
        return Path.GetFullPath((value ?? string.Empty).Trim());
    }

    private static string NormalizeRemotePrefix(string value)
    {
        return (value ?? string.Empty).Trim().Replace("\\", "/").Trim('/');
    }

    private static string BuildRemoteKey(string remotePrefix, string relativePath)
    {
        var normalizedRelative = relativePath.Replace("\\", "/").Trim('/');
        return string.IsNullOrWhiteSpace(remotePrefix)
            ? normalizedRelative
            : $"{remotePrefix}/{normalizedRelative}";
    }

    private static string GetContentType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private static void Emit(
        Action<BackupProgressEvent>? callback,
        string eventName,
        BackupProgressPayload? payload = null,
        bool? ok = null,
        string? message = null)
    {
        callback?.Invoke(new BackupProgressEvent
        {
            EventName = eventName,
            Payload = payload ?? new BackupProgressPayload(),
            Ok = ok,
            Message = message
        });
    }

    private sealed class BackupSourceContext
    {
        public int Index { get; init; }

        public string LocalPath { get; init; } = string.Empty;

        public string BucketOrContainer { get; init; } = string.Empty;

        public string RemotePrefix { get; init; } = string.Empty;

        public string RemotePath { get; init; } = string.Empty;
    }

    private sealed class BackupScanResult
    {
        public List<BackupFileCandidate> ChangedFiles { get; init; } = [];

        public long BytesTotal { get; init; }

        public string ManifestPath { get; init; } = string.Empty;

        public int RunId { get; init; }

        public string? Error { get; init; }
    }

    private sealed class BackupFileCandidate
    {
        public string FullPath { get; init; } = string.Empty;

        public string RelativePath { get; init; } = string.Empty;

        public string RemoteKey { get; init; } = string.Empty;

        public long Size { get; init; }

        public long ModifiedTimeNs { get; init; }
    }
}
