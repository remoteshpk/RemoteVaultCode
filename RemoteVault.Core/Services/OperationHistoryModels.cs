namespace RemoteVault.Core.Services;

public sealed class OperationHistoryEntry
{
    public long Sequence { get; init; }

    public string TimestampUtc { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public int TotalCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailedCount { get; init; }

    public string Detail { get; init; } = string.Empty;
}

public sealed class OperationHistorySnapshot
{
    public IReadOnlyList<OperationHistoryEntry> Entries { get; init; } = Array.Empty<OperationHistoryEntry>();

    public bool NeedsRecovery { get; init; }

    public string RecoveryMessage { get; init; } = string.Empty;

    public string RecoveryFilePath { get; init; } = string.Empty;
}
