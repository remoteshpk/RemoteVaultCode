using System.Windows;

namespace RemoteVault.Desktop.WPF.Views;

public partial class TimestampPickerDialog : Window
{
    public DateTime? SelectedDateTime { get; private set; }

    public TimestampPickerDialog()
    {
        InitializeComponent();
        var now = DateTime.Now;
        DatePickerControl.SelectedDate = now;
        HourBox.Text = now.Hour.ToString();
        MinuteBox.Text = now.Minute.ToString();
        SecondBox.Text = now.Second.ToString();
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (DatePickerControl.SelectedDate is null)
        {
            ErrorText.Text = "Please select a date.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (!int.TryParse(HourBox.Text, out var hour) || hour < 0 || hour > 23 ||
            !int.TryParse(MinuteBox.Text, out var minute) || minute < 0 || minute > 59 ||
            !int.TryParse(SecondBox.Text, out var second) || second < 0 || second > 59)
        {
            ErrorText.Text = "Hour must be 0-23, minute and second must be 0-59.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var date = DatePickerControl.SelectedDate.Value;
        SelectedDateTime = new DateTime(date.Year, date.Month, date.Day, hour, minute, second);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
