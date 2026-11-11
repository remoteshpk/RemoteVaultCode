using System.Windows;
using System.Windows.Controls;
using RemoteVault.Core.Services;
using RemoteVault.Desktop.WPF.ViewModels;

namespace RemoteVault.Desktop.WPF.Views;

public partial class DatabaseProfileEditorDialog : Window
{
    public DatabaseBackupProfileItem? ResultProfile { get; private set; }

    private readonly IDatabaseBackupService _databaseBackupService;
    private readonly string _configPath;
    private readonly DatabaseBackupProfileItem? _existing;
    private readonly HashSet<string> _preselectedDatabases;
    private string _lastSelectedDbType = "mssql";
    private bool _syncingSelection;

    public DatabaseProfileEditorDialog(DatabaseBackupProfileItem? existing, IDatabaseBackupService databaseBackupService, string configPath)
    {
        _existing = existing;
        _databaseBackupService = databaseBackupService;
        _configPath = configPath;
        InitializeComponent();

        Title = existing is null ? "Add database" : "Edit database";

        var workingCopy = existing?.Clone() ?? new DatabaseBackupProfileItem();
        _preselectedDatabases = DatabaseBackupProfileItem.ParseDatabases(workingCopy.DatabaseListText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lastSelectedDbType = workingCopy.DatabaseType;

        NameBox.Text = workingCopy.ProfileName;
        HostBox.Text = workingCopy.Host;
        PortBox.Text = workingCopy.Port.ToString();
        UsernameBox.Text = workingCopy.Username;
        PasswordBox.Password = workingCopy.Password;
        InstanceBox.Text = workingCopy.Instance;
        KeepChainsBox.Text = workingCopy.KeepFullChains.ToString();
        DiffEnabledBox.IsChecked = workingCopy.DiffEnabled;
        DatabasesTextBox.Text = workingCopy.DatabaseListText;

        // Select the right ComboBox item
        foreach (ComboBoxItem item in TypeBox.Items)
        {
            if (string.Equals(item.Content?.ToString(), workingCopy.DatabaseType, StringComparison.OrdinalIgnoreCase))
            {
                TypeBox.SelectedItem = item;
                break;
            }
        }
        if (TypeBox.SelectedItem is null) TypeBox.SelectedIndex = 0;

        DiscoveryStatusText.Text = _preselectedDatabases.Count > 0
            ? $"{_preselectedDatabases.Count} database(s) currently selected."
            : "Enter databases below or use Discover to scan from the server.";

        UpdateTypeSpecificFieldVisibility();

        if (_preselectedDatabases.Count > 0)
            ApplyDiscoveredDatabases(_preselectedDatabases);
    }

    private string GetSelectedDbType()
        => (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "mssql";

    private void OnTypeBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedType = GetSelectedDbType();
        var suggestedPort = DatabaseBackupProfileItem.DefaultPortFor(selectedType);
        if (int.TryParse(PortBox.Text, out var currentPort))
        {
            if (currentPort == DatabaseBackupProfileItem.DefaultPortFor(_lastSelectedDbType))
                PortBox.Text = suggestedPort.ToString();
        }
        else if (string.IsNullOrWhiteSpace(PortBox.Text))
        {
            PortBox.Text = suggestedPort.ToString();
        }

        _lastSelectedDbType = selectedType;
        UpdateTypeSpecificFieldVisibility();
    }

    private void UpdateTypeSpecificFieldVisibility()
    {
        var isMssql = string.Equals(GetSelectedDbType(), "mssql", StringComparison.OrdinalIgnoreCase);
        InstanceBox.Visibility = isMssql ? Visibility.Visible : Visibility.Collapsed;
        InstanceLabel.Visibility = isMssql ? Visibility.Visible : Visibility.Collapsed;
        DiffEnabledBox.Visibility = isMssql ? Visibility.Visible : Visibility.Collapsed;
        if (!isMssql) DiffEnabledBox.IsChecked = false;
    }

    private void OnDatabasesListBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        SyncDatabaseTextFromSelection();
    }

    private void OnDatabasesTextBoxChanged(object sender, TextChangedEventArgs e)
    {
        // Manual typing takes priority; keep list in sync but don't overwrite text
    }

    private void SyncDatabaseTextFromSelection()
    {
        var selected = DatabasesListBox.SelectedItems.OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count > 0)
        {
            DatabasesTextBox.Text = string.Join(Environment.NewLine, selected);
            DiscoveryStatusText.Text = $"{selected.Count} database(s) selected.";
        }
        else if (DatabasesListBox.Items.Count > 0)
        {
            DatabasesTextBox.Text = string.Empty;
            DiscoveryStatusText.Text = "No databases selected from the discovered list.";
        }
    }

    private void ApplyDiscoveredDatabases(IEnumerable<string> names)
    {
        _syncingSelection = true;
        var all = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DatabasesListBox.ItemsSource = all;
        DatabasesListBox.SelectedItems.Clear();

        foreach (var name in all)
        {
            if (_preselectedDatabases.Contains(name))
                DatabasesListBox.SelectedItems.Add(name);
        }

        if (DatabasesListBox.SelectedItems.Count == 0 && all.Count > 0)
        {
            foreach (var name in all)
                DatabasesListBox.SelectedItems.Add(name);
        }

        _syncingSelection = false;
        SyncDatabaseTextFromSelection();
    }

    private async void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Visibility = Visibility.Collapsed;
        DiscoveryStatusText.Text = "Discovering databases...";
        DiscoverButton.IsEnabled = false;
        try
        {
            var probe = new DatabaseBackupProfilePayload
            {
                ProfileId = _existing?.ProfileId ?? Guid.NewGuid().ToString(),
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "tmp" : NameBox.Text.Trim(),
                DbType = GetSelectedDbType(),
                Host = HostBox.Text.Trim(),
                Port = int.TryParse(PortBox.Text.Trim(), out var portValue) ? portValue : 0,
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password,
                Instance = string.Equals(GetSelectedDbType(), "mssql", StringComparison.OrdinalIgnoreCase)
                    ? InstanceBox.Text.Trim() : string.Empty
            };

            var result = await _databaseBackupService.DiscoverDatabasesAsync(_configPath, probe);

            if (!result.IsSuccess)
            {
                ValidationText.Text = result.Message;
                ValidationText.Visibility = Visibility.Visible;
                DiscoveryStatusText.Text = "Database discovery failed.";
                return;
            }

            ApplyDiscoveredDatabases(result.Databases);
            DiscoveryStatusText.Text = result.Databases.Count == 0
                ? "No databases were returned by the server."
                : $"Discovered {result.Databases.Count} database(s).";
        }
        catch (Exception ex)
        {
            ValidationText.Text = ex.Message;
            ValidationText.Visibility = Visibility.Visible;
            DiscoveryStatusText.Text = "Database discovery failed.";
        }
        finally
        {
            DiscoverButton.IsEnabled = true;
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        _syncingSelection = true;
        DatabasesListBox.SelectedItems.Clear();
        foreach (var item in DatabasesListBox.Items.Cast<string>())
            DatabasesListBox.SelectedItems.Add(item);
        _syncingSelection = false;
        SyncDatabaseTextFromSelection();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        DatabasesListBox.SelectedItems.Clear();
        SyncDatabaseTextFromSelection();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        ValidationText.Visibility = Visibility.Collapsed;

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            ShowError("Port must be a number between 1 and 65535.");
            return;
        }

        if (!int.TryParse(KeepChainsBox.Text.Trim(), out var keepChains) || keepChains < 1)
        {
            ShowError("Full sets kept must be at least 1.");
            return;
        }

        var databases = DatabaseBackupProfileItem.ParseDatabases(DatabasesTextBox.Text);
        if (databases.Count == 0)
        {
            ShowError("Please enter at least one database name.");
            return;
        }

        var dbType = GetSelectedDbType();
        var requiresUsername = !string.Equals(dbType, "mssql", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(HostBox.Text) ||
            (requiresUsername && string.IsNullOrWhiteSpace(UsernameBox.Text)))
        {
            ShowError(requiresUsername
                ? "Profile name, host, and username are required."
                : "Profile name and host are required.");
            return;
        }

        ResultProfile = new DatabaseBackupProfileItem
        {
            ProfileId = _existing?.ProfileId ?? Guid.NewGuid().ToString(),
            ProfileName = NameBox.Text.Trim(),
            DatabaseType = dbType,
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = UsernameBox.Text.Trim(),
            Password = PasswordBox.Password,
            Instance = string.Equals(dbType, "mssql", StringComparison.OrdinalIgnoreCase)
                ? InstanceBox.Text.Trim() : string.Empty,
            DatabaseListText = string.Join(Environment.NewLine, databases),
            KeepFullChains = keepChains,
            DiffEnabled = DiffEnabledBox.IsChecked == true &&
                          string.Equals(dbType, "mssql", StringComparison.OrdinalIgnoreCase)
        };

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
