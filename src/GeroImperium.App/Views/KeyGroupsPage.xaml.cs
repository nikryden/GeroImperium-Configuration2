using System.Windows.Controls;
using GeroImperium.App.ViewModels;

namespace GeroImperium.App.Views;

public partial class KeyGroupsPage : UserControl
{
    public KeyGroupsPage(KeyGroupsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
