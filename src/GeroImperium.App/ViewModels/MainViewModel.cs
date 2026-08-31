using CommunityToolkit.Mvvm.ComponentModel;
using GeroImperium.Core.Data;

namespace GeroImperium.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "GeroImperium";

    public ApplicationsViewModel ApplicationsViewModel { get; }

    public KeyGroupsViewModel KeyGroupsViewModel { get; }

    public MainViewModel(GeroImperiumRepository repository)
    {
        ApplicationsViewModel = new ApplicationsViewModel(repository);
        KeyGroupsViewModel = new KeyGroupsViewModel(repository);
    }
}
