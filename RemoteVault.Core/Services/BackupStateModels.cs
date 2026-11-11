namespace RemoteVault.Core.Services;

public sealed class BackupHistoryEntry
{
    public string Time { get; init; } = string.Empty;

    public string LocalPath { get; init; } = string.Empty;

    public string RemoteDest { get; init; } = string.Empty;

    public int Changed { get; init; }

    public string Result { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

public sealed class BackupStatusSnapshot
{
    public string Time { get; init; } = string.Empty;

    public bool Success { get; init; }
}

public sealed class BackupStateSnapshot
{
    public IReadOnlyList<BackupHistoryEntry> History { get; init; } = Array.Empty<BackupHistoryEntry>();

    public BackupStatusSnapshot? Status { get; init; }
}
