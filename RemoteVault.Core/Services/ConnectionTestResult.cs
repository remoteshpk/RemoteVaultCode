namespace RemoteVault.Core.Services;

public sealed class ConnectionTestResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}
