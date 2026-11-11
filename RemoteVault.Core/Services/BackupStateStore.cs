using System.Text.Json;

namespace RemoteVault.Core.Services;

public sealed class BackupStateStore : IBackupStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public async Task<BackupStateSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupStorageLayout.RootDirectory);

        var history = new List<BackupHistoryEntry>();
        if (File.Exists(BackupStorageLayout.HistoryPath))
        {
            var historyJson = await File.ReadAllTextAsync(BackupStorageLayout.HistoryPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(historyJson))
            {
                try
                {
                    history = JsonSerializer.Deserialize<List<BackupHistoryEntry>>(historyJson, JsonOptions) ?? [];
                }
                catch (JsonException)
                {
                    history = [];
                }
            }
        }

        BackupStatusSnapshot? status = null;
        if (File.Exists(BackupStorageLayout.StatusPath))
        {
            var statusJson = await File.ReadAllTextAsync(BackupStorageLayout.StatusPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(statusJson))
            {
                try
                {
                    status = JsonSerializer.Deserialize<BackupStatusSnapshot>(statusJson, JsonOptions);
                }
                catch (JsonException)
                {
                    status = null;
                }
            }
        }

        return new BackupStateSnapshot
        {
            History = history,
            Status = status
        };
    }

    public async Task SaveAsync(
        IReadOnlyList<BackupHistoryEntry> history,
        BackupStatusSnapshot status,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BackupStorageLayout.RootDirectory);

        var trimmedHistory = history.TakeLast(30).ToArray();

        var historyTempPath = BackupStorageLayout.HistoryPath + ".tmp";
        await using (var historyStream = File.Create(historyTempPath))
        {
            await JsonSerializer.SerializeAsync(historyStream, trimmedHistory, JsonOptions, cancellationToken);
        }

        File.Move(historyTempPath, BackupStorageLayout.HistoryPath, overwrite: true);

        var statusTempPath = BackupStorageLayout.StatusPath + ".tmp";
        await using (var statusStream = File.Create(statusTempPath))
        {
            await JsonSerializer.SerializeAsync(statusStream, status, JsonOptions, cancellationToken);
        }

        File.Move(statusTempPath, BackupStorageLayout.StatusPath, overwrite: true);
    }
}
