using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.Shell;
using RemoteVault.Desktop.WPF.ViewModels;

namespace RemoteVault.Desktop.WPF.Views;

public partial class SettingsPage : Page
{
    public SettingsPageViewModel ViewModel { get; }
    private readonly IConnectionTestService _connectionTestService;
    private bool _isAdjustingDatabaseBackupToggle;
    private bool _secretKeyInitialized;

    public SettingsPage()
    {
        _connectionTestService = App.Services.GetRequiredService<IConnectionTestService>();
        ViewModel = new SettingsPageViewModel(
            App.Services.GetRequiredService<IAppConfigStore>(),
            App.Services.GetRequiredService<IProviderCatalog>(),
            App.Services.GetRequiredService<IBackupSchedulingService>(),
            App.Services.GetRequiredService<IBackupStateStore>(),
            App.Services.GetRequiredService<IVersionCleanupService>(),
            App.Services.GetRequiredService<AppLaunchOptions>());
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnPageLoaded;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SecretKey) && !_secretKeyInitialized)
        {
            SecretKeyBox.Password = ViewModel.SecretKey;
            _secretKeyInitialized = true;
        }
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadAsync();
            SecretKeyBox.Password = ViewModel.SecretKey;
            _secretKeyInitialized = true;
        }
        catch (Exception ex)
        {
            ViewModel.FooterStatus = $"Failed to load: {ex.Message}";
        }
    }

    private void OnSecretKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_secretKeyInitialized)
            ViewModel.SecretKey = SecretKeyBox.Password;
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a local folder to back up",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                ViewModel.AddFolderFromSelection(dialog.SelectedPath);
        }
        catch (Exception ex)
        {
            ViewModel.FooterStatus = $"Failed to add folder: {ex.Message}";
        }
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        var providerId = ViewModel.SelectedProvider?.Id ?? string.Empty;
        var endpointValue = ViewModel.SelectedEndpoint?.Value ?? string.Empty;
        ViewModel.FooterStatus = "Testing remote connection...";

        try
        {
            var result = await _connectionTestService.TestAsync(
                providerId, ViewModel.BucketName, endpointValue,
                ViewModel.AccessKey, ViewModel.SecretKey);

            ViewModel.FooterStatus = result.Detail;
            if (result.IsSuccess) ViewModel.StatusConfiguration = result.Message;
        }
        catch (Exception ex)
        {
            ViewModel.FooterStatus = $"Connection test failed: {ex.Message}";
        }
    }

    private async void OnSaveConfigurationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SaveToDiskAsync();
            if (App.MainWindowInstance is MainWindow mainWindow)
                await mainWindow.RefreshDatabaseNavigationVisibilityAsync();
        }
        catch (Exception ex)
        {
            ViewModel.FooterStatus = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Storage cleanup: count first, show exactly what will be deleted, and only delete
    /// after the user confirms. Cancel is the default answer.
    /// </summary>
    private async void OnCleanUpOldVersionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!ViewModel.TryGetCleanupKeep(out var keep)) return;

            var preview = await ViewModel.PreviewCleanupAsync(keep);
            if (!preview.IsSuccess || preview.VersionsDeleted == 0) return;

            var answer = MessageBox.Show(
                $"{preview.VersionsDeleted:N0} old version(s) of {preview.FilesExamined:N0} file(s) checked will be permanently deleted from \"{ViewModel.BucketName}\", freeing about {SettingsPageViewModel.FormatSize(preview.BytesFreed)}.\n\n" +
                $"The newest {keep} versions of every file are kept. Files with {keep} or fewer versions are not touched.\n\n" +
                "This cannot be undone. Delete the old versions?",
                "Clean up old versions",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                ViewModel.CleanupStatus = "Cleanup cancelled. Nothing was deleted.";
                return;
            }

            await ViewModel.RunCleanupAsync(keep);
        }
        catch (Exception ex)
        {
            ViewModel.CleanupStatus = $"Cleanup failed: {ex.Message}";
        }
    }

    private async void OnDatabaseBackupToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_isAdjustingDatabaseBackupToggle || !ViewModel.IsLoaded) return;
        try
        {
            // Turning the feature on needs a usable folder: not empty and not inside a user profile.
            if (sender is CheckBox cb && cb.IsChecked == true &&
                DatabaseBackupFolderRules.GetProblem(ViewModel.DatabaseBackupRootPath) is not null)
            {
                var chosen = PickDatabaseBackupFolder();
                if (chosen is null)
                {
                    _isAdjustingDatabaseBackupToggle = true;
                    cb.IsChecked = false;
                    _isAdjustingDatabaseBackupToggle = false;
                    ViewModel.FooterStatus = $"Database backup stays off until you choose a folder on C: or another drive, for example {DatabaseBackupFolderRules.SuggestedFolder}.";
                    return;
                }

                ViewModel.DatabaseBackupRootPath = chosen;
                ViewModel.FooterStatus = $"Database backup files will be written to {chosen}";
            }

            if (sender is CheckBox toggle && App.MainWindowInstance is MainWindow mainWindow)
                mainWindow.SetDatabaseNavigationVisibility(toggle.IsChecked == true);

            await ViewModel.SaveDatabaseFeaturePreferenceAsync();
        }
        catch (Exception ex) { ViewModel.FooterStatus = $"Save failed: {ex.Message}"; }
    }

    private async void OnDatabaseRestoreToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsLoaded) return;
        try { await ViewModel.SaveDatabaseFeaturePreferenceAsync(); }
        catch (Exception ex) { ViewModel.FooterStatus = $"Save failed: {ex.Message}"; }
    }

    private async void OnBrowseDatabaseBackupRootClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = PickDatabaseBackupFolder();
            if (path is null) return;
            ViewModel.DatabaseBackupRootPath = path;
            await ViewModel.SaveDatabaseFeaturePreferenceAsync();
        }
        catch (Exception ex) { ViewModel.FooterStatus = $"Save failed: {ex.Message}"; }
    }

    private void OnBrowseSqlcmdPathClick(object sender, RoutedEventArgs e)
    {
        try { PickExecutableAndAssign(path => ViewModel.SqlcmdPath = path, "sqlcmd.exe"); }
        catch (Exception ex) { ViewModel.FooterStatus = $"Failed to select sqlcmd.exe: {ex.Message}"; }
    }

    private void OnBrowseMysqldumpPathClick(object sender, RoutedEventArgs e)
    {
        try { PickExecutableAndAssign(path => ViewModel.MysqldumpPath = path, "mysqldump.exe"); }
        catch (Exception ex) { ViewModel.FooterStatus = $"Failed to select mysqldump.exe: {ex.Message}"; }
    }

    private void OnBrowsePgDumpPathClick(object sender, RoutedEventArgs e)
    {
        try { PickExecutableAndAssign(path => ViewModel.PgDumpPath = path, "pg_dump.exe"); }
        catch (Exception ex) { ViewModel.FooterStatus = $"Failed to select pg_dump.exe: {ex.Message}"; }
    }

    /// <summary>
    /// Folder picker for database backup files. Opens on the system drive and refuses folders
    /// inside a user profile (SQL Server cannot write there), asking again until the user picks
    /// a usable folder or cancels.
    /// </summary>
    private string? PickDatabaseBackupFolder()
    {
        var startIn = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        while (true)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose a folder on C: or another drive (not in Documents, Downloads or Desktop)",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = startIn
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;

            var problem = DatabaseBackupFolderRules.GetProblem(dialog.SelectedPath);
            if (problem is null) return dialog.SelectedPath;

            MessageBox.Show(
                $"{problem}\n\nYou chose:\n{dialog.SelectedPath}",
                "Choose a different folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private string? PickFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    private void PickExecutableAndAssign(Action<string> assign, string fileName)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Select {fileName}",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            FilterIndex = 1
        };

        if (dialog.ShowDialog() != true) return;
        assign(dialog.FileName);
        ViewModel.FooterStatus = $"{fileName} path selected.";
    }
}
