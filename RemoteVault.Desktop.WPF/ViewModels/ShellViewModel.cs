using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteVault.Contracts.Dtos;
using RemoteVault.Core.Services;

namespace RemoteVault.Desktop.WPF.ViewModels;

public partial class ShellViewModel : ObservableObject
{
    private readonly IAppStateService _appStateService;

    [ObservableProperty]
    private string _appTitle = "RemoteVault";

    [ObservableProperty]
    private string _statusHeadline = "RemoteVault ready";

    [ObservableProperty]
    private string _statusDetail = "Loading backup status...";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private AppSummaryDto? _summary;

    public ShellViewModel(IAppStateService appStateService)
    {
        _appStateService = appStateService;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            Summary = await _appStateService.LoadSummaryAsync();
            StatusHeadline = Summary.HealthState;
            StatusDetail = $"Last backup: {Summary.LastBackupDisplay}   Next scheduled: {Summary.NextBackupDisplay}";
        }
        catch (Exception ex)
        {
            StatusHeadline = "Status unavailable";
            StatusDetail = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
