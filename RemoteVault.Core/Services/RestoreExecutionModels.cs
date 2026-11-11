using System.Text.Json.Serialization;

namespace RemoteVault.Core.Services;

public sealed class RestoreOperationRequest
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("configPath")]
    public string ConfigPath { get; init; } = string.Empty;

    [JsonPropertyName("tasks")]
    public IReadOnlyList<RestoreTaskRequest>? Tasks { get; init; }

    [JsonPropertyName("parentRemotePath")]
    public string? ParentRemotePath { get; init; }

    [JsonPropertyName("filename")]
    public string? FileName { get; init; }

    [JsonPropertyName("destinationRoot")]
    public string? DestinationRoot { get; init; }

    [JsonPropertyName("rootRemote")]
    public string? RootRemote { get; init; }

    [JsonPropertyName("versionName")]
    public string? VersionName { get; init; }

    [JsonPropertyName("versionId")]
    public string? VersionId { get; init; }

    [JsonPropertyName("dateTime")]
    public string? DateTime { get; init; }

    [JsonPropertyName("isCurrent")]
    public bool? IsCurrent { get; init; }

    [JsonPropertyName("cutoffTimestamp")]
    public string? CutoffTimestamp { get; init; }

    [JsonPropertyName("folderRemotes")]
    public IReadOnlyList<string>? FolderRemotes { get; init; }

    [JsonPropertyName("fileItems")]
    public IReadOnlyList<RestoreTimestampFileItem>? FileItems { get; init; }

    [JsonPropertyName("baseEntry")]
    public RestoreBaseEntry? BaseEntry { get; init; }
}

public sealed class RestoreTaskRequest
{
    [JsonPropertyName("remoteItemPath")]
    public string RemoteItemPath { get; init; } = string.Empty;

    [JsonPropertyName("localTarget")]
    public string LocalTarget { get; init; } = string.Empty;

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; init; }
}

public sealed class RestoreTimestampFileItem
{
    [JsonPropertyName("parent_remote")]
    public string ParentRemote { get; init; } = string.Empty;

    [JsonPropertyName("filename")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("rel_relpath")]
    public string RelativePath { get; init; } = string.Empty;
}

public sealed class RestoreBaseEntry
{
    [JsonPropertyName("Display")]
    public string Display { get; init; } = string.Empty;

    [JsonPropertyName("LocalPath")]
    public string LocalPath { get; init; } = string.Empty;

    [JsonPropertyName("SubFolder")]
    public string SubFolder { get; init; } = string.Empty;
}

public sealed class RestoreWorkerEnvelope
{
    [JsonPropertyName("event")]
    public string EventName { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public RestoreWorkerPayload? Payload { get; init; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("success")]
    public int? Success { get; init; }

    [JsonPropertyName("failed")]
    public int? Failed { get; init; }

    [JsonPropertyName("total")]
    public int? Total { get; init; }

    [JsonPropertyName("destination")]
    public string? Destination { get; init; }

    [JsonPropertyName("debug")]
    public string? Debug { get; init; }
}

public sealed class RestoreWorkerPayload
{
    public string? Remote { get; init; }

    public string? Local { get; init; }

    public string? Line { get; init; }

    public double? Percent { get; init; }

    public int? Index { get; init; }

    public int? Total { get; init; }

    public IReadOnlyList<RestoreVersionItem>? Items { get; init; }
}

public sealed class RestoreVersionItem
{
    public string Name { get; init; } = string.Empty;

    public string VersionName { get; init; } = string.Empty;

    public string DateTime { get; init; } = string.Empty;

    public int? SizeKb { get; init; }

    public bool IsCurrent { get; init; }

    public string? VersionId { get; init; }

    public string Kind { get; init; } = "s3";
}

public sealed class RestoreWorkerEvent
{
    public string EventName { get; init; } = string.Empty;

    public RestoreWorkerPayload Payload { get; init; } = new();

    public bool? Ok { get; init; }

    public string? Message { get; init; }

    public int? Success { get; init; }

    public int? Failed { get; init; }

    public int? Total { get; init; }

    public string? Destination { get; init; }

    public string? Debug { get; init; }
}

public sealed class RestoreOperationResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public int ExitCode { get; init; }

    public IReadOnlyList<RestoreVersionItem> VersionItems { get; init; } = Array.Empty<RestoreVersionItem>();

    public int SuccessCount { get; init; }

    public int FailedCount { get; init; }

    public int TotalCount { get; init; }

    public string? DestinationPath { get; init; }

    public string? DebugText { get; init; }
}
