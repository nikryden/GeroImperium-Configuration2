using System.Windows.Controls;
using GeroImperium.App.ViewModels;

namespace GeroImperium.App.Views;

public partial class ProvisioningPage : UserControl
{
    public ProvisioningPage(ProvisioningViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
