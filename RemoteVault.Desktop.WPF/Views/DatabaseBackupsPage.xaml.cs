using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.ViewModels;

namespace RemoteVault.Desktop.WPF.Views;

public partial class DatabaseBackupsPage : Page
{
    public DatabaseBackupsPageViewModel ViewModel { get; }
    private readonly IDatabaseBackupService _databaseBackupService;
    private bool _backupRunInProgress;

    public DatabaseBackupsPage()
    {
        _databaseBackupService = App.Services.GetRequiredService<IDatabaseBackupService>();
        ViewModel = new DatabaseBackupsPageViewModel(
            App.Services.GetRequiredService<IAppConfigStore>(),
            _databaseBackupService,
            App.Services.GetRequiredService<AppLaunchOptions>());
        DataContext = ViewModel;
        InitializeComponent();
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Failed to load: {ex.Message}";
        }
    }

    private async void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ShowProfileEditor(null);
            if (profile is not null) ViewModel.UpsertProfile(profile);
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Failed to add profile: {ex.Message}";
        }
    }

    private async void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = GetSelectedProfiles();
            if (selected.Count != 1)
            {
                MessageBox.Show("Select one database entry to edit.", "Database backup",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var profile = ShowProfileEditor(selected[0]);
            if (profile is not null) ViewModel.UpsertProfile(profile);
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Failed to edit profile: {ex.Message}";
        }
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = GetSelectedProfiles();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select at least one database entry to delete.", "Database backup",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirmMsg = selected.Count == 1
                ? $"Delete profile '{selected[0].ProfileName}'?"
                : $"Delete {selected.Count} selected profiles?";

            if (MessageBox.Show(confirmMsg, "Delete profile", MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                ViewModel.RemoveProfiles(selected);
            }
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Failed to delete profile: {ex.Message}";
        }
    }

    private async void OnSaveProfilesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await ViewModel.SaveAsync();
            if (!result.IsSuccess)
                MessageBox.Show(result.Message, "Database backup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Save failed: {ex.Message}";
        }
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.ReloadAsync();
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Reload failed: {ex.Message}";
        }
    }

    private async void OnRunFullNowClick(object sender, RoutedEventArgs e)
    {
        if (_backupRunInProgress || ViewModel.IsBusy) return;
        _backupRunInProgress = true;
        RunFullNowButton.IsEnabled = false;
        RunDiffNowButton.IsEnabled = false;
        try { await RunBackupAsync("full"); }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Backup failed: {ex.Message}";
            _backupRunInProgress = false;
            SetRunButtonsEnabled(ViewModel.CanRunBackups);
        }
    }

    private async void OnRunDiffNowClick(object sender, RoutedEventArgs e)
    {
        if (_backupRunInProgress || ViewModel.IsBusy) return;
        _backupRunInProgress = true;
        RunFullNowButton.IsEnabled = false;
        RunDiffNowButton.IsEnabled = false;
        try { await RunBackupAsync("diff"); }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Backup failed: {ex.Message}";
            _backupRunInProgress = false;
            SetRunButtonsEnabled(ViewModel.CanRunBackups);
        }
    }

    private async Task RunBackupAsync(string backupType)
    {
        var selected = GetSelectedProfiles();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one database entry first.", "Database backup",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _backupRunInProgress = false;
            SetRunButtonsEnabled(ViewModel.CanRunBackups);
            return;
        }

        try
        {
            var result = await ViewModel.RunNowAsync(
                backupType,
                selected.Select(item => item.ProfileId).ToList(),
                HandleWorkerEvent);

            MessageBox.Show(
                result.IsSuccess ? result.Message : $"Database backup finished with errors.\n\n{result.Message}",
                "Database backup", MessageBoxButton.OK,
                result.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            _backupRunInProgress = false;
            SetRunButtonsEnabled(ViewModel.CanRunBackups);
        }
    }

    private void SetRunButtonsEnabled(bool isEnabled)
    {
        if (RunFullNowButton is not null) RunFullNowButton.IsEnabled = isEnabled;
        if (RunDiffNowButton is not null) RunDiffNowButton.IsEnabled = isEnabled;
    }

    private void HandleWorkerEvent(DatabaseBackupWorkerEvent workerEvent)
    {
        Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(workerEvent.Message))
                ViewModel.FooterMessage = workerEvent.Message!;
            else if (!string.IsNullOrWhiteSpace(workerEvent.Payload.Line))
                ViewModel.FooterMessage = workerEvent.Payload.Line!;
        });
    }

    private List<DatabaseBackupProfileItem> GetSelectedProfiles()
    {
        var selected = ProfilesListBox.SelectedItems.OfType<DatabaseBackupProfileItem>().ToList();
        if (selected.Count == 0 && ViewModel.SelectedProfile is not null)
            selected.Add(ViewModel.SelectedProfile);
        return selected;
    }

    private DatabaseBackupProfileItem? ShowProfileEditor(DatabaseBackupProfileItem? existing)
    {
        var dialog = new DatabaseProfileEditorDialog(existing, _databaseBackupService, ViewModel.ConfigPath)
        {
            Owner = Window.GetWindow(this)
        };
        return dialog.ShowDialog() == true ? dialog.ResultProfile : null;
    }
}
