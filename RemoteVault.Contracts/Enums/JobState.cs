namespace RemoteVault.Contracts.Enums;

public enum JobState
{
    Queued,
    Starting,
    Running,
    Cancelling,
    Succeeded,
    Failed,
    CompletedWithWarnings
}
