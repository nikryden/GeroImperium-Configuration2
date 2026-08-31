using System.Windows.Controls;
using GeroImperium.App.ViewModels;

namespace GeroImperium.App.Views;

public partial class ApplicationsPage : UserControl
{
    public ApplicationsPage(ApplicationsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
