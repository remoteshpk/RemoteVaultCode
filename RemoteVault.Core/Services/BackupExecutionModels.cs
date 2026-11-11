using System.Text.Json.Serialization;

namespace RemoteVault.Core.Services;

public sealed class BackupExecutionRequest
{
    [JsonPropertyName("configPath")]
    public string ConfigPath { get; init; } = string.Empty;

    [JsonPropertyName("selectedLocalPaths")]
    public IReadOnlyList<string> SelectedLocalPaths { get; init; } = Array.Empty<string>();
}

public sealed class BackupProgressEnvelope
{
    [JsonPropertyName("event")]
    public string EventName { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public BackupProgressPayload? Payload { get; init; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class BackupProgressPayload
{
    public bool? Ok { get; init; }

    public string? LocalPath { get; init; }

    public string? RemoteDest { get; init; }

    public string? Result { get; init; }

    public string? Message { get; init; }

    public string? RelPath { get; init; }

    public string? Phase { get; init; }

    public string? Line { get; init; }

    public int? Index { get; init; }

    public int? TotalSources { get; init; }

    public int? FilesTotal { get; init; }

    public long? BytesTotal { get; init; }

    public long? BytesDone { get; init; }

    public IReadOnlyList<string>? ChangedFiles { get; init; }
}

public sealed class BackupProgressEvent
{
    public string EventName { get; init; } = string.Empty;

    public BackupProgressPayload Payload { get; init; } = new();

    public bool? Ok { get; init; }

    public string? Message { get; init; }
}

public sealed class BackupExecutionResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public int ExitCode { get; init; }
}
