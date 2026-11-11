namespace RemoteVault.Contracts.Commands;

public sealed class AppSummaryRequest
{
    public bool IncludeActivityPreview { get; init; } = true;
}
