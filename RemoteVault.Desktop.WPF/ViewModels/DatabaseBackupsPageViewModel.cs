using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.ViewModels;

public partial class DatabaseBackupsPageViewModel : ObservableObject
{
    private readonly IAppConfigStore _localConfigService;
    private readonly IDatabaseBackupService _databaseBackupService;
    private readonly AppLaunchOptions _launchOptions;
    private readonly SynchronizationContext? _uiContext;
    private readonly bool _hasUsableConfiguration;
    private bool _editingUnlocked;

    [ObservableProperty] private DatabaseBackupProfileItem? _selectedProfile;
    [ObservableProperty] private string _pageTitle = "Database backup";
    [ObservableProperty] private string _pageSubtitle = "Set up the databases to back up and run full or differential backups.";
    [ObservableProperty] private string _statusMessage = "Databases";
    [ObservableProperty] private string _footerMessage = "Loading...";
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isStorageLocked;

    public ObservableCollection<DatabaseBackupProfileItem> Profiles { get; } = new();
    // Saved profiles open locked so nothing changes by accident; the Edit button unlocks them in place.
    private bool IsUnlocked => !_hasUsableConfiguration || !IsStorageLocked || _launchOptions.IsDatabaseConfigMode || _editingUnlocked;
    public bool CanEditProfiles => !IsBusy && IsUnlocked;
    public Visibility UnlockButtonVisibility => IsUnlocked ? Visibility.Collapsed : Visibility.Visible;

    [RelayCommand]
    private void UnlockEditing()
    {
        _editingUnlocked = true;
        OnPropertyChanged(nameof(CanEditProfiles));
        OnPropertyChanged(nameof(UnlockButtonVisibility));
        FooterMessage = "Editing is on. Select Save to keep your changes.";
    }
    public bool CanRunBackups => !IsBusy;
    public string ConfigPath => _localConfigService.ConfigPath;

    public DatabaseBackupsPageViewModel(IAppConfigStore localConfigService, IDatabaseBackupService databaseBackupService, AppLaunchOptions launchOptions)
    {
        _localConfigService = localConfigService;
        _databaseBackupService = databaseBackupService;
        _launchOptions = launchOptions;
        _uiContext = SynchronizationContext.Current;
        _hasUsableConfiguration = localConfigService.HasUsableConfigurationFile();
    }

    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEditProfiles)); OnPropertyChanged(nameof(CanRunBackups)); }
    partial void OnIsStorageLockedChanged(bool value) { OnPropertyChanged(nameof(CanEditProfiles)); OnPropertyChanged(nameof(UnlockButtonVisibility)); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoaded) return;
        await ReloadAsync(cancellationToken);
        IsLoaded = true;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var result = await _databaseBackupService.LoadProfilesAsync(ConfigPath, cancellationToken);
            if (!result.IsSuccess) { Profiles.Clear(); StatusMessage = "Could not load the databases"; FooterMessage = result.Message; IsStorageLocked = false; return; }
            ApplyProfiles(result);
        }
        finally { IsBusy = false; }
    }

    public async Task<DatabaseBackupSaveResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEditProfiles) return new DatabaseBackupSaveResult { IsSuccess = false, ExitCode = -1, Message = "Select Edit databases before saving." };
        IsBusy = true;
        try
        {
            var result = await _databaseBackupService.SaveProfilesAsync(ConfigPath, Profiles.Select(p => p.ToPayload()).ToList(), cancellationToken);
            FooterMessage = result.Message;
            if (result.IsSuccess) { IsStorageLocked = result.IsLocked; StatusMessage = "Databases"; }
            return result;
        }
        finally { IsBusy = false; }
    }

    public async Task<DatabaseBackupRunResult> RunNowAsync(string backupType, IReadOnlyList<string> selectedProfileIds, Action<DatabaseBackupWorkerEvent>? onProgress = null, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var result = await _databaseBackupService.RunNowAsync(
                new DatabaseBackupRunRequest { ConfigPath = ConfigPath, BackupType = backupType, SelectedProfileIds = selectedProfileIds.ToList(), Profiles = Profiles.Select(p => p.ToPayload()).ToList() },
                onProgress, cancellationToken);
            FooterMessage = result.Message;
            StatusMessage = result.IsSuccess ? "Databases" : "Backup finished with errors";
            return result;
        }
        finally { IsBusy = false; }
    }

    public void UpsertProfile(DatabaseBackupProfileItem profile)
    {
        var existing = Profiles.FirstOrDefault(i => string.Equals(i.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase));
        if (existing is null) { Profiles.Add(profile); SelectedProfile = profile; FooterMessage = "Profile added."; }
        else { var index = Profiles.IndexOf(existing); Profiles[index] = profile; SelectedProfile = profile; FooterMessage = "Profile updated."; }
        StatusMessage = "Databases";
    }

    public void RemoveProfiles(IEnumerable<DatabaseBackupProfileItem> profilesToRemove)
    {
        var ids = profilesToRemove.Select(i => i.ProfileId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return;
        for (var i = Profiles.Count - 1; i >= 0; i--) { if (ids.Contains(Profiles[i].ProfileId)) Profiles.RemoveAt(i); }
        SelectedProfile = Profiles.FirstOrDefault();
        FooterMessage = ids.Count == 1 ? "Profile removed." : $"{ids.Count} profiles removed.";
        StatusMessage = "Databases";
    }

    private void ApplyProfiles(DatabaseBackupLoadResult result)
    {
        DispatchToUi(() =>
        {
            Profiles.Clear();
            foreach (var payload in result.Profiles) Profiles.Add(DatabaseBackupProfileItem.FromPayload(payload));
            SelectedProfile = Profiles.FirstOrDefault();
            IsStorageLocked = result.IsLocked;
            StatusMessage = "Databases";
            FooterMessage = BuildLoadFooterMessage(result.Message, result.IsLocked);
        });
    }

    private string BuildLoadFooterMessage(string workerMessage, bool isLocked)
    {
        if (isLocked && !IsUnlocked) return "Databases are locked so nothing changes by accident. Select Edit databases to make changes.";
        if (!_hasUsableConfiguration) return "Storage is not set up yet. You can add databases now and finish Settings afterwards.";
        return string.IsNullOrWhiteSpace(workerMessage) ? "Profiles loaded." : workerMessage;
    }

    private void DispatchToUi(Action action)
    {
        if (_uiContext is null) { action(); return; }
        _uiContext.Post(_ => action(), null);
    }
}

public sealed partial class DatabaseBackupProfileItem : ObservableObject
{
    [ObservableProperty] private string _profileId = Guid.NewGuid().ToString();
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _databaseType = "mssql";
    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private int _port = 1433;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _instance = string.Empty;
    [ObservableProperty] private bool _includeAllDatabases;
    [ObservableProperty] private string _databaseListText = string.Empty;
    [ObservableProperty] private int _keepFullChains = 4;
    [ObservableProperty] private bool _diffEnabled;

    public string DatabaseTypeDisplay => DatabaseType.ToUpperInvariant();
    public string DiffModeDisplay => SupportsDiff ? "Enabled" : "-";
    public string KeepFullChainsDisplay => KeepFullChains.ToString();
    public string DatabaseCountDisplay => ParseDatabases(DatabaseListText).Count.ToString();
    public bool SupportsDiff => string.Equals(DatabaseType, "mssql", StringComparison.OrdinalIgnoreCase) && DiffEnabled;

    partial void OnDatabaseTypeChanged(string value) { OnPropertyChanged(nameof(DatabaseTypeDisplay)); OnPropertyChanged(nameof(DiffModeDisplay)); OnPropertyChanged(nameof(SupportsDiff)); }
    partial void OnDiffEnabledChanged(bool value) { OnPropertyChanged(nameof(DiffModeDisplay)); OnPropertyChanged(nameof(SupportsDiff)); }
    partial void OnKeepFullChainsChanged(int value) { OnPropertyChanged(nameof(KeepFullChainsDisplay)); }
    partial void OnDatabaseListTextChanged(string value) { OnPropertyChanged(nameof(DatabaseCountDisplay)); }

    public DatabaseBackupProfilePayload ToPayload() => new()
    {
        ProfileId = ProfileId, Name = ProfileName, DbType = DatabaseType, Host = Host, Port = Port,
        Username = Username, Password = Password, Instance = Instance, IncludeAllDatabases = IncludeAllDatabases,
        Databases = ParseDatabases(DatabaseListText).ToList(), KeepFullChains = KeepFullChains, DiffEnabled = DiffEnabled
    };

    public DatabaseBackupProfileItem Clone() => FromPayload(ToPayload());

    public static DatabaseBackupProfileItem FromPayload(DatabaseBackupProfilePayload payload) => new()
    {
        ProfileId = string.IsNullOrWhiteSpace(payload.ProfileId) ? Guid.NewGuid().ToString() : payload.ProfileId,
        ProfileName = payload.Name, DatabaseType = string.IsNullOrWhiteSpace(payload.DbType) ? "mssql" : payload.DbType,
        Host = string.IsNullOrWhiteSpace(payload.Host) ? "localhost" : payload.Host,
        Port = payload.Port <= 0 ? DefaultPortFor(payload.DbType) : payload.Port,
        Username = payload.Username, Password = payload.Password, Instance = payload.Instance,
        IncludeAllDatabases = payload.IncludeAllDatabases,
        DatabaseListText = string.Join(Environment.NewLine, payload.Databases ?? new List<string>()),
        KeepFullChains = payload.KeepFullChains <= 0 ? 4 : payload.KeepFullChains, DiffEnabled = payload.DiffEnabled
    };

    public static IReadOnlyList<string> ParseDatabases(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static int DefaultPortFor(string? dbType) => (dbType ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "mysql" => 3306, "postgres" => 5432, _ => 1433
    };
}
