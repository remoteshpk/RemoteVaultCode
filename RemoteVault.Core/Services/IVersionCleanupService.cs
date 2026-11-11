namespace RemoteVault.Core.Services;

public interface IVersionCleanupService
{
    /// <summary>
    /// Counts what <see cref="RunAsync"/> would delete, without deleting anything
    /// and without writing to the operation history.
    /// </summary>
    Task<VersionCleanupResult> PreviewAsync(
        string configPath,
        int keepVersions,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes old remote versions, keeping the newest ones per file.</summary>
    Task<VersionCleanupResult> RunAsync(
        string configPath,
        int keepVersions,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default);
}

public sealed class VersionCleanupResult
{
    public bool IsSuccess { get; init; }

    public int ExitCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public int FilesExamined { get; init; }

    /// <summary>Versions deleted, or for a preview the versions that would be deleted.</summary>
    public int VersionsDeleted { get; init; }

    /// <summary>Total size of those versions in bytes.</summary>
    public long BytesFreed { get; init; }

    /// <summary>True when nothing was deleted and the numbers are what a real run would do.</summary>
    public bool IsPreview { get; init; }

    public int FailedDeletions { get; init; }
}
