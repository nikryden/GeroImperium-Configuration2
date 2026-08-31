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

    public MainViewModel(GeroImperiumRepository repository, string databasePath)
    {
        ApplicationsViewModel = new ApplicationsViewModel(repository);
        KeyGroupsViewModel = new KeyGroupsViewModel(repository);
        SyncViewModel = new SyncViewModel(repository, databasePath);
    }

    public void Dispose() => SyncViewModel.Dispose();
}
