using RemoteVault.Contracts.Enums;

namespace RemoteVault.Contracts.Events;

public sealed class JobProgressEvent
{
    public string JobId { get; init; } = string.Empty;

    public string Operation { get; init; } = string.Empty;

    public JobState State { get; init; } = JobState.Queued;

    public int? ProgressPercent { get; init; }

    public string Detail { get; init; } = string.Empty;
}
