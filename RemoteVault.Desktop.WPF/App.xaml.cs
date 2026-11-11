using Microsoft.Extensions.DependencyInjection;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.Shell;
using RemoteVault.Desktop.WPF.ViewModels;
using System.Text;
using System.Windows;

namespace RemoteVault.Desktop.WPF;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    public static MainWindow? MainWindowInstance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        EnsureStartupJsonFilesAreUsable();
        Services = ConfigureServices();

        var launchOptions = Services.GetRequiredService<AppLaunchOptions>();

        if (launchOptions.RunScheduledBackup ||
            launchOptions.RunScheduledDatabaseBackup ||
            launchOptions.RunScheduledDatabaseFullBackup ||
            launchOptions.RunScheduledDatabaseDiffBackup)
        {
            var exitCode = Task.Run(() => ExecuteScheduledRunAsync(launchOptions)).GetAwaiter().GetResult();
            Environment.Exit(exitCode);
            return;
        }

        if (launchOptions.RunCleanOldVersions)
        {
            if (launchOptions.CleanOldVersionsKeep is not int keepCount)
            {
                if (Environment.UserInteractive)
                {
                    MessageBox.Show(
                        $"--clean-old-versions requires the number of versions to keep per file ({NativeVersionCleanupService.MinimumKeep}-{NativeVersionCleanupService.MaximumKeep}).\n\nExample: RemoteVault.exe --clean-old-versions 10",
                        "Remote Vault",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                Environment.Exit(-4);
                return;
            }

            var exitCode = Task.Run(() => ExecuteCleanOldVersionsAsync(keepCount)).GetAwaiter().GetResult();
            Environment.Exit(exitCode);
            return;
        }

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindowInstance = mainWindow;
        mainWindow.Show();

        _ = RepairScheduledTaskActionsAsync();
    }

    /// <summary>
    /// Tasks registered by an older install point at the previous executable
    /// name (RemoteVault.Desktop.WPF.exe); re-register them against the
    /// current one. Best-effort: scheduling problems surface on save instead.
    /// </summary>
    private static async Task RepairScheduledTaskActionsAsync()
    {
        try
        {
            var configStore = Services.GetRequiredService<IAppConfigStore>();
            var config = await configStore.LoadAsync();
            if (config is null) return;

            var schedulingService = Services.GetRequiredService<IBackupSchedulingService>();
            await schedulingService.RepairTaskActionsAsync(config, configStore.ConfigPath);
        }
        catch
        {
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var serviceCollection = new ServiceCollection();
        var args = Environment.GetCommandLineArgs().Select(a => a.Trim().ToLowerInvariant()).ToArray();
        var (cleanRequested, cleanKeep) = ParseCleanOldVersions(args);
        var launchOptions = new AppLaunchOptions
        {
            IsConfigMode = args.Any(a => a is "--config" or "/config" or "-config"),
            IsDatabaseConfigMode = args.Any(a => a is "--configbackup" or "/configbackup" or "-configbackup"),
            RunScheduledBackup = args.Any(a => a is "--run-scheduled-backup" or "/run-scheduled-backup" or "-run-scheduled-backup"),
            RunScheduledDatabaseBackup = args.Any(a => a is "--run-scheduled-db-backup" or "/run-scheduled-db-backup" or "-run-scheduled-db-backup"),
            RunScheduledDatabaseFullBackup = args.Any(a => a is "--run-scheduled-db-full-backup" or "/run-scheduled-db-full-backup" or "-run-scheduled-db-full-backup"),
            RunScheduledDatabaseDiffBackup = args.Any(a => a is "--run-scheduled-db-diff-backup" or "/run-scheduled-db-diff-backup" or "-run-scheduled-db-diff-backup"),
            RunCleanOldVersions = cleanRequested,
            CleanOldVersionsKeep = cleanKeep
        };

        serviceCollection.AddSingleton(launchOptions);
        serviceCollection.AddSingleton<IProviderCatalog, StaticProviderCatalog>();
        serviceCollection.AddSingleton<IAppConfigStore, LocalConfigService>();
        serviceCollection.AddSingleton<IBackupStateStore, BackupStateStore>();
        serviceCollection.AddSingleton<IOperationHistoryStore, OperationHistoryStore>();
        serviceCollection.AddSingleton<IConnectionTestService, NativeConnectionTestService>();
        serviceCollection.AddSingleton<IRemoteBrowserService, NativeRemoteBrowserService>();
        serviceCollection.AddSingleton<IVersionCatalogService, NativeVersionCatalogService>();
        serviceCollection.AddSingleton<IBackupExecutionService, NativeBackupExecutionService>();
        serviceCollection.AddSingleton<IVersionCleanupService, NativeVersionCleanupService>();
        serviceCollection.AddSingleton<IRestoreExecutionService, NativeRestoreExecutionService>();
        serviceCollection.AddSingleton<IDatabaseBackupService, NativeDatabaseBackupService>();
        serviceCollection.AddSingleton<IBackupSchedulingService, WindowsTaskSchedulerService>();
        serviceCollection.AddSingleton<IAppStateService, AppStateService>();
        serviceCollection.AddSingleton<ShellViewModel>();
        serviceCollection.AddSingleton<MainWindow>();

        return serviceCollection.BuildServiceProvider();
    }

    private static void EnsureStartupJsonFilesAreUsable()
    {
        MigrateLegacyWritableState();
        EnsureJsonArrayFile(BackupStorageLayout.HistoryPath);
        EnsureJsonObjectFile(BackupStorageLayout.StatusPath);
        EnsureOperationHistoryFile(BackupStorageLayout.OperationHistoryPath);
        EnsureJsonObjectFile(BackupStorageLayout.ConfigPath);
        EnsureJsonProfilesFile(BackupStorageLayout.DbProfilesPath);
    }

    private static void EnsureJsonArrayFile(string path) => EnsureJsonFile(path, "[]");
    private static void EnsureJsonObjectFile(string path) => EnsureJsonFile(path, "{}");
    private static void EnsureJsonProfilesFile(string path) => EnsureJsonFile(path, "{\"profiles\":[]}");

    private static void EnsureOperationHistoryFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(path)) return;
            var content = NormalizeJsonText(File.ReadAllText(path, Encoding.UTF8));
            if (string.IsNullOrWhiteSpace(content)) File.Delete(path);
        }
        catch { }
    }

    private static void EnsureJsonFile(string path, string defaultContent)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(path)) return;
            var content = NormalizeJsonText(File.ReadAllText(path, Encoding.UTF8));
            if (string.IsNullOrWhiteSpace(content)) File.WriteAllText(path, defaultContent, Encoding.UTF8);
        }
        catch { }
    }

    private static void MigrateLegacyWritableState()
    {
        try
        {
            Directory.CreateDirectory(BackupStorageLayout.AppDataRootDirectory);
            foreach (var root in GetCandidateRoots())
            {
                TryMigrateFile(Path.Combine(root, "remotevault.config.json"), BackupStorageLayout.ConfigPath);
                TryMigrateFile(Path.Combine(root, "data", "db_profiles.json"), BackupStorageLayout.DbProfilesPath);
                TryMigrateFile(Path.Combine(root, "data", "backup-state", "RemoteVaultBackupHistory.json"), BackupStorageLayout.HistoryPath);
                TryMigrateFile(Path.Combine(root, "data", "backup-state", "RemoteVaultBackupStatus.json"), BackupStorageLayout.StatusPath);
                TryMigrateFile(Path.Combine(root, "data", "backup-state", "RemoteVaultOperationHistory.json"), BackupStorageLayout.OperationHistoryPath);
                TryMigrateDirectory(Path.Combine(root, "data", "backup-state", "manifests"), BackupStorageLayout.ManifestRoot);
            }
        }
        catch { }
    }

    private static IEnumerable<string> GetCandidateRoots() =>
        new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static void TryMigrateFile(string source, string destination)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(destination)) return;
            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.Copy(source, destination, overwrite: false);
        }
        catch { }
    }

    private static void TryMigrateDirectory(string sourceDir, string destDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(destDir);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(sourceDir, sourceFile);
                var destFile = Path.Combine(destDir, rel);
                if (File.Exists(destFile)) continue;
                var destFileDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrWhiteSpace(destFileDir)) Directory.CreateDirectory(destFileDir);
                File.Copy(sourceFile, destFile, overwrite: false);
            }
        }
        catch { }
    }

    private static string NormalizeJsonText(string? content) =>
        (content ?? string.Empty).Trim('﻿', '​', '\0', ' ', '\t', '\r', '\n');

    private static (bool Requested, int? Keep) ParseCleanOldVersions(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var isFlag = arg is "--clean-old-versions" or "/clean-old-versions" or "-clean-old-versions";
            var separatorIndex = arg.IndexOf('=');
            var isInline = !isFlag && separatorIndex > 0 &&
                           arg[..separatorIndex] is "--clean-old-versions" or "/clean-old-versions" or "-clean-old-versions";

            if (!isFlag && !isInline)
            {
                continue;
            }

            var valueText = isInline
                ? arg[(separatorIndex + 1)..]
                : (i + 1 < args.Length ? args[i + 1] : null);

            return (true, int.TryParse(valueText, out var value) ? value : null);
        }

        return (false, null);
    }

    private static async Task<int> ExecuteCleanOldVersionsAsync(int keepCount)
    {
        var configStore = Services.GetRequiredService<IAppConfigStore>();
        var cleanupService = Services.GetRequiredService<IVersionCleanupService>();
        var result = await cleanupService.RunAsync(configStore.ConfigPath, keepCount);
        return result.ExitCode;
    }

    private static async Task<int> ExecuteScheduledRunAsync(AppLaunchOptions launchOptions)
    {
        var configStore = Services.GetRequiredService<IAppConfigStore>();
        if (launchOptions.RunScheduledBackup)
        {
            var backupService = Services.GetRequiredService<IBackupExecutionService>();
            var result = await backupService.RunAsync(new BackupExecutionRequest { ConfigPath = configStore.ConfigPath });
            return result.ExitCode;
        }

        if (launchOptions.RunScheduledDatabaseBackup ||
            launchOptions.RunScheduledDatabaseFullBackup ||
            launchOptions.RunScheduledDatabaseDiffBackup)
        {
            var databaseBackupService = Services.GetRequiredService<IDatabaseBackupService>();
            var config = await configStore.LoadAsync();
            if (config is null) return 1;

            var profiles = await databaseBackupService.LoadProfilesAsync(configStore.ConfigPath);
            if (!profiles.IsSuccess) return profiles.ExitCode == 0 ? 1 : profiles.ExitCode;

            var fullSchedule = ResolveDatabaseFullSchedule(config);
            var diffSchedule = ResolveDatabaseDiffSchedule(config);
            var backupType = launchOptions.RunScheduledDatabaseDiffBackup
                ? "diff"
                : launchOptions.RunScheduledDatabaseFullBackup
                    ? "full"
                    : string.Equals(diffSchedule.BackupType, "diff", StringComparison.OrdinalIgnoreCase) &&
                      diffSchedule.Enabled && !fullSchedule.Enabled
                        ? "diff"
                        : "full";

            var scheduledProfiles = profiles.Profiles;
            if (string.Equals(backupType, "diff", StringComparison.OrdinalIgnoreCase))
            {
                scheduledProfiles = profiles.Profiles
                    .Where(p => string.Equals(p.DbType, "mssql", StringComparison.OrdinalIgnoreCase) && p.DiffEnabled)
                    .ToList();
                if (scheduledProfiles.Count == 0) return 0;
            }

            var result = await databaseBackupService.RunNowAsync(new DatabaseBackupRunRequest
            {
                ConfigPath = configStore.ConfigPath,
                BackupType = backupType,
                Profiles = scheduledProfiles
            });
            return result.ExitCode;
        }

        return 0;
    }

    private static StoredBackupSchedule ResolveDatabaseFullSchedule(StoredAppConfig config) =>
        HasExplicitSchedule(config.DatabaseFullBackupSchedule)
            ? config.DatabaseFullBackupSchedule
            : config.DatabaseBackupSchedule;

    private static StoredBackupSchedule ResolveDatabaseDiffSchedule(StoredAppConfig config) =>
        config.DatabaseDiffBackupSchedule;

    private static bool HasExplicitSchedule(StoredBackupSchedule schedule) =>
        schedule.Enabled ||
        !string.Equals(schedule.Frequency ?? "daily", "daily", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(schedule.DailyTime ?? "02:00", "02:00", StringComparison.OrdinalIgnoreCase) ||
        schedule.DayOfMonth != 1 ||
        !string.Equals(schedule.Weekday ?? "monday", "monday", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(schedule.BackupType, "diff", StringComparison.OrdinalIgnoreCase);
}
