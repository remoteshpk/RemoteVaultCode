using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.ViewModels;

namespace RemoteVault.Desktop.WPF.Views;

public partial class RestorePage : Page
{
    public RestorePageViewModel ViewModel { get; }
    private readonly IRestoreExecutionService _restoreExecutionService;
    private readonly IVersionCatalogService _versionCatalogService;

    public RestorePage()
    {
        _restoreExecutionService = App.Services.GetRequiredService<IRestoreExecutionService>();
        _versionCatalogService = App.Services.GetRequiredService<IVersionCatalogService>();
        ViewModel = new RestorePageViewModel(
            App.Services.GetRequiredService<IAppConfigStore>(),
            App.Services.GetRequiredService<IRemoteBrowserService>(),
            App.Services.GetRequiredService<IDatabaseBackupService>());
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

    private void OnBrowseDestinationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select restore destination folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ViewModel.SetDestinationFolder(dialog.SelectedPath);
            }
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Browse failed: {ex.Message}";
        }
    }

    private void OnDestinationModeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radioButton)
        {
            ViewModel.SetDestinationMode(string.Equals(radioButton.Tag?.ToString(), "original", StringComparison.OrdinalIgnoreCase));
        }
    }

    private async void OnBrowserItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            await ViewModel.BrowseIntoSelectedItemAsync();
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Navigation failed: {ex.Message}";
        }
    }

    private async void OnRestoreSelectedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedItems = GetSelectedBrowserItems();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("Select at least one file or folder to restore.", "Restore",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!EnsureDestinationExists("Restore")) return;

            var tasks = ViewModel.BuildRestoreTasks(selectedItems);
            if (tasks.Count == 0)
            {
                MessageBox.Show("No valid items were selected for restore.", "Restore",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ViewModel.FooterMessage = $"Starting restore of {tasks.Count} item(s)...";

            var result = await _restoreExecutionService.RunAsync(
                new RestoreOperationRequest
                {
                    Command = "restore_selected",
                    ConfigPath = ViewModel.ConfigPath,
                    Tasks = tasks
                },
                HandleWorkerEvent);

            MessageBox.Show(
                result.IsSuccess
                    ? $"Restore completed successfully.\n\nSuccess: {result.SuccessCount}\nFailed: {result.FailedCount}\nTotal: {result.TotalCount}"
                    : $"Restore finished with errors.\n\n{result.Message}",
                "Restore", MessageBoxButton.OK,
                result.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Restore failed: {ex.Message}";
        }
    }

    private async void OnRestoreFolderByTimestampClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedItems = GetSelectedBrowserItems();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("Select at least one folder or file first.", "Restore folder as of a date",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!EnsureDestinationExists("Restore folder as of a date")) return;

            var timestamp = PromptForTimestamp();
            if (timestamp is null) return;

            var baseEntry = ViewModel.BuildCurrentBaseEntry();
            if (baseEntry is null)
            {
                MessageBox.Show("No backup source is selected.", "Restore folder as of a date",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selection = ViewModel.BuildTimestampSelection(selectedItems);
            var result = await _restoreExecutionService.RunAsync(
                new RestoreOperationRequest
                {
                    Command = "restore_by_timestamp",
                    ConfigPath = ViewModel.ConfigPath,
                    DestinationRoot = ViewModel.DestinationFolder,
                    CutoffTimestamp = timestamp.Value.ToString("yyyy-MM-ddTHH:mm:ss"),
                    FolderRemotes = selection.FolderRemotes,
                    FileItems = selection.FileItems,
                    BaseEntry = baseEntry
                },
                HandleWorkerEvent);

            MessageBox.Show(
                result.IsSuccess
                    ? $"Restore completed successfully.\n\nSuccess: {result.SuccessCount}\nFailed: {result.FailedCount}\nTotal: {result.TotalCount}"
                    : $"Restore finished with errors.\n\n{result.Message}",
                "Restore folder as of a date", MessageBoxButton.OK,
                result.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Restore by timestamp failed: {ex.Message}";
        }
    }

    private async void OnShowVersionsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedItems = GetSelectedBrowserItems();
            if (selectedItems.Count != 1 || selectedItems[0].IsDirectory)
            {
                MessageBox.Show("Select one file to see its earlier versions.", "Earlier versions",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!EnsureDestinationExists("Earlier versions")) return;

            var selectedItem = selectedItems[0];
            if (ViewModel.StoredConfig is null)
            {
                MessageBox.Show("Configuration is not loaded.", "Earlier versions",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IReadOnlyList<RestoreVersionItem> versionItems;
            try
            {
                versionItems = await _versionCatalogService.ListVersionsAsync(
                    ViewModel.StoredConfig,
                    ViewModel.CurrentRemotePath,
                    selectedItem.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Earlier versions", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (versionItems.Count == 0)
            {
                versionItems = new[]
                {
                    new RestoreVersionItem
                    {
                        Name = selectedItem.Name,
                        VersionName = "Current",
                        DateTime = selectedItem.Modified,
                        SizeKb = ParseSizeKb(selectedItem.SizeDisplay),
                        IsCurrent = true,
                        Kind = "s3"
                    }
                };
            }

            var versionDialog = new VersionPickerDialog(selectedItem.Name, versionItems)
            {
                Owner = Window.GetWindow(this)
            };

            if (versionDialog.ShowDialog() != true || versionDialog.SelectedVersion is null) return;

            var version = versionDialog.SelectedVersion;
            var restoreResult = await _restoreExecutionService.RunAsync(
                new RestoreOperationRequest
                {
                    Command = "restore_version",
                    ConfigPath = ViewModel.ConfigPath,
                    ParentRemotePath = ViewModel.CurrentRemotePath,
                    FileName = selectedItem.Name,
                    DestinationRoot = ViewModel.DestinationFolder,
                    RootRemote = ViewModel.SelectedSource?.RemoteRoot,
                    VersionName = version.Name,
                    VersionId = version.VersionId,
                    DateTime = version.DateTime,
                    IsCurrent = version.IsCurrent
                });

            MessageBox.Show(
                restoreResult.IsSuccess
                    ? $"Version restored to:\n{restoreResult.DestinationPath ?? ViewModel.DestinationFolder}"
                    : restoreResult.Message,
                "Earlier versions", MessageBoxButton.OK,
                restoreResult.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ViewModel.FooterMessage = $"Versions failed: {ex.Message}";
        }
    }

    private List<RestoreBrowserItem> GetSelectedBrowserItems()
    {
        var selected = BrowserListBox.SelectedItems.OfType<RestoreBrowserItem>().ToList();
        if (selected.Count == 0 && ViewModel.SelectedBrowserItem is not null)
            selected.Add(ViewModel.SelectedBrowserItem);
        return selected;
    }

    private bool EnsureDestinationExists(string title)
    {
        var destination = (ViewModel.DestinationFolder ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show("Please choose a destination folder first.", title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (Directory.Exists(destination)) return true;

        var result = MessageBox.Show(
            $"Destination folder does not exist:\n{destination}\n\nCreate it?",
            title, MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (result != MessageBoxResult.OK) return false;

        Directory.CreateDirectory(destination);
        return true;
    }

    private static DateTime? PromptForTimestamp()
    {
        var dialog = new TimestampPickerDialog { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.SelectedDateTime : null;
    }

    private void HandleWorkerEvent(RestoreWorkerEvent workerEvent)
    {
        Dispatcher.Invoke(() =>
        {
            if (workerEvent.EventName == "status" && !string.IsNullOrWhiteSpace(workerEvent.Payload.Remote))
                ViewModel.FooterMessage = $"Restoring {workerEvent.Payload.Index}/{workerEvent.Payload.Total}: {workerEvent.Payload.Remote}";
            else if (workerEvent.EventName == "rclone_stats" && !string.IsNullOrWhiteSpace(workerEvent.Payload.Line))
                ViewModel.FooterMessage = workerEvent.Payload.Line!;
            else if (workerEvent.EventName == "fatal" && !string.IsNullOrWhiteSpace(workerEvent.Message))
                ViewModel.FooterMessage = workerEvent.Message!;
            else if (workerEvent.EventName == "completed")
                ViewModel.FooterMessage = $"Completed. Success: {workerEvent.Success}, Failed: {workerEvent.Failed}, Total: {workerEvent.Total}";
        });
    }

    private static int? ParseSizeKb(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value.Split('.', 2)[0], out var parsed) ? parsed : null;
    }
}
