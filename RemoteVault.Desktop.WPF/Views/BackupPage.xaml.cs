using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.ViewModels;

namespace RemoteVault.Desktop.WPF.Views;

public partial class BackupPage : Page
{
    public BackupPageViewModel ViewModel { get; }

    private bool _startFullBackupWhenLoaded;

    /// <param name="startFullBackup">Start a full backup as soon as the page has loaded
    /// (used by "Back up now" on the Home page).</param>
    public BackupPage(bool startFullBackup = false)
    {
        _startFullBackupWhenLoaded = startFullBackup;
        ViewModel = new BackupPageViewModel(
            App.Services.GetRequiredService<IAppConfigStore>(),
            App.Services.GetRequiredService<IBackupExecutionService>(),
            App.Services.GetRequiredService<IBackupStateStore>(),
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

            if (_startFullBackupWhenLoaded)
            {
                _startFullBackupWhenLoaded = false;
                if (ViewModel.CanTriggerFullBackup && ViewModel.RunFullBackupNowCommand.CanExecute(null))
                {
                    ViewModel.RunFullBackupNowCommand.Execute(null);
                }
            }
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Failed to load: {ex.Message}";
        }
    }
}
