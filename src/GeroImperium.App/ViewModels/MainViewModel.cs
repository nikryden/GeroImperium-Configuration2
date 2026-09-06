using System;
using CommunityToolkit.Mvvm.ComponentModel;
using GeroImperium.Core.Data;

namespace GeroImperium.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _title = "GeroImperium";

    public ApplicationsViewModel ApplicationsViewModel { get; }

    public KeyGroupsViewModel KeyGroupsViewModel { get; }

    public SyncViewModel SyncViewModel { get; }

    public ProvisioningViewModel ProvisioningViewModel { get; }

    public MainViewModel(GeroImperiumRepository repository)
    {
        ApplicationsViewModel = new ApplicationsViewModel(repository);
        KeyGroupsViewModel = new KeyGroupsViewModel(repository);
        SyncViewModel = new SyncViewModel(repository);
        ProvisioningViewModel = new ProvisioningViewModel(repository);

        // Lets a just-provisioned IP show up in the Sync page immediately, without requiring an app restart to
        // re-read GeneralSettings.
        ProvisioningViewModel.IpLearned += (_, ip) => SyncViewModel.DeviceIpAddress = ip;
    }

    public void Dispose() => SyncViewModel.Dispose();
}
