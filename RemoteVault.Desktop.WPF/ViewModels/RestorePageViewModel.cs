using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.ViewModels;

public partial class RestorePageViewModel : ObservableObject
{
    private readonly IAppConfigStore _localConfigService;
    private readonly IRemoteBrowserService _remoteBrowserService;
    private readonly IDatabaseBackupService _databaseBackupService;
    private readonly SynchronizationContext? _uiContext;

    private StoredAppConfig? _storedConfig;
    private string _currentRemotePath = string.Empty;

    public ObservableCollection<RestoreSourceOption> SourceOptions { get; } = new();
    public ObservableCollection<RestoreBrowserItem> BrowserItems { get; } = new();

    [ObservableProperty] private RestoreSourceOption? _selectedSource;
    [ObservableProperty] private RestoreBrowserItem? _selectedBrowserItem;
    [ObservableProperty] private string _pageTitle = "Restore";
    [ObservableProperty] private string _pageSubtitle = "Pick a backed-up folder, browse its files, and restore what you need.";
    [ObservableProperty] private string _remotePath = string.Empty;
    [ObservableProperty] private string _destinationFolder = string.Empty;
    [ObservableProperty] private bool _restoreToOriginal = false;
    [ObservableProperty] private bool _restoreToCustom = true;
    [ObservableProperty] private string _statusMessage = "Configuration OK.";
    [ObservableProperty] private string _footerMessage = "Choose a backed-up folder to see its files.";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasValidConfiguration;

    public string ConfigPath => _localConfigService.ConfigPath;
    public string CurrentRemotePath => _currentRemotePath;
    public StoredAppConfig? StoredConfig => _storedConfig;

    public RestorePageViewModel(
        IAppConfigStore localConfigService,
        IRemoteBrowserService remoteBrowserService,
        IDatabaseBackupService databaseBackupService)
    {
        _localConfigService = localConfigService;
        _remoteBrowserService = remoteBrowserService;
        _databaseBackupService = databaseBackupService;
        _uiContext = SynchronizationContext.Current;
    }

    partial void OnSelectedSourceChanged(RestoreSourceOption? value) => _ = HandleSourceSelectionChangedAsync(value);

    partial void OnSelectedBrowserItemChanged(RestoreBrowserItem? value)
    {
        ShowVersionsCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoaded) return;
        _storedConfig = await _localConfigService.LoadAsync(cancellationToken);
        if (_storedConfig is null)
        {
            HasValidConfiguration = false;
            StatusMessage = "Settings are missing or invalid. Open Settings to set up RemoteVault first.";
            FooterMessage = "No saved configuration file was found. Complete Settings first.";
            IsLoaded = true;
            return;
        }

        await BuildSourcesAsync(_storedConfig, cancellationToken);
        HasValidConfiguration = SourceOptions.Count > 0 &&
            !string.IsNullOrWhiteSpace(_storedConfig.BucketName) &&
            !string.IsNullOrWhiteSpace(_storedConfig.Endpoint) &&
            !string.IsNullOrWhiteSpace(_storedConfig.AccessKey) &&
            !string.IsNullOrWhiteSpace(_storedConfig.SecretKey);

        FooterMessage = HasValidConfiguration
            ? "Select a backup source to browse its remote contents."
            : "Settings are incomplete. Finish them in Settings before restoring.";
        SelectedSource = SourceOptions.FirstOrDefault();
        IsLoaded = true;
    }

    [RelayCommand(CanExecute = nameof(CanBrowseRemote))]
    private async Task RefreshAsync()
    {
        if (_storedConfig is null || SelectedSource is null) { FooterMessage = "Select a backup source first."; return; }
        await LoadRemoteListingAsync(_currentRemotePath);
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private async Task UpAsync()
    {
        if (SelectedSource is null) return;
        var root = SelectedSource.RemoteRoot.TrimEnd('/');
        var current = string.IsNullOrWhiteSpace(_currentRemotePath) ? root : _currentRemotePath.TrimEnd('/');
        if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return;
        var lastSlash = current.LastIndexOf('/');
        var parent = lastSlash > 0 ? current[..lastSlash] : root;
        if (parent.Length < root.Length) parent = root;
        await LoadRemoteListingAsync(parent);
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSelection))]
    private void RestoreSelected() => FooterMessage = "Restore execution is ready with source mapping, destination mode, and remote browser navigation.";

    [RelayCommand(CanExecute = nameof(CanShowVersions))]
    private void ShowVersions() => FooterMessage = "Version browsing is available from this page.";

    [RelayCommand(CanExecute = nameof(CanBrowseRemote))]
    private void RestoreFolderByTimestamp() => FooterMessage = "Timestamp restore ready.";

    public async Task BrowseIntoSelectedItemAsync()
    {
        if (SelectedBrowserItem is null || !SelectedBrowserItem.IsDirectory) return;
        await LoadRemoteListingAsync($"{_currentRemotePath.TrimEnd('/')}/{SelectedBrowserItem.RelativePath.TrimStart('/')}");
    }

    public void SetDestinationFolder(string folderPath)
    {
        DestinationFolder = folderPath?.Trim() ?? string.Empty;
        FooterMessage = string.IsNullOrWhiteSpace(DestinationFolder) ? "Destination cleared." : $"Destination set to {DestinationFolder}";
    }

    public RestoreBaseEntry? BuildCurrentBaseEntry()
    {
        if (SelectedSource is null) return null;
        return new RestoreBaseEntry { Display = SelectedSource.DisplayName, LocalPath = SelectedSource.LocalPath, SubFolder = SelectedSource.RemoteSubfolder };
    }

    public IReadOnlyList<RestoreTaskRequest> BuildRestoreTasks(IEnumerable<RestoreBrowserItem> items)
    {
        if (SelectedSource is null) return Array.Empty<RestoreTaskRequest>();
        var remoteRootBase = SelectedSource.RemoteRoot.TrimEnd('/');
        var tasks = new List<RestoreTaskRequest>();
        foreach (var item in items)
        {
            var relpath = (item.RelativePath ?? string.Empty).Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(relpath)) continue;
            var remoteItemPath = $"{_currentRemotePath.TrimEnd('/')}/{relpath}".Replace("\\", "/");
            var relToRoot = remoteItemPath.StartsWith(remoteRootBase + "/", StringComparison.OrdinalIgnoreCase)
                ? remoteItemPath[(remoteRootBase.Length + 1)..] : relpath;
            tasks.Add(new RestoreTaskRequest
            {
                RemoteItemPath = remoteItemPath,
                LocalTarget = Path.Combine(DestinationFolder, relToRoot.Replace("/", Path.DirectorySeparatorChar.ToString())),
                IsDirectory = item.IsDirectory
            });
        }
        return tasks;
    }

    public (IReadOnlyList<string> FolderRemotes, IReadOnlyList<RestoreTimestampFileItem> FileItems) BuildTimestampSelection(IEnumerable<RestoreBrowserItem> items)
    {
        var folders = new List<string>();
        var files = new List<RestoreTimestampFileItem>();
        var baseRemote = (_currentRemotePath ?? string.Empty).Trim().TrimEnd('/');
        foreach (var item in items)
        {
            var rel = (item.RelativePath ?? string.Empty).Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(rel)) continue;
            if (item.IsDirectory) { folders.Add($"{baseRemote}/{rel}".TrimEnd('/')); }
            else
            {
                var normalized = rel.Replace("\\", "/").Trim('/');
                var lastSlash = normalized.LastIndexOf('/');
                files.Add(new RestoreTimestampFileItem
                {
                    ParentRemote = lastSlash >= 0 ? $"{baseRemote}/{normalized[..lastSlash]}".TrimEnd('/') : baseRemote,
                    FileName = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized,
                    RelativePath = normalized
                });
            }
        }
        return (folders, files);
    }

    public void SetDestinationMode(bool restoreToOriginal)
    {
        RestoreToOriginal = restoreToOriginal;
        RestoreToCustom = !restoreToOriginal;
        if (restoreToOriginal && SelectedSource is not null)
        {
            DestinationFolder = SelectedSource.LocalPath;
            FooterMessage = "Destination switched to the original local source folder.";
        }
        else if (!restoreToOriginal)
        {
            FooterMessage = "Destination switched to a custom restore path.";
        }
    }

    private bool CanBrowseRemote() => HasValidConfiguration && SelectedSource is not null && !IsBusy;
    private bool CanGoUp() => CanBrowseRemote() && SelectedSource is not null && !string.Equals(_currentRemotePath.TrimEnd('/'), SelectedSource.RemoteRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private bool CanRestoreSelection() => !IsBusy && SelectedSource is not null && SelectedBrowserItem is not null && !string.IsNullOrWhiteSpace(DestinationFolder);
    private bool CanShowVersions() => !IsBusy && SelectedBrowserItem is not null && !SelectedBrowserItem.IsDirectory;

    private async Task BuildSourcesAsync(StoredAppConfig stored, CancellationToken cancellationToken)
    {
        SourceOptions.Clear();
        var remoteName = string.IsNullOrWhiteSpace(stored.RemoteName) ? "RemoteVault" : stored.RemoteName.Trim();
        var bucketName = stored.BucketName.Trim();
        foreach (var folder in stored.BackupFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.LocalPath) || string.IsNullOrWhiteSpace(folder.RemoteSubfolder)) continue;
            var localPath = folder.LocalPath.Trim();
            var remoteSubfolder = folder.RemoteSubfolder.Trim().Replace("\\", "/").Trim('/');
            var folderName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName)) folderName = remoteSubfolder;
            SourceOptions.Add(new RestoreSourceOption
            {
                DisplayName = $"{folderName} ({localPath})",
                LocalPath = localPath,
                RemoteSubfolder = remoteSubfolder,
                RemoteRoot = $"{remoteName}:{bucketName}/{remoteSubfolder}".Replace("\\", "/")
            });
        }
        if (stored.EnableDatabaseRestore)
            await AddDatabaseRestoreSourcesAsync(stored, remoteName, bucketName, cancellationToken);
    }

    private async Task AddDatabaseRestoreSourcesAsync(StoredAppConfig stored, string remoteName, string bucketName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucketName)) return;
        var loadResult = await _databaseBackupService.LoadProfilesAsync(ConfigPath, cancellationToken);
        if (!loadResult.IsSuccess || loadResult.Profiles.Count == 0) return;
        var dbBackupRoot = NativeDatabaseBackupService.ResolveDatabaseBackupRoot(stored, ConfigPath);
        foreach (var profile in loadResult.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name)) continue;
            var safeProfileName = BuildSafeProfileFolderName(profile.Name);
            var localPath = Path.Combine(dbBackupRoot, safeProfileName);
            var remoteSubfolder = $"dbbackup/{safeProfileName}";
            SourceOptions.Add(new RestoreSourceOption
            {
                DisplayName = $"DB Backup - {profile.Name} ({localPath})",
                LocalPath = localPath,
                RemoteSubfolder = remoteSubfolder,
                RemoteRoot = $"{remoteName}:{bucketName}/{remoteSubfolder}".Replace("\\", "/"),
                SourceKind = "dbrestore"
            });
        }
    }

    private static string BuildSafeProfileFolderName(string? profileName)
    {
        var safeName = new string((profileName ?? "profile").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(safeName) ? "profile" : safeName;
    }

    private async Task HandleSourceSelectionChangedAsync(RestoreSourceOption? source)
    {
        if (source is null) { BrowserItems.Clear(); _currentRemotePath = string.Empty; RemotePath = string.Empty; UpCommand.NotifyCanExecuteChanged(); return; }
        if (RestoreToOriginal) DestinationFolder = source.LocalPath;
        await LoadRemoteListingAsync(source.RemoteRoot);
    }

    private async Task LoadRemoteListingAsync(string remotePath)
    {
        if (_storedConfig is null || SelectedSource is null || string.IsNullOrWhiteSpace(remotePath)) return;
        IsBusy = true;
        RefreshCommand.NotifyCanExecuteChanged(); UpCommand.NotifyCanExecuteChanged(); RestoreSelectedCommand.NotifyCanExecuteChanged(); ShowVersionsCommand.NotifyCanExecuteChanged(); RestoreFolderByTimestampCommand.NotifyCanExecuteChanged();
        try
        {
            var entries = await _remoteBrowserService.ListAsync(_storedConfig, remotePath);
            DispatchToUi(() =>
            {
                BrowserItems.Clear();
                foreach (var entry in entries)
                {
                    BrowserItems.Add(new RestoreBrowserItem
                    {
                        Name = entry.Name, RelativePath = entry.RelativePath, Type = entry.IsDirectory ? "Dir" : "File",
                        SizeDisplay = entry.IsDirectory || entry.SizeBytes is null ? string.Empty : Math.Round(entry.SizeBytes.Value / 1024d, 1).ToString("0.0"),
                        Modified = entry.ModifiedDisplay, IsDirectory = entry.IsDirectory
                    });
                }
                _currentRemotePath = remotePath.Trim();
                RemotePath = _currentRemotePath;
                SelectedBrowserItem = BrowserItems.FirstOrDefault();
                var dirCount = BrowserItems.Count(i => i.IsDirectory);
                StatusMessage = $"Loaded {dirCount} folder(s), {BrowserItems.Count - dirCount} file(s) from {_currentRemotePath}.";
                FooterMessage = StatusMessage;
                RefreshCommand.NotifyCanExecuteChanged(); UpCommand.NotifyCanExecuteChanged(); RestoreSelectedCommand.NotifyCanExecuteChanged(); ShowVersionsCommand.NotifyCanExecuteChanged(); RestoreFolderByTimestampCommand.NotifyCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            DispatchToUi(() =>
            {
                BrowserItems.Clear(); StatusMessage = "Could not load the backed-up files."; FooterMessage = ex.Message;
                RefreshCommand.NotifyCanExecuteChanged(); UpCommand.NotifyCanExecuteChanged(); RestoreSelectedCommand.NotifyCanExecuteChanged(); ShowVersionsCommand.NotifyCanExecuteChanged(); RestoreFolderByTimestampCommand.NotifyCanExecuteChanged();
            });
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged(); UpCommand.NotifyCanExecuteChanged(); RestoreSelectedCommand.NotifyCanExecuteChanged(); ShowVersionsCommand.NotifyCanExecuteChanged(); RestoreFolderByTimestampCommand.NotifyCanExecuteChanged();
        }
    }

    private void DispatchToUi(Action action)
    {
        if (_uiContext is null) { action(); return; }
        _uiContext.Post(_ => action(), null);
    }
}

public sealed partial class RestoreSourceOption : ObservableObject
{
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _localPath = string.Empty;
    [ObservableProperty] private string _remoteSubfolder = string.Empty;
    [ObservableProperty] private string _remoteRoot = string.Empty;
    [ObservableProperty] private string _sourceKind = "folder";
}

public sealed partial class RestoreBrowserItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _type = string.Empty;
    [ObservableProperty] private string _sizeDisplay = string.Empty;
    [ObservableProperty] private string _modified = string.Empty;
    [ObservableProperty] private string _relativePath = string.Empty;
    [ObservableProperty] private bool _isDirectory;
}
