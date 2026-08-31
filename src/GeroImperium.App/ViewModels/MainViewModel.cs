using CommunityToolkit.Mvvm.ComponentModel;

namespace GeroImperium.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "GeroImperium";
}
