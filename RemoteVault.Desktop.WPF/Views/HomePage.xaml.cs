using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.ViewModels;

namespace RemoteVault.Desktop.WPF.Views;

public partial class HomePage : Page
{
    public HomePageViewModel ViewModel { get; }

    public HomePage()
    {
        ViewModel = new HomePageViewModel(
            App.Services.GetRequiredService<IAppStateService>(),
            App.Services.GetRequiredService<IOperationHistoryStore>(),
            App.Services.GetRequiredService<IAppConfigStore>());
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
        catch (System.Text.Json.JsonException)
        {
            var historyStore = App.Services.GetRequiredService<IOperationHistoryStore>();
            await historyStore.ResetAsync();
            await ViewModel.LoadAsync();
            return;
        }
        catch (Exception ex) when (ex.Message.Contains("JSON tokens", StringComparison.OrdinalIgnoreCase))
        {
            var historyStore = App.Services.GetRequiredService<IOperationHistoryStore>();
            await historyStore.ResetAsync();
            await ViewModel.LoadAsync();
            return;
        }

        if (ViewModel.PendingHistoryRecovery is not null)
        {
            var issue = ViewModel.PendingHistoryRecovery;
            var result = MessageBox.Show(
                $"{issue.RecoveryMessage}{Environment.NewLine}{Environment.NewLine}File: {issue.RecoveryFilePath}{Environment.NewLine}{Environment.NewLine}You can recreate the file now to continue.",
                "Recent activity file issue",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.OK)
            {
                var historyStore = App.Services.GetRequiredService<IOperationHistoryStore>();
                await historyStore.ResetAsync();
                await ViewModel.LoadAsync();
                return;
            }

            App.MainWindowInstance?.Close();
        }
    }

    /// <summary>"Back up now" starts a full backup; before setup it leads to Settings instead.</summary>
    private void OnHeroActionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsConfigured)
        {
            App.MainWindowInstance?.NavigateTo("backups", startFullBackup: true);
        }
        else
        {
            App.MainWindowInstance?.NavigateTo("settings");
        }
    }

    private void OnOpenRestoreClick(object sender, RoutedEventArgs e)
    {
        App.MainWindowInstance?.NavigateTo("restore");
    }
}
