namespace RemoteVault.Contracts.Dtos;

public sealed class AppSummaryDto
{
    public string HealthState { get; init; } = "Unknown";

    public string LastBackupDisplay { get; init; } = "No backups yet";

    public string NextBackupDisplay { get; init; } = "No schedule configured";

    public string StorageDisplay { get; init; } = "Not connected";
}
