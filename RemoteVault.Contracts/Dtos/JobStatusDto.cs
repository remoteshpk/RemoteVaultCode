using RemoteVault.Contracts.Enums;

namespace RemoteVault.Contracts.Dtos;

public sealed class JobStatusDto
{
    public string JobId { get; init; } = string.Empty;

    public string Operation { get; init; } = string.Empty;

    public JobState State { get; init; } = JobState.Queued;

    public int? ProgressPercent { get; init; }

    public string Message { get; init; } = string.Empty;
}
