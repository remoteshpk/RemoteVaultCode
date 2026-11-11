namespace RemoteVault.Core.Services;

public sealed class RemoteBrowserEntry
{
    public string Name { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public bool IsDirectory { get; init; }

    public long? SizeBytes { get; init; }

    public string ModifiedDisplay { get; init; } = string.Empty;
}
