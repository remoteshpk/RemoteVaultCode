using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.ViewModels;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly IAppConfigStore _localConfigService;
    private readonly IProviderCatalog _providerCatalog;
    private readonly IBackupSchedulingService _backupSchedulingService;
    private readonly IBackupStateStore _backupStateStore;
    private readonly IVersionCleanupService _versionCleanupService;
    private readonly AppLaunchOptions _launchOptions;
    private readonly bool _hasUsableConfiguration;

    public ObservableCollection<ProviderOption> ProviderOptions { get; } = new();
    public ObservableCollection<EndpointOption> EndpointOptions { get; } = new();
    public ObservableCollection<BackupFolderItem> BackupFolders { get; } = new();
    public ObservableCollection<ScheduleFrequencyOption> ScheduleFrequencyOptions { get; } = new();
    public ObservableCollection<ScheduleFrequencyOption> DatabaseScheduleFrequencyOptions { get; } = new();
    public ObservableCollection<ScheduleFrequencyOption> DatabaseDiffScheduleFrequencyOptions { get; } = new();
    public ObservableCollection<WeekdayOption> WeekdayOptions { get; } = new();
    public ObservableCollection<MonthDayOption> MonthDayOptions { get; } = new();

    [ObservableProperty] private ProviderOption? _selectedProvider;
    [ObservableProperty] private EndpointOption? _selectedEndpoint;
    [ObservableProperty] private BackupFolderItem? _selectedBackupFolder;
    [ObservableProperty] private string _statusConfiguration = "Not set up yet";
    [ObservableProperty] private string _statusBackup = "None yet";
    [ObservableProperty] private string _bucketName = string.Empty;
    [ObservableProperty] private string _accessKey = string.Empty;
    [ObservableProperty] private string _secretKey = string.Empty;
    [ObservableProperty] private bool _enableDatabaseBackups;
    [ObservableProperty] private bool _enableDatabaseRestore;
    [ObservableProperty] private string _databaseBackupRootPath = string.Empty;
    [ObservableProperty] private string _mysqldumpPath = string.Empty;
    [ObservableProperty] private string _pgDumpPath = string.Empty;
    [ObservableProperty] private string _sqlcmdPath = string.Empty;
    [ObservableProperty] private bool _enableFileBackupSchedule;
    [ObservableProperty] private ScheduleFrequencyOption? _selectedFileBackupFrequency;
    [ObservableProperty] private string _fileBackupDailyTimeText = "02:00";
    [ObservableProperty] private bool _enableDatabaseFullBackupSchedule;
    [ObservableProperty] private ScheduleFrequencyOption? _selectedDatabaseFullBackupFrequency;
    [ObservableProperty] private string _databaseFullBackupTimeText = "02:00";
    [ObservableProperty] private WeekdayOption? _selectedDatabaseFullBackupWeekday;
    [ObservableProperty] private MonthDayOption? _selectedDatabaseFullBackupMonthDay;
    [ObservableProperty] private bool _enableDatabaseDiffBackupSchedule;
    [ObservableProperty] private ScheduleFrequencyOption? _selectedDatabaseDiffBackupFrequency;
    [ObservableProperty] private string _databaseDiffBackupTimeText = "02:00";
    [ObservableProperty] private WeekdayOption? _selectedDatabaseDiffBackupWeekday;
    [ObservableProperty] private MonthDayOption? _selectedDatabaseDiffBackupMonthDay;
    [ObservableProperty] private string _excludePatterns = "*.tmp; *.log; cache/; node_modules/";
    [ObservableProperty] private string _footerStatus = "Loading...";
    [ObservableProperty] private string _connectionCardTitle = "Remote Connection Settings (S3/Remote)";
    [ObservableProperty] private string _endpointLabel = "Service Endpoint";
    [ObservableProperty] private string _bucketLabel = "Bucket Name";
    [ObservableProperty] private string _accessLabel = "Access Key ID";
    [ObservableProperty] private string _secretLabel = "Secret Access Key";
    [ObservableProperty] private string _cleanupKeepText = NativeVersionCleanupService.MinimumKeep.ToString();
    [ObservableProperty] private string _cleanupStatus = string.Empty;
    [ObservableProperty] private bool _isCleanupRunning;
    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _configFilePath = string.Empty;
    [ObservableProperty] private string _editModeMessage = string.Empty;
    [ObservableProperty] private double _editSectionOpacity = 1.0;

    public bool IsDatabaseRestoreAvailable => EnableDatabaseBackups;
    public bool IsDatabaseBackupPathAvailable => EnableDatabaseBackups;
    public bool IsDatabaseScheduleAvailable => EnableDatabaseBackups;
    public bool IsFileBackupDailySchedule => string.Equals(SelectedFileBackupFrequency?.Id, "daily", StringComparison.OrdinalIgnoreCase);
    public bool IsDatabaseFullBackupWeeklySchedule => string.Equals(SelectedDatabaseFullBackupFrequency?.Id, "weekly", StringComparison.OrdinalIgnoreCase);
    public bool IsDatabaseFullBackupMonthlySchedule => string.Equals(SelectedDatabaseFullBackupFrequency?.Id, "monthly", StringComparison.OrdinalIgnoreCase);
    public bool IsDatabaseDiffBackupWeeklySchedule => string.Equals(SelectedDatabaseDiffBackupFrequency?.Id, "weekly", StringComparison.OrdinalIgnoreCase);
    public bool IsDatabaseDiffBackupMonthlySchedule => string.Equals(SelectedDatabaseDiffBackupFrequency?.Id, "monthly", StringComparison.OrdinalIgnoreCase);

    public Visibility DatabaseFullBackupWeekdayVisibility => IsDatabaseFullBackupWeeklySchedule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DatabaseFullBackupMonthDayVisibility => IsDatabaseFullBackupMonthlySchedule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DatabaseDiffBackupWeekdayVisibility => IsDatabaseDiffBackupWeeklySchedule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DatabaseDiffBackupMonthDayVisibility => IsDatabaseDiffBackupMonthlySchedule ? Visibility.Visible : Visibility.Collapsed;

    public SettingsPageViewModel(
        IAppConfigStore localConfigService,
        IProviderCatalog providerCatalog,
        IBackupSchedulingService backupSchedulingService,
        IBackupStateStore backupStateStore,
        IVersionCleanupService versionCleanupService,
        AppLaunchOptions launchOptions)
    {
        _localConfigService = localConfigService;
        _providerCatalog = providerCatalog;
        _backupSchedulingService = backupSchedulingService;
        _backupStateStore = backupStateStore;
        _versionCleanupService = versionCleanupService;
        _launchOptions = launchOptions;
        _hasUsableConfiguration = localConfigService.HasUsableConfigurationFile();
        SeedProviders(); SeedScheduleFrequencies(); SeedDatabaseScheduleFrequencies(); SeedDatabaseDiffScheduleFrequencies(); SeedWeekdays(); SeedMonthDays();
        SelectedProvider = ProviderOptions.FirstOrDefault();
        SelectedFileBackupFrequency = ScheduleFrequencyOptions.FirstOrDefault(o => o.Id == "daily");
        SelectedDatabaseFullBackupFrequency = DatabaseScheduleFrequencyOptions.FirstOrDefault(o => o.Id == "daily");
        SelectedDatabaseDiffBackupFrequency = DatabaseDiffScheduleFrequencyOptions.FirstOrDefault(o => o.Id == "daily");
        SelectedDatabaseFullBackupWeekday = WeekdayOptions.FirstOrDefault();
        SelectedDatabaseDiffBackupWeekday = WeekdayOptions.FirstOrDefault();
        SelectedDatabaseFullBackupMonthDay = MonthDayOptions.FirstOrDefault();
        SelectedDatabaseDiffBackupMonthDay = MonthDayOptions.FirstOrDefault();
        BucketName = "backup-remote";
        IsEditMode = launchOptions.IsConfigMode || !_hasUsableConfiguration;
        ConfigFilePath = localConfigService.ConfigPath;
        ApplyEditModeState();
    }

    public Visibility EditButtonVisibility => IsEditMode ? Visibility.Collapsed : Visibility.Visible;

    // ---- Storage cleanup: delete old remote versions, keeping the newest N per file ----

    public int CleanupMinimumKeep => NativeVersionCleanupService.MinimumKeep;
    public int CleanupMaximumKeep => NativeVersionCleanupService.MaximumKeep;

    /// <summary>Cleanup deletes data, so it is only available once settings are unlocked and saved.</summary>
    public bool CanRunCleanup => IsEditMode && !IsCleanupRunning && _localConfigService.HasUsableConfigurationFile();

    partial void OnIsCleanupRunningChanged(bool value) => OnPropertyChanged(nameof(CanRunCleanup));

    /// <summary>Parses the "versions to keep" box; false (with a message) when it is not a number in range.</summary>
    public bool TryGetCleanupKeep(out int keep)
    {
        if (int.TryParse((CleanupKeepText ?? string.Empty).Trim(), out keep) &&
            keep >= CleanupMinimumKeep && keep <= CleanupMaximumKeep)
        {
            return true;
        }

        CleanupStatus = $"Enter a number of versions to keep between {CleanupMinimumKeep} and {CleanupMaximumKeep}.";
        return false;
    }

    /// <summary>Counts what a cleanup would delete. Nothing is removed.</summary>
    public async Task<VersionCleanupResult> PreviewCleanupAsync(int keep, CancellationToken cancellationToken = default)
    {
        IsCleanupRunning = true;
        CleanupStatus = "Checking how many old versions can be removed...";
        try
        {
            var result = await Task.Run(() => _versionCleanupService.PreviewAsync(ConfigFilePath, keep, null, cancellationToken), cancellationToken);
            CleanupStatus = result.Message;
            return result;
        }
        finally
        {
            IsCleanupRunning = false;
        }
    }

    /// <summary>Permanently deletes old versions. Call only after the user has confirmed.</summary>
    public async Task<VersionCleanupResult> RunCleanupAsync(int keep, CancellationToken cancellationToken = default)
    {
        IsCleanupRunning = true;
        CleanupStatus = "Deleting old versions...";
        try
        {
            var result = await Task.Run(() => _versionCleanupService.RunAsync(ConfigFilePath, keep, null, cancellationToken), cancellationToken);
            CleanupStatus = result.IsSuccess
                ? $"{result.Message} Freed {FormatSize(result.BytesFreed)}."
                : result.Message;
            return result;
        }
        finally
        {
            IsCleanupRunning = false;
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Settings open locked so nothing changes by accident; this unlocks them in place.</summary>
    [RelayCommand]
    private void UnlockEditing()
    {
        IsEditMode = true;
        FooterStatus = "Editing is on. Changes take effect when you save.";
    }

    partial void OnIsEditModeChanged(bool value) => ApplyEditModeState();

    private void ApplyEditModeState()
    {
        EditModeMessage = !IsEditMode
            ? "Settings are locked so nothing changes by accident."
            : !_hasUsableConfiguration
                ? "Welcome. Enter your storage details and the folders to protect, then save."
                : "Editing is on. Changes take effect when you save.";
        EditSectionOpacity = IsEditMode ? 1.0 : 0.55;
        OnPropertyChanged(nameof(EditButtonVisibility));
        OnPropertyChanged(nameof(CanRunCleanup));
    }

    partial void OnSelectedProviderChanged(ProviderOption? value)
    {
        EndpointOptions.Clear();
        if (value is null) return;
        ConnectionCardTitle = value.ConnectionTitle; EndpointLabel = value.EndpointLabel; BucketLabel = value.BucketLabel; AccessLabel = value.AccessLabel; SecretLabel = value.SecretLabel;
        foreach (var endpoint in value.Endpoints) EndpointOptions.Add(endpoint);
        SelectedEndpoint = EndpointOptions.FirstOrDefault();
    }

    partial void OnSelectedBackupFolderChanged(BackupFolderItem? value) => RemoveSelectedFolderCommand.NotifyCanExecuteChanged();

    /// <summary>Why the database backup folder cannot be used; empty when it is fine or the feature is off.</summary>
    public string DatabaseBackupRootProblem =>
        EnableDatabaseBackups ? DatabaseBackupFolderRules.GetProblem(DatabaseBackupRootPath) ?? string.Empty : string.Empty;

    public Visibility DatabaseBackupRootProblemVisibility =>
        string.IsNullOrEmpty(DatabaseBackupRootProblem) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnDatabaseBackupRootPathChanged(string value) => NotifyDatabaseBackupRootProblem();

    private void NotifyDatabaseBackupRootProblem()
    {
        OnPropertyChanged(nameof(DatabaseBackupRootProblem));
        OnPropertyChanged(nameof(DatabaseBackupRootProblemVisibility));
    }

    partial void OnEnableDatabaseBackupsChanged(bool value)
    {
        NotifyDatabaseBackupRootProblem();
        if (!value) { if (EnableDatabaseRestore) EnableDatabaseRestore = false; if (EnableDatabaseFullBackupSchedule) EnableDatabaseFullBackupSchedule = false; if (EnableDatabaseDiffBackupSchedule) EnableDatabaseDiffBackupSchedule = false; }
        OnPropertyChanged(nameof(IsDatabaseRestoreAvailable)); OnPropertyChanged(nameof(IsDatabaseBackupPathAvailable)); OnPropertyChanged(nameof(IsDatabaseScheduleAvailable));
    }

    partial void OnSelectedFileBackupFrequencyChanged(ScheduleFrequencyOption? value) => OnPropertyChanged(nameof(IsFileBackupDailySchedule));
    partial void OnSelectedDatabaseFullBackupFrequencyChanged(ScheduleFrequencyOption? value) { OnPropertyChanged(nameof(IsDatabaseFullBackupWeeklySchedule)); OnPropertyChanged(nameof(IsDatabaseFullBackupMonthlySchedule)); OnPropertyChanged(nameof(DatabaseFullBackupWeekdayVisibility)); OnPropertyChanged(nameof(DatabaseFullBackupMonthDayVisibility)); }
    partial void OnSelectedDatabaseDiffBackupFrequencyChanged(ScheduleFrequencyOption? value) { OnPropertyChanged(nameof(IsDatabaseDiffBackupWeeklySchedule)); OnPropertyChanged(nameof(IsDatabaseDiffBackupMonthlySchedule)); OnPropertyChanged(nameof(DatabaseDiffBackupWeekdayVisibility)); OnPropertyChanged(nameof(DatabaseDiffBackupMonthDayVisibility)); }

    [RelayCommand]
    private void AddFolder()
    {
        var nextIndex = BackupFolders.Count + 1;
        var item = new BackupFolderItem { LocalPath = $@"C:\Data\Source{nextIndex}", RemoteSubfolder = $"source{nextIndex}" };
        BackupFolders.Add(item); SelectedBackupFolder = item; FooterStatus = "Folder added. Save to apply.";
    }

    public void AddFolderFromSelection(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var normalizedPath = folderPath.Trim();
        var folderName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = "root";
        var existing = BackupFolders.FirstOrDefault(i => string.Equals(i.LocalPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectedBackupFolder = existing; FooterStatus = $"Folder already exists: {normalizedPath}"; return; }
        var newItem = new BackupFolderItem { LocalPath = normalizedPath, RemoteSubfolder = folderName };
        BackupFolders.Add(newItem); SelectedBackupFolder = newItem; FooterStatus = $"Added folder '{normalizedPath}' with remote subfolder '{folderName}'.";
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFolder))]
    private void RemoveSelectedFolder()
    {
        if (SelectedBackupFolder is null) return;
        BackupFolders.Remove(SelectedBackupFolder); SelectedBackupFolder = BackupFolders.FirstOrDefault(); FooterStatus = "Folder removed. Save to apply.";
    }

    private bool CanRemoveFolder() => SelectedBackupFolder is not null;

    [RelayCommand]
    private void TestConnection() => FooterStatus = $"Connection test target: {SelectedProvider?.DisplayName ?? "Unknown"} / {SelectedEndpoint?.DisplayName ?? "No endpoint"}";

    [RelayCommand]
    private void SaveConfiguration() => FooterStatus = "Use the asynchronous save flow from the Settings page.";

    private void SeedProviders() { ProviderOptions.Clear(); foreach (var p in _providerCatalog.GetProviders()) ProviderOptions.Add(p); }
    private void SeedScheduleFrequencies() { ScheduleFrequencyOptions.Clear(); ScheduleFrequencyOptions.Add(new("daily", "Daily")); ScheduleFrequencyOptions.Add(new("2", "Every 2 hours")); ScheduleFrequencyOptions.Add(new("4", "Every 4 hours")); ScheduleFrequencyOptions.Add(new("6", "Every 6 hours")); ScheduleFrequencyOptions.Add(new("12", "Every 12 hours")); ScheduleFrequencyOptions.Add(new("24", "Every 24 hours")); }
    private void SeedDatabaseScheduleFrequencies() { DatabaseScheduleFrequencyOptions.Clear(); DatabaseScheduleFrequencyOptions.Add(new("daily", "Daily")); DatabaseScheduleFrequencyOptions.Add(new("weekly", "Weekly")); DatabaseScheduleFrequencyOptions.Add(new("monthly", "Monthly")); }
    private void SeedDatabaseDiffScheduleFrequencies() { DatabaseDiffScheduleFrequencyOptions.Clear(); DatabaseDiffScheduleFrequencyOptions.Add(new("daily", "Daily")); DatabaseDiffScheduleFrequencyOptions.Add(new("1", "Hourly")); DatabaseDiffScheduleFrequencyOptions.Add(new("2", "Every 2 hours")); DatabaseDiffScheduleFrequencyOptions.Add(new("4", "Every 4 hours")); DatabaseDiffScheduleFrequencyOptions.Add(new("6", "Every 6 hours")); DatabaseDiffScheduleFrequencyOptions.Add(new("8", "Every 8 hours")); }
    private void SeedWeekdays() { WeekdayOptions.Clear(); foreach (var (id, name) in new[] { ("monday","Monday"),("tuesday","Tuesday"),("wednesday","Wednesday"),("thursday","Thursday"),("friday","Friday"),("saturday","Saturday"),("sunday","Sunday") }) WeekdayOptions.Add(new(id, name)); }
    private void SeedMonthDays() { MonthDayOptions.Clear(); for (var d = 1; d <= 31; d++) MonthDayOptions.Add(new(d)); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoaded) return;
        var stored = await _localConfigService.LoadAsync(cancellationToken);
        if (stored is not null) { ApplyStoredConfig(stored); StatusConfiguration = "Set up"; FooterStatus = "Settings loaded."; }
        else { FooterStatus = IsEditMode ? "Fill in the fields above, then save." : "Select Edit settings to make changes."; StatusConfiguration = "Not set up yet"; }
        var backupState = await _backupStateStore.LoadAsync(cancellationToken);
        if (backupState.Status is not null && !string.IsNullOrWhiteSpace(backupState.Status.Time))
        {
            var outcome = backupState.Status.Success ? "Success" : "Failed";
            StatusBackup = $"{backupState.Status.Time} ({outcome})";
        }
        else { StatusBackup = "None yet"; }
        IsLoaded = true;
    }

    public async Task SaveToDiskAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEditMode) { FooterStatus = "Select Edit settings before saving."; return; }
        if (SelectedProvider is null) { FooterStatus = "Select a provider before saving."; return; }
        if (string.IsNullOrWhiteSpace(BucketName) || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey) || string.IsNullOrWhiteSpace(SelectedEndpoint?.Value)) { FooterStatus = "Provider, endpoint, bucket, access key, and secret key are required."; return; }
        if (BackupFolders.Count == 0) { FooterStatus = "Add at least one folder before saving."; return; }
        if (!string.IsNullOrEmpty(DatabaseBackupRootProblem)) { FooterStatus = $"Not saved. {DatabaseBackupRootProblem}"; return; }

        var config = new StoredAppConfig
        {
            AppName = "RemoteVault", Version = "ModernWPF", Created = DateTime.Now.ToString("O"),
            Provider = SelectedProvider.Id, RemoteName = "RemoteVault", BucketName = BucketName.Trim(),
            Endpoint = (SelectedEndpoint?.Value ?? string.Empty).Trim(), AccessKey = AccessKey.Trim(), SecretKey = SecretKey.Trim(),
            EnableDatabaseBackups = EnableDatabaseBackups, EnableDatabaseRestore = EnableDatabaseRestore, DatabaseBackupRootPath = DatabaseBackupRootPath.Trim(),
            MysqldumpPath = MysqldumpPath.Trim(), PgDumpPath = PgDumpPath.Trim(), SqlcmdPath = SqlcmdPath.Trim(),
            FileBackupSchedule = BuildSchedule(EnableFileBackupSchedule, SelectedFileBackupFrequency, FileBackupDailyTimeText),
            DatabaseBackupSchedule = BuildLegacyDatabaseSchedule(),
            DatabaseFullBackupSchedule = BuildDatabaseSchedule(EnableDatabaseFullBackupSchedule && EnableDatabaseBackups, SelectedDatabaseFullBackupFrequency, DatabaseFullBackupTimeText, SelectedDatabaseFullBackupWeekday, SelectedDatabaseFullBackupMonthDay, "full"),
            DatabaseDiffBackupSchedule = BuildDatabaseSchedule(EnableDatabaseDiffBackupSchedule && EnableDatabaseBackups, SelectedDatabaseDiffBackupFrequency, DatabaseDiffBackupTimeText, SelectedDatabaseDiffBackupWeekday, SelectedDatabaseDiffBackupMonthDay, "diff"),
            BackupFolders = BackupFolders.Where(f => !string.IsNullOrWhiteSpace(f.LocalPath) && !string.IsNullOrWhiteSpace(f.RemoteSubfolder)).Select(f => new StoredBackupFolder { LocalPath = f.LocalPath.Trim(), RemoteSubfolder = f.RemoteSubfolder.Trim() }).ToList(),
            ExcludePatterns = ParseExcludePatterns()
        };

        await _localConfigService.SaveAsync(config, cancellationToken);
        var syncResult = await _backupSchedulingService.SyncAsync(config, ConfigFilePath, cancellationToken);
        StatusConfiguration = "Saved"; OnPropertyChanged(nameof(CanRunCleanup));
        FooterStatus = syncResult.IsSuccess ? $"Settings saved. {syncResult.Message}" : $"Settings saved, but {syncResult.Message}";
    }

    public async Task SaveDatabaseFeaturePreferenceAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(DatabaseBackupRootProblem)) { FooterStatus = $"Not saved. {DatabaseBackupRootProblem}"; return; }
        var existing = await _localConfigService.LoadAsync(cancellationToken);
        var config = existing is null
            ? new StoredAppConfig { EnableDatabaseBackups = EnableDatabaseBackups, EnableDatabaseRestore = EnableDatabaseRestore, DatabaseBackupRootPath = DatabaseBackupRootPath.Trim(), MysqldumpPath = MysqldumpPath.Trim(), PgDumpPath = PgDumpPath.Trim(), SqlcmdPath = SqlcmdPath.Trim() }
            : new StoredAppConfig
            {
                AppName = existing.AppName, Version = existing.Version, Created = existing.Created, Provider = existing.Provider, RemoteName = existing.RemoteName,
                BucketName = existing.BucketName, Endpoint = existing.Endpoint, AccessKey = existing.AccessKey, SecretKey = existing.SecretKey,
                EnableDatabaseBackups = EnableDatabaseBackups, EnableDatabaseRestore = EnableDatabaseRestore, DatabaseBackupRootPath = DatabaseBackupRootPath.Trim(),
                MysqldumpPath = MysqldumpPath.Trim(), PgDumpPath = PgDumpPath.Trim(), SqlcmdPath = SqlcmdPath.Trim(),
                FileBackupSchedule = existing.FileBackupSchedule, DatabaseBackupSchedule = BuildLegacyDatabaseSchedule(),
                DatabaseFullBackupSchedule = BuildDatabaseSchedule(EnableDatabaseFullBackupSchedule && EnableDatabaseBackups, SelectedDatabaseFullBackupFrequency, DatabaseFullBackupTimeText, SelectedDatabaseFullBackupWeekday, SelectedDatabaseFullBackupMonthDay, "full"),
                DatabaseDiffBackupSchedule = BuildDatabaseSchedule(EnableDatabaseDiffBackupSchedule && EnableDatabaseBackups, SelectedDatabaseDiffBackupFrequency, DatabaseDiffBackupTimeText, SelectedDatabaseDiffBackupWeekday, SelectedDatabaseDiffBackupMonthDay, "diff"),
                BackupFolders = existing.BackupFolders, ExcludePatterns = existing.ExcludePatterns
            };
        await _localConfigService.SaveAsync(config, cancellationToken);
        var syncResult = await _backupSchedulingService.SyncAsync(config, ConfigFilePath, cancellationToken);
        FooterStatus = syncResult.IsSuccess ? $"Database feature preference saved. {syncResult.Message}" : $"Database feature preference saved, but {syncResult.Message}";
    }

    private void ApplyStoredConfig(StoredAppConfig stored)
    {
        BucketName = stored.BucketName ?? string.Empty; AccessKey = stored.AccessKey ?? string.Empty; SecretKey = stored.SecretKey ?? string.Empty;
        EnableDatabaseBackups = stored.EnableDatabaseBackups; EnableDatabaseRestore = stored.EnableDatabaseRestore; DatabaseBackupRootPath = stored.DatabaseBackupRootPath ?? string.Empty;
        MysqldumpPath = stored.MysqldumpPath ?? string.Empty; PgDumpPath = stored.PgDumpPath ?? string.Empty; SqlcmdPath = stored.SqlcmdPath ?? string.Empty;
        EnableFileBackupSchedule = stored.FileBackupSchedule.Enabled;
        SelectedFileBackupFrequency = ResolveScheduleFrequency(stored.FileBackupSchedule, ScheduleFrequencyOptions);
        FileBackupDailyTimeText = stored.FileBackupSchedule.DailyTime ?? "02:00";
        var fullSchedule = ResolveDatabaseFullSchedule(stored); var diffSchedule = ResolveDatabaseDiffSchedule(stored);
        EnableDatabaseFullBackupSchedule = fullSchedule.Enabled;
        SelectedDatabaseFullBackupFrequency = ResolveScheduleFrequency(fullSchedule, DatabaseScheduleFrequencyOptions);
        DatabaseFullBackupTimeText = fullSchedule.DailyTime ?? "02:00";
        SelectedDatabaseFullBackupWeekday = ResolveWeekday(fullSchedule.Weekday); SelectedDatabaseFullBackupMonthDay = ResolveMonthDay(fullSchedule.DayOfMonth);
        EnableDatabaseDiffBackupSchedule = diffSchedule.Enabled;
        SelectedDatabaseDiffBackupFrequency = ResolveScheduleFrequency(diffSchedule, DatabaseDiffScheduleFrequencyOptions);
        DatabaseDiffBackupTimeText = diffSchedule.DailyTime ?? "02:00";
        SelectedDatabaseDiffBackupWeekday = ResolveWeekday(diffSchedule.Weekday); SelectedDatabaseDiffBackupMonthDay = ResolveMonthDay(diffSchedule.DayOfMonth);
        ExcludePatterns = string.Join("; ", stored.ExcludePatterns ?? new List<string>());
        var provider = ProviderOptions.FirstOrDefault(o => string.Equals(o.Id, stored.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is not null) SelectedProvider = provider;
        var endpoint = EndpointOptions.FirstOrDefault(o => string.Equals(o.Value, stored.Endpoint, StringComparison.OrdinalIgnoreCase));
        if (endpoint is not null) SelectedEndpoint = endpoint;
        BackupFolders.Clear();
        foreach (var folder in stored.BackupFolders ?? new List<StoredBackupFolder>()) BackupFolders.Add(new BackupFolderItem { LocalPath = folder.LocalPath, RemoteSubfolder = folder.RemoteSubfolder });
        SelectedBackupFolder = BackupFolders.FirstOrDefault();
    }

    private List<string> ParseExcludePatterns() =>
        string.IsNullOrWhiteSpace(ExcludePatterns) ? new List<string>() :
        ExcludePatterns.Replace(",", ";").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    private static StoredBackupSchedule BuildSchedule(bool enabled, ScheduleFrequencyOption? option, string timeText)
    {
        var id = option?.Id ?? "daily"; var isDaily = string.Equals(id, "daily", StringComparison.OrdinalIgnoreCase);
        var intervalHours = int.TryParse(id, out var h) ? h : 6;
        return new StoredBackupSchedule { Enabled = enabled, Frequency = isDaily ? "daily" : "hourly", DailyTime = timeText, IntervalHours = Math.Clamp(intervalHours, 1, 24), BackupType = "full" };
    }

    private static StoredBackupSchedule BuildDatabaseSchedule(bool enabled, ScheduleFrequencyOption? frequencyOption, string timeText, WeekdayOption? weekdayOption, MonthDayOption? monthDayOption, string backupType)
    {
        var frequency = frequencyOption?.Id ?? "daily"; var isHourly = int.TryParse(frequency, out var intervalHours);
        return new StoredBackupSchedule { Enabled = enabled, Frequency = isHourly ? "hourly" : frequency, DailyTime = timeText, IntervalHours = isHourly ? Math.Clamp(intervalHours, 1, 24) : 0, BackupType = backupType, Weekday = weekdayOption?.Id ?? "monday", DayOfMonth = monthDayOption?.Value ?? 1 };
    }

    private StoredBackupSchedule BuildLegacyDatabaseSchedule() => BuildDatabaseSchedule(EnableDatabaseFullBackupSchedule && EnableDatabaseBackups, SelectedDatabaseFullBackupFrequency, DatabaseFullBackupTimeText, SelectedDatabaseFullBackupWeekday, SelectedDatabaseFullBackupMonthDay, "full");

    private static string ResolveScheduleOptionId(StoredBackupSchedule? schedule) =>
        string.Equals(schedule?.Frequency, "hourly", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(schedule?.IntervalHours ?? 1, 1, 24).ToString()
            : (schedule?.Frequency ?? "daily").Trim().ToLowerInvariant();

    private static ScheduleFrequencyOption ResolveScheduleFrequency(StoredBackupSchedule? schedule, ObservableCollection<ScheduleFrequencyOption> options)
    {
        var key = ResolveScheduleOptionId(schedule);
        return options.FirstOrDefault(o => o.Id == key) ?? options.First(o => o.Id == "daily");
    }

    private WeekdayOption ResolveWeekday(string? weekday)
    {
        var key = string.IsNullOrWhiteSpace(weekday) ? "monday" : weekday.Trim().ToLowerInvariant();
        return WeekdayOptions.FirstOrDefault(o => o.Id == key) ?? WeekdayOptions.First();
    }

    private MonthDayOption ResolveMonthDay(int day) => MonthDayOptions.First(o => o.Value == Math.Clamp(day, 1, 31));

    private static StoredBackupSchedule ResolveDatabaseFullSchedule(StoredAppConfig stored) =>
        HasExplicitDatabaseSchedule(stored.DatabaseFullBackupSchedule) ? stored.DatabaseFullBackupSchedule : stored.DatabaseBackupSchedule;

    private static StoredBackupSchedule ResolveDatabaseDiffSchedule(StoredAppConfig stored) => stored.DatabaseDiffBackupSchedule;

    private static bool HasExplicitDatabaseSchedule(StoredBackupSchedule schedule) =>
        schedule.Enabled || !string.Equals(schedule.Frequency ?? "daily", "daily", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(schedule.DailyTime ?? "02:00", "02:00", StringComparison.OrdinalIgnoreCase) ||
        schedule.DayOfMonth != 1 || !string.Equals(schedule.Weekday ?? "monday", "monday", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(schedule.BackupType, "diff", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class BackupFolderItem : ObservableObject
{
    [ObservableProperty] private string _localPath = string.Empty;
    [ObservableProperty] private string _remoteSubfolder = string.Empty;
}

public sealed class ScheduleFrequencyOption(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
}

public sealed class WeekdayOption(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
}

public sealed class MonthDayOption(int value)
{
    public int Value { get; } = value;
    public string DisplayName { get; } = value.ToString();
}
