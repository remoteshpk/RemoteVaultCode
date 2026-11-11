using Microsoft.Extensions.DependencyInjection;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.ViewModels;
using RemoteVault.Desktop.WPF.Views;
using System.Windows;
using System.Windows.Controls;

namespace RemoteVault.Desktop.WPF.Shell;

public partial class MainWindow : Window
{
    public ShellViewModel ViewModel { get; }

    public MainWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        TrySetWindowIcon();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? string.Empty : $"Version {version.ToString(3)}";
        _ = RefreshDatabaseNavigationVisibilityAsync();
        NavListBox.SelectedIndex = 0;
        ContentFrame.Navigate(new HomePage());
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "RemoteVault.ico");
            if (File.Exists(iconPath))
            {
                // BitmapFrame keeps every size in the .ico, so Windows gets a full-size
                // image for the taskbar; BitmapImage would hand over only one small frame.
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
            }
        }
        catch { }
    }

    public async Task RefreshDatabaseNavigationVisibilityAsync()
    {
        var configStore = App.Services.GetService(typeof(IAppConfigStore)) as IAppConfigStore;
        var config = configStore is null ? null : await configStore.LoadAsync();
        SetDatabaseNavigationVisibility(config?.EnableDatabaseBackups == true);
    }

    public void SetDatabaseNavigationVisibility(bool isEnabled)
    {
        DatabaseNavItem.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (!isEnabled &&
            NavListBox.SelectedItem is ListBoxItem selectedItem &&
            string.Equals(selectedItem.Tag?.ToString(), "database", StringComparison.OrdinalIgnoreCase))
        {
            NavListBox.SelectedIndex = 0;
            ContentFrame.Navigate(new HomePage());
        }
    }

    private bool _startFullBackupOnNavigate;

    /// <summary>
    /// Switches page through the sidebar so its selection stays in sync with what is shown.
    /// Tags: home, backups, restore, database, settings.
    /// </summary>
    public void NavigateTo(string tag, bool startFullBackup = false)
    {
        var target = NavListBox.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.Visibility != Visibility.Visible)
        {
            return;
        }

        _startFullBackupOnNavigate = startFullBackup;
        if (ReferenceEquals(NavListBox.SelectedItem, target))
        {
            OnNavSelectionChanged(NavListBox, null!);
        }
        else
        {
            NavListBox.SelectedItem = target;
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (NavListBox.SelectedItem is not ListBoxItem item)
            {
                return;
            }

            var tag = item.Tag?.ToString();
            switch (tag)
            {
                case "home":
                    ContentFrame.Navigate(new HomePage());
                    break;
                case "backups":
                    ContentFrame.Navigate(new BackupPage(_startFullBackupOnNavigate));
                    _startFullBackupOnNavigate = false;
                    break;
                case "restore":
                    ContentFrame.Navigate(new RestorePage());
                    break;
                case "database":
                    ContentFrame.Navigate(new DatabaseBackupsPage());
                    break;
                case "settings":
                    ContentFrame.Navigate(new SettingsPage());
                    break;
            }
        }
        catch { }
    }
}
