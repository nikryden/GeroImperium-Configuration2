using System.Windows.Controls;
using GeroImperium.App.ViewModels;

namespace GeroImperium.App.Views;

public partial class SyncPage : UserControl
{
    public SyncPage(SyncViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
