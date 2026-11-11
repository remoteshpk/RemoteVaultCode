using System.Windows;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.Views;

public partial class VersionPickerDialog : Window
{
    public RestoreVersionItem? SelectedVersion { get; private set; }

    public VersionPickerDialog(string fileName, IReadOnlyList<RestoreVersionItem> versions)
    {
        InitializeComponent();
        TitleBlock.Text = $"Versions - {fileName}";
        VersionListBox.ItemsSource = versions;
        if (versions.Count > 0) VersionListBox.SelectedIndex = 0;
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        SelectedVersion = VersionListBox.SelectedItem as RestoreVersionItem;
        if (SelectedVersion is null) return;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
