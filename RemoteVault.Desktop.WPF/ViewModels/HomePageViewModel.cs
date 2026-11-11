using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.ViewModels;

public partial class HomePageViewModel : ObservableObject
{
    private readonly IAppStateService _appStateService;
    private readonly IOperationHistoryStore _operationHistoryStore;
    private readonly IAppConfigStore _configStore;

    private static readonly Brush ActivitySuccessBrush = new SolidColorBrush(Color.FromArgb(255, 20, 120, 92));
    private static readonly Brush ActivityWarningBrush = new SolidColorBrush(Color.FromArgb(255, 201, 122, 0));
    private static readonly Brush ActivityFailureBrush = new SolidColorBrush(Color.FromArgb(255, 175, 55, 55));
    private static readonly Brush ActivityNeutralBrush = new SolidColorBrush(Color.FromArgb(255, 92, 103, 115));

    public ObservableCollection<HomeActivityItem> RecentActivity { get; } = new();

    [ObservableProperty] private string _greetingTitle = "Welcome back";
    [ObservableProperty] private string _greetingSubtitle = "Here is how your backups are doing.";
    [ObservableProperty] private string _motdTitle = "Tip";
    [ObservableProperty] private string _motdMessage = "A backup is only proven once you have restored from it. Try a test restore now and then.";
    [ObservableProperty] private string _operationsPulseTitle = "Latest activity";
    [ObservableProperty] private string _operationsPulseValue = "Checking...";
    [ObservableProperty] private string _operationsPulseDetail = "Loading the latest activity.";
    [ObservableProperty] private string _configurationTitle = "Configuration";
    [ObservableProperty] private string _configurationValue = "Checking...";
    [ObservableProperty] private string _configurationDetail = "Loading...";
    [ObservableProperty] private string _backupTitle = "Last backup";
    [ObservableProperty] private string _backupValue = "Checking...";
    [ObservableProperty] private string _backupDetail = "No backup summary available yet.";
    [ObservableProperty] private string _restoreTitle = "Last restore";
    [ObservableProperty] private string _restoreValue = "Checking...";
    [ObservableProperty] private string _restoreDetail = "No restore activity recorded yet.";
    [ObservableProperty] private string _databaseTitle = "Database backup";
    [ObservableProperty] private string _databaseValue = "Checking...";
    [ObservableProperty] private string _databaseDetail = "No database backup activity recorded yet.";
    [ObservableProperty] private string _storageTitle = "Storage";
    [ObservableProperty] private string _storageValue = "Checking...";
    [ObservableProperty] private string _storageDetail = "No provider summary available yet.";
    [ObservableProperty] private string _nextBackupTitle = "Next backup";
    [ObservableProperty] private string _nextBackupValue = "Checking...";
    [ObservableProperty] private string _nextBackupDetail = "Loading the file backup schedule.";
    [ObservableProperty] private string _historyHeadline = "Recent activity";
    [ObservableProperty] private string _historySubtitle = "The most recent backups and restores on this computer.";
    [ObservableProperty] private string _protectionCoverageTitle = "Protected folders";
    [ObservableProperty] private string _protectionCoverageValue = "Checking...";
    [ObservableProperty] private string _protectionCoverageDetail = "Loading...";
    [ObservableProperty] private string _recentReliabilityTitle = "Success rate";
    [ObservableProperty] private string _recentReliabilityValue = "Checking...";
    [ObservableProperty] private string _recentReliabilityDetail = "Loading...";
    [ObservableProperty] private string _recoveryReadinessTitle = "Restore tested";
    [ObservableProperty] private string _recoveryReadinessValue = "Checking...";
    [ObservableProperty] private string _recoveryReadinessDetail = "Loading...";
    [ObservableProperty] private string _heroTitle = "Checking your backups...";
    [ObservableProperty] private string _heroDetail = string.Empty;
    [ObservableProperty] private string _heroGlyph = "";
    [ObservableProperty] private string _heroActionText = "Back up now";
    [ObservableProperty] private bool _isConfigured;
    [ObservableProperty] private bool _isLoaded;

    public OperationHistorySnapshot? PendingHistoryRecovery { get; private set; }

    public HomePageViewModel(
        IAppStateService appStateService,
        IOperationHistoryStore operationHistoryStore,
        IAppConfigStore configStore)
    {
        _appStateService = appStateService;
        _operationHistoryStore = operationHistoryStore;
        _configStore = configStore;
        SetGreeting();
        SetMotd();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        SetGreeting();
        SetMotd();

        var summary = await _appStateService.LoadSummaryAsync(cancellationToken);
        var config = await _configStore.LoadAsync(cancellationToken);
        var operationHistory = await _operationHistoryStore.LoadAsync(cancellationToken);
        PendingHistoryRecovery = operationHistory.NeedsRecovery ? operationHistory : null;
        var ordered = operationHistory.Entries.OrderByDescending(e => e.Sequence).ToList();

        IsConfigured = config is not null;
        ApplyHero(config, ordered);

        ConfigurationValue = config is null ? "Setup required" : "Configured";
        ConfigurationDetail = config is null
            ? "Open Settings to choose your storage and the folders to protect."
            : $"Health: {summary.HealthState}";

        BackupValue = FindLatestTimestamp(ordered, "backup");
        BackupDetail = DescribeLatest(ordered, "backup", "No backup activity recorded yet.");
        RestoreValue = FindLatestTimestamp(ordered, "restore");
        RestoreDetail = DescribeLatest(ordered, "restore", "No restore activity recorded yet.");
        DatabaseValue = FindLatestTimestamp(ordered, "dbbackup");
        DatabaseDetail = DescribeLatest(ordered, "dbbackup", "No database backup activity recorded yet.");
        StorageValue = config is null || string.IsNullOrWhiteSpace(config.BucketName) ? "Not set up" : config.BucketName;
        StorageDetail = config is null
            ? "Choose your storage in Settings."
            : $"{DescribeProvider(config.Provider)} · {config.Endpoint}";

        var fileScheduleEnabled = config?.FileBackupSchedule?.Enabled == true;
        NextBackupValue = fileScheduleEnabled ? summary.NextBackupDisplay : "Not scheduled";
        NextBackupDetail = fileScheduleEnabled
            ? "Scheduled file backups run through Windows Task Scheduler."
            : "Enable a file backup schedule in Settings to run backups automatically.";

        ProtectionCoverageValue = BuildProtectionCoverageValue(config);
        ProtectionCoverageDetail = config is null
            ? "No folders are protected yet."
            : BuildProtectionCoverageDetail(config);

        RecentReliabilityValue = BuildRecentReliabilityValue(ordered);
        RecentReliabilityDetail = BuildRecentReliabilityDetail(ordered);
        RecoveryReadinessValue = BuildRecoveryReadinessValue(ordered);
        RecoveryReadinessDetail = BuildRecoveryReadinessDetail(ordered);
        OperationsPulseValue = BuildOperationsPulseValue(ordered);
        OperationsPulseDetail = BuildOperationsPulseDetail(ordered, config);

        RecentActivity.Clear();
        foreach (var entry in ordered.Take(6))
        {
            var normalizedStatus = NormalizeStatus(entry);
            var appearance = BuildActivityAppearance(normalizedStatus);
            RecentActivity.Add(new HomeActivityItem
            {
                Timestamp = FormatTimestamp(entry.TimestampUtc),
                Category = FormatCategory(entry.Category),
                Summary = entry.Summary,
                Detail = string.IsNullOrWhiteSpace(entry.Detail) ? $"{entry.Source} -> {entry.Target}" : entry.Detail,
                Status = FormatStatus(normalizedStatus),
                StatusGlyph = appearance.Glyph,
                StatusBackground = appearance.Background
            });
        }

        if (RecentActivity.Count == 0)
        {
            RecentActivity.Add(new HomeActivityItem
            {
                Timestamp = "No entries yet",
                Category = "Activity",
                Summary = "Backups and restores will be listed here once they have run.",
                Detail = "Run a backup to get started.",
                Status = "Ready",
                StatusGlyph = "",
                StatusBackground = ActivityNeutralBrush
            });
        }

        IsLoaded = true;
    }

    private void SetGreeting()
    {
        var hour = DateTime.Now.Hour;
        var period = hour switch { < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        GreetingTitle = $"{period}, {Environment.UserName}";
        GreetingSubtitle = "Here is how your backups are doing.";
    }

    private void SetMotd()
    {
        var notes = new[]
        {
            ("Tip", "A backup is only proven once you have restored from it. Try a test restore now and then."),
            ("Tip", "Run a small test restore after big changes to what you back up."),
            ("Reminder", "If you rotate your storage keys, update them in Settings before the next scheduled backup."),
            ("Did you know", "Older versions of a file can be restored from the Restore page with the Versions button.")
        };
        var index = Math.Abs(DateTime.Today.DayOfYear) % notes.Length;
        MotdTitle = notes[index].Item1;
        MotdMessage = notes[index].Item2;
    }

    private static string DescribeLatest(IReadOnlyList<OperationHistoryEntry> entries, string category, string fallback)
    {
        var entry = entries.FirstOrDefault(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));
        return entry is null ? fallback : $"{FormatStatus(NormalizeStatus(entry))} | {entry.Summary}";
    }

    private static string FindLatestTimestamp(IReadOnlyList<OperationHistoryEntry> entries, string category)
    {
        var entry = entries.FirstOrDefault(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase));
        return entry is null ? "No runs yet" : FormatTimestamp(entry.TimestampUtc);
    }

    /// <summary>"Today, 14:11", "Yesterday, 09:30", "3 Nov, 02:00" or "3 Nov 2025, 02:00".</summary>
    private static string FormatTimestamp(string ts)
    {
        if (!DateTime.TryParse(ts, out var parsed)) return ts;
        var local = parsed.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return $"Today, {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday, {local:HH:mm}";
        return local.Year == today.Year ? local.ToString("d MMM, HH:mm") : local.ToString("d MMM yyyy, HH:mm");
    }

    private static string DescribeProvider(string? providerId) => (providerId ?? string.Empty).ToLowerInvariant() switch
    {
        "aws" => "Amazon S3",
        "azure" => "Azure Blob",
        "wasabi" => "Wasabi",
        "contabo" => "Contabo",
        "hetzner" => "Hetzner",
        "remote" => "Remote",
        "" => "Storage",
        var other => other
    };

    /// <summary>The one-line answer to "are my files safe?" shown at the top of the page.</summary>
    private void ApplyHero(StoredAppConfig? config, IReadOnlyList<OperationHistoryEntry> ordered)
    {
        const string okGlyph = "", warningGlyph = "", infoGlyph = "";

        if (config is null)
        {
            HeroGlyph = infoGlyph;
            HeroTitle = "Let's set up your backups";
            HeroDetail = "Choose where backups are stored and which folders to protect.";
            HeroActionText = "Open Settings";
            return;
        }

        HeroActionText = "Back up now";
        var folders = config.BackupFolders.Count == 1 ? "1 folder" : $"{config.BackupFolders.Count} folders";
        var lastBackup = ordered.FirstOrDefault(e => string.Equals(e.Category, "backup", StringComparison.OrdinalIgnoreCase));

        if (lastBackup is null)
        {
            HeroGlyph = infoGlyph;
            HeroTitle = "No backup has run yet";
            HeroDetail = $"{folders} ready to back up to {config.BucketName}.";
            return;
        }

        var when = FormatTimestamp(lastBackup.TimestampUtc);
        if (!string.Equals(NormalizeStatus(lastBackup), "success", StringComparison.OrdinalIgnoreCase))
        {
            HeroGlyph = warningGlyph;
            HeroTitle = "The last backup had problems";
            HeroDetail = $"{when}: {lastBackup.Summary} Open Backup to see the log and try again.";
            return;
        }

        var ageDays = DateTime.TryParse(lastBackup.TimestampUtc, out var parsed)
            ? (int)(DateTime.Now - parsed.ToLocalTime()).TotalDays
            : 0;
        if (ageDays >= 7 && config.FileBackupSchedule?.Enabled != true)
        {
            HeroGlyph = warningGlyph;
            HeroTitle = $"Your last backup was {ageDays} days ago";
            HeroDetail = $"{folders} protected in {config.BucketName}. Back up now, or set a schedule in Settings.";
            return;
        }

        HeroGlyph = okGlyph;
        HeroTitle = "Your files are backed up";
        HeroDetail = $"Last backup {char.ToLowerInvariant(when[0])}{when[1..]}. {folders} protected in {config.BucketName}.";
    }

    private static string FormatCategory(string category) => category.ToLowerInvariant() switch
    {
        "dbbackup" => "Database",
        "backup" => "Backup",
        "restore" => "Restore",
        _ => "Activity"
    };

    private static string FormatStatus(string status) => status.ToLowerInvariant() switch
    {
        "success" => "OK",
        "partial" => "Warning",
        "failed" => "Failed",
        _ => "Ready"
    };

    private static string BuildProtectionCoverageValue(StoredAppConfig? config)
    {
        if (config?.BackupFolders is null || config.BackupFolders.Count == 0) return "None";
        return config.BackupFolders.Count == 1 ? "1 folder" : $"{config.BackupFolders.Count} folders";
    }

    private static string BuildProtectionCoverageDetail(StoredAppConfig config)
    {
        var provider = string.IsNullOrWhiteSpace(config.Provider) ? "storage" : config.Provider.ToUpperInvariant();
        var bucket = string.IsNullOrWhiteSpace(config.BucketName) ? "no bucket selected" : config.BucketName;
        return $"{config.BackupFolders.Count} folder(s) backed up to {bucket}.";
    }

    private static string BuildRecentReliabilityValue(IReadOnlyList<OperationHistoryEntry> ordered)
    {
        var recent = ordered.Take(10).ToList();
        if (recent.Count == 0) return "No runs yet";
        var successCount = recent.Count(e => string.Equals(NormalizeStatus(e), "success", StringComparison.OrdinalIgnoreCase));
        return $"{(int)Math.Round((double)successCount / recent.Count * 100, MidpointRounding.AwayFromZero)}% success";
    }

    private static string BuildRecentReliabilityDetail(IReadOnlyList<OperationHistoryEntry> ordered)
    {
        var recent = ordered.Take(10).ToList();
        if (recent.Count == 0) return "Nothing has run yet.";
        var successCount = recent.Count(e => string.Equals(NormalizeStatus(e), "success", StringComparison.OrdinalIgnoreCase));
        return $"{successCount} of the last {recent.Count} runs finished without problems.";
    }

    private static string BuildRecoveryReadinessValue(IReadOnlyList<OperationHistoryEntry> ordered)
    {
        var restore = ordered.FirstOrDefault(e => string.Equals(e.Category, "restore", StringComparison.OrdinalIgnoreCase));
        if (restore is null) return "Not yet";
        return string.Equals(NormalizeStatus(restore), "success", StringComparison.OrdinalIgnoreCase) ? "Yes" : "Last restore failed";
    }

    private static string BuildRecoveryReadinessDetail(IReadOnlyList<OperationHistoryEntry> ordered)
    {
        var restore = ordered.FirstOrDefault(e => string.Equals(e.Category, "restore", StringComparison.OrdinalIgnoreCase));
        if (restore is null) return "You have not restored anything yet. A test restore confirms your backups work.";
        return $"{FormatTimestamp(restore.TimestampUtc)} | {FormatStatus(NormalizeStatus(restore))}";
    }

    private static string BuildOperationsPulseValue(IReadOnlyList<OperationHistoryEntry> ordered)
    {
        var latest = ordered.FirstOrDefault();
        return latest is null ? "Quiet" : $"{FormatCategory(latest.Category)} / {FormatStatus(NormalizeStatus(latest))}";
    }

    private static string BuildOperationsPulseDetail(IReadOnlyList<OperationHistoryEntry> ordered, StoredAppConfig? config)
    {
        var latest = ordered.FirstOrDefault();
        if (latest is null)
            return config is null
                ? "Finish setup in Settings to start backing up."
                : "Nothing has run yet. Your first backup will show up here.";
        return $"{FormatTimestamp(latest.TimestampUtc)} | {latest.Summary}";
    }

    private static string NormalizeStatus(OperationHistoryEntry entry)
    {
        var status = (entry.Status ?? string.Empty).Trim().ToLowerInvariant();
        var combined = $"{entry.Summary} {entry.Detail}".ToLowerInvariant();
        if (combined.Contains("failed") || combined.Contains("error") || combined.Contains("issue") || combined.Contains("fatal")) return "failed";
        if (status is "failed" or "partial" or "success") return status;
        if (combined.Contains("warning")) return "partial";
        return "success";
    }

    private static ActivityAppearance BuildActivityAppearance(string status) => status.ToLowerInvariant() switch
    {
        "success" => new ActivityAppearance("", ActivitySuccessBrush),
        "partial" => new ActivityAppearance("", ActivityWarningBrush),
        "failed" => new ActivityAppearance("", ActivityFailureBrush),
        _ => new ActivityAppearance("", ActivityNeutralBrush)
    };
}

public sealed class HomeActivityItem
{
    public string Timestamp { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusGlyph { get; init; } = "";
    public Brush StatusBackground { get; init; } = new SolidColorBrush(Color.FromArgb(255, 92, 103, 115));
}

sealed record ActivityAppearance(string Glyph, Brush Background);
