using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.ViewModels;

public partial class BackupPageViewModel : ObservableObject
{
    private readonly IAppConfigStore _localConfigService;
    private readonly IBackupExecutionService _backupExecutionService;
    private readonly IBackupStateStore _backupStateStore;
    private readonly SynchronizationContext? _uiContext;

    public ObservableCollection<BackupSourceStatusItem> BackupSources { get; } = new();

    [ObservableProperty] private BackupSourceStatusItem? _selectedBackupSource;
    [ObservableProperty] private string _pageTitle = "Backup";
    [ObservableProperty] private string _pageSubtitle = "Back up your protected folders now, all of them or only the ones you tick.";
    [ObservableProperty] private string _statusMessage = "Loading protected folders...";
    [ObservableProperty] private string _executionLog = string.Empty;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _hasValidConfiguration;
    [ObservableProperty] private string _configSummary = "Configuration not loaded.";
    [ObservableProperty] private string _footerMessage = "Loading...";
    [ObservableProperty] private bool _isBackupRunning;

    public bool CanTriggerFullBackup => !IsBackupRunning && HasValidConfiguration;
    public bool CanTriggerSelectedBackup => !IsBackupRunning && HasValidConfiguration && (BackupSources.Any(i => i.IsSelected) || SelectedBackupSource is not null);

    public BackupPageViewModel(
        IAppConfigStore localConfigService,
        IBackupExecutionService backupExecutionService,
        IBackupStateStore backupStateStore,
        AppLaunchOptions launchOptions)
    {
        _localConfigService = localConfigService;
        _backupExecutionService = backupExecutionService;
        _backupStateStore = backupStateStore;
        _uiContext = SynchronizationContext.Current;
    }

    partial void OnSelectedBackupSourceChanged(BackupSourceStatusItem? value)
    {
        BackupSelectedSourcesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanTriggerSelectedBackup));
    }

    partial void OnIsBackupRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTriggerFullBackup));
        OnPropertyChanged(nameof(CanTriggerSelectedBackup));
    }

    partial void OnHasValidConfigurationChanged(bool value)
    {
        OnPropertyChanged(nameof(CanTriggerFullBackup));
        OnPropertyChanged(nameof(CanTriggerSelectedBackup));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoaded) return;
        var stored = await _localConfigService.LoadAsync(cancellationToken);
        if (stored is null)
        {
            HasValidConfiguration = false;
            StatusMessage = "Settings are missing or invalid. Open Settings to set up RemoteVault first.";
            FooterMessage = "No local configuration file found.";
            IsLoaded = true;
            return;
        }

        await ApplyStoredConfigAsync(stored, cancellationToken);
        IsLoaded = true;
    }

    [RelayCommand(CanExecute = nameof(CanRunAnyBackup))]
    private async Task RunFullBackupNow()
    {
        if (!HasValidConfiguration)
        {
            AppendLog("Configuration is invalid or missing.");
            FooterMessage = "Backup cannot start because configuration is missing.";
            return;
        }

        await ExecuteBackupAsync(Array.Empty<string>(), "Starting full backup run...");
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectedBackup))]
    private async Task BackupSelectedSources()
    {
        if (!HasValidConfiguration)
        {
            AppendLog("Configuration is invalid or missing.");
            FooterMessage = "Selected backup cannot start because configuration is missing.";
            return;
        }

        var selected = BackupSources.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0 && SelectedBackupSource is not null) selected.Add(SelectedBackupSource);
        if (selected.Count == 0)
        {
            AppendLog("Please select at least one source in the backup table.");
            FooterMessage = "No backup source selected.";
            return;
        }

        await ExecuteBackupAsync(selected.Select(i => i.LocalFolder).ToArray(), $"Starting backup for {selected.Count} selected source(s)...");
    }

    private bool CanRunSelectedBackup() => !IsBackupRunning && (BackupSources.Any(i => i.IsSelected) || SelectedBackupSource is not null);
    private bool CanRunAnyBackup() => !IsBackupRunning;

    private async Task ApplyStoredConfigAsync(StoredAppConfig stored, CancellationToken cancellationToken)
    {
        BackupSources.Clear();
        var remoteName = string.IsNullOrWhiteSpace(stored.RemoteName) ? "RemoteVault" : stored.RemoteName.Trim();
        var bucketName = (stored.BucketName ?? string.Empty).Trim();

        foreach (var folder in stored.BackupFolders ?? new List<StoredBackupFolder>())
        {
            if (string.IsNullOrWhiteSpace(folder.LocalPath) || string.IsNullOrWhiteSpace(folder.RemoteSubfolder)) continue;
            var remotePath = string.IsNullOrWhiteSpace(bucketName)
                ? $"{remoteName}:<bucket-missing>/{folder.RemoteSubfolder.Trim()}"
                : $"{remoteName}:{bucketName}/{folder.RemoteSubfolder.Trim()}";
            BackupSources.Add(new BackupSourceStatusItem
            {
                LocalFolder = Path.GetFullPath(folder.LocalPath.Trim()),
                RemotePath = remotePath,
                Result = "Ready",
                FilesChanged = "-"
            });
        }

        foreach (var item in BackupSources)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BackupSourceStatusItem.IsSelected))
                {
                    BackupSelectedSourcesCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(CanTriggerSelectedBackup));
                }
            };
        }

        SelectedBackupSource = BackupSources.FirstOrDefault();
        HasValidConfiguration =
            !string.IsNullOrWhiteSpace(bucketName) &&
            BackupSources.Count > 0 &&
            !string.IsNullOrWhiteSpace(stored.Endpoint) &&
            !string.IsNullOrWhiteSpace(stored.AccessKey) &&
            !string.IsNullOrWhiteSpace(stored.SecretKey);

        ConfigSummary = HasValidConfiguration
            ? $"{BackupSources.Count} protected folder(s), backed up to bucket {bucketName}"
            : "Settings are incomplete. Finish them in Settings before running a backup.";
        StatusMessage = HasValidConfiguration ? "Protected folders" : "Settings are missing or incomplete. Please review Settings.";
        FooterMessage = HasValidConfiguration ? "Ready." : "Settings are incomplete.";
        ExecutionLog = string.Empty;
        AppendLog(ConfigSummary);
        AppendLog("Ready to run backups.");
        await ApplyPersistedBackupStateAsync(cancellationToken);
        RunFullBackupNowCommand.NotifyCanExecuteChanged();
        BackupSelectedSourcesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanTriggerFullBackup));
        OnPropertyChanged(nameof(CanTriggerSelectedBackup));
    }

    private async Task ApplyPersistedBackupStateAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _backupStateStore.LoadAsync(cancellationToken);
        var latestByPath = snapshot.History
            .GroupBy(e => e.LocalPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in BackupSources)
        {
            if (!latestByPath.TryGetValue(item.LocalFolder, out var history)) continue;
            item.Result = string.IsNullOrWhiteSpace(history.Result) ? item.Result : history.Result;
            item.FilesChanged = history.Changed.ToString();
        }

        if (snapshot.Status is not null)
        {
            FooterMessage = snapshot.Status.Success
                ? $"Last backup completed successfully at {snapshot.Status.Time}."
                : $"Last backup finished with issues at {snapshot.Status.Time}.";
            AppendLog(FooterMessage);
        }
    }

    private void AppendLog(string line)
    {
        ExecutionLog = string.IsNullOrWhiteSpace(ExecutionLog) ? line : ExecutionLog + Environment.NewLine + line;
    }

    private async Task ExecuteBackupAsync(IReadOnlyList<string> selectedLocalPaths, string startMessage)
    {
        IsBackupRunning = true;
        RunFullBackupNowCommand.NotifyCanExecuteChanged();
        BackupSelectedSourcesCommand.NotifyCanExecuteChanged();
        FooterMessage = startMessage;
        AppendLog(startMessage);

        foreach (var source in BackupSources)
        {
            if (selectedLocalPaths.Count == 0 || selectedLocalPaths.Contains(source.LocalFolder, StringComparer.OrdinalIgnoreCase))
            {
                source.Result = "Queued";
                source.FilesChanged = "-";
            }
        }

        try
        {
            var result = await _backupExecutionService.RunAsync(
                new BackupExecutionRequest { ConfigPath = _localConfigService.ConfigPath, SelectedLocalPaths = selectedLocalPaths },
                HandleProgressEvent);
            FooterMessage = result.Message;
            AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            FooterMessage = $"Backup failed to start: {ex.Message}";
            AppendLog(FooterMessage);
        }
        finally
        {
            IsBackupRunning = false;
            RunFullBackupNowCommand.NotifyCanExecuteChanged();
            BackupSelectedSourcesCommand.NotifyCanExecuteChanged();
        }
    }

    private void HandleProgressEvent(BackupProgressEvent progressEvent) => DispatchToUi(() => ApplyProgressEvent(progressEvent));

    private void ApplyProgressEvent(BackupProgressEvent progressEvent)
    {
        var payload = progressEvent.Payload;
        var localPath = (payload.LocalPath ?? string.Empty).Trim();
        var item = BackupSources.FirstOrDefault(s => string.Equals(s.LocalFolder, localPath, StringComparison.OrdinalIgnoreCase));

        switch (progressEvent.EventName)
        {
            case "start_entry":
                if (item is not null) { item.Result = "Running"; item.FilesChanged = (payload.FilesTotal ?? 0).ToString(); }
                AppendLog($"Starting backup of: {localPath} -> {payload.RemoteDest}");
                break;
            case "file":
                if (string.Equals(payload.Phase, "scan", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(payload.RelPath))
                    AppendLog($"Scanning: {payload.RelPath}");
                break;
            case "rclone_stats":
                if (!string.IsNullOrWhiteSpace(payload.Message)) AppendLog(payload.Message);
                else if (!string.IsNullOrWhiteSpace(payload.Line)) AppendLog(payload.Line);
                else if (!string.IsNullOrWhiteSpace(progressEvent.Message)) AppendLog(progressEvent.Message!);
                break;
            case "end_entry":
                var changedCount = payload.ChangedFiles?.Count ?? 0;
                if (item is not null) { item.Result = string.IsNullOrWhiteSpace(payload.Result) ? "Completed" : payload.Result!; item.FilesChanged = changedCount.ToString(); }
                AppendLog(string.IsNullOrWhiteSpace(payload.Message)
                    ? $"Finished: {localPath} ({payload.Result}, {changedCount} changed)"
                    : $"Finished: {localPath} ({payload.Result}, {changedCount} changed) - {payload.Message}");
                break;
            case "finished":
                var succeeded = payload.Ok ?? progressEvent.Ok ?? false;
                StatusMessage = succeeded ? "Protected folders" : "Backup finished with issues";
                AppendLog(succeeded ? "Backup completed successfully." : "Backup completed with errors.");
                break;
            case "completed":
                AppendLog(progressEvent.Ok == true ? "Worker completed successfully." : "Worker reported a failed backup run.");
                break;
            case "fatal":
                StatusMessage = "Backup failed";
                if (!string.IsNullOrWhiteSpace(progressEvent.Message)) AppendLog(progressEvent.Message!);
                break;
        }
    }

    private void DispatchToUi(Action action)
    {
        if (_uiContext is null) { action(); return; }
        _uiContext.Post(_ => action(), null);
    }
}

public sealed partial class BackupSourceStatusItem : ObservableObject
{
    [ObservableProperty] private string _localFolder = string.Empty;
    [ObservableProperty] private string _remotePath = string.Empty;
    [ObservableProperty] private string _result = string.Empty;
    [ObservableProperty] private string _filesChanged = string.Empty;
    [ObservableProperty] private bool _isSelected;
}
