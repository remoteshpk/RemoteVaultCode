using System.Text.Json;

namespace RemoteVault.Core.Services;

public sealed class OperationHistoryFileException : Exception
{
    public OperationHistoryFileException(string filePath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}

public sealed class OperationHistoryStore : IOperationHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<OperationHistorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupStorageLayout.RootDirectory);

        if (!File.Exists(BackupStorageLayout.OperationHistoryPath))
        {
            return new OperationHistorySnapshot();
        }

        try
        {
            var json = await File.ReadAllTextAsync(BackupStorageLayout.OperationHistoryPath, cancellationToken);
            if (!TryPrepareJson(json, out var normalizedJson, out var recoveryMessage))
            {
                return new OperationHistorySnapshot
                {
                    NeedsRecovery = true,
                    RecoveryMessage = recoveryMessage,
                    RecoveryFilePath = BackupStorageLayout.OperationHistoryPath
                };
            }

            var entries = JsonSerializer.Deserialize<List<OperationHistoryEntry>>(normalizedJson, JsonOptions)
                ?? [];

            var normalized = NormalizeSequences(entries);
            var requiresMigration = entries.Any(entry => entry.Sequence <= 0);

            if (requiresMigration)
            {
                await SaveEntriesAsync(normalized, cancellationToken);
            }

            return new OperationHistorySnapshot
            {
                Entries = normalized
            };
        }
        catch (JsonException ex)
        {
            return new OperationHistorySnapshot
            {
                NeedsRecovery = true,
                RecoveryMessage = $"The recent activity history file is damaged and could not be read. {ex.Message}",
                RecoveryFilePath = BackupStorageLayout.OperationHistoryPath
            };
        }
    }

    public async Task AppendAsync(OperationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken);
        var entries = snapshot.Entries.ToList();
        var nextSequence = entries.Count == 0 ? 1 : entries.Max(item => item.Sequence) + 1;
        entries.Add(CloneWithSequence(entry, nextSequence));

        var trimmed = entries
            .OrderByDescending(item => item.Sequence)
            .Take(200)
            .ToList();

        await SaveEntriesAsync(trimmed, cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupStorageLayout.RootDirectory);
        await SaveEntriesAsync([], cancellationToken);
    }

    private static bool TryPrepareJson(string? json, out string normalizedJson, out string recoveryMessage)
    {
        normalizedJson = string.Empty;
        recoveryMessage = string.Empty;

        if (string.IsNullOrEmpty(json))
        {
            recoveryMessage = "The recent activity history file is empty.";
            return false;
        }

        var span = json.AsSpan();
        var start = 0;
        while (start < span.Length)
        {
            var ch = span[start];
            if (!char.IsWhiteSpace(ch) && ch != '\uFEFF' && ch != '\u200B' && ch != '\0')
            {
                break;
            }

            start++;
        }

        if (start >= span.Length)
        {
            recoveryMessage = "The recent activity history file is empty.";
            return false;
        }

        var first = span[start];
        if (first != '[' && first != '{')
        {
            recoveryMessage = "The recent activity history file is damaged and could not be read.";
            return false;
        }

        normalizedJson = span[start..].ToString();
        return true;
    }

    private static List<OperationHistoryEntry> NormalizeSequences(List<OperationHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        var nextFallbackSequence = entries.Count;
        return entries
            .Select(entry =>
            {
                if (entry.Sequence > 0)
                {
                    return entry;
                }

                return CloneWithSequence(entry, nextFallbackSequence--);
            })
            .OrderByDescending(entry => entry.Sequence)
            .ToList();
    }

    private static OperationHistoryEntry CloneWithSequence(OperationHistoryEntry entry, long sequence)
    {
        return new OperationHistoryEntry
        {
            Sequence = sequence,
            TimestampUtc = entry.TimestampUtc,
            Category = entry.Category,
            Action = entry.Action,
            Status = entry.Status,
            Summary = entry.Summary,
            Source = entry.Source,
            Target = entry.Target,
            Provider = entry.Provider,
            TotalCount = entry.TotalCount,
            SuccessCount = entry.SuccessCount,
            FailedCount = entry.FailedCount,
            Detail = entry.Detail
        };
    }

    private static async Task SaveEntriesAsync(IReadOnlyList<OperationHistoryEntry> entries, CancellationToken cancellationToken)
    {
        var tempPath = BackupStorageLayout.OperationHistoryPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, BackupStorageLayout.OperationHistoryPath, overwrite: true);
    }
}
