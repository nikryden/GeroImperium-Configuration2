using System.Windows;
using GeroImperium.App.ViewModels;
using Wpf.Ui.Controls;

namespace GeroImperium.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly ApplicationsPage _applicationsPage;
    private readonly KeyGroupsPage _keyGroupsPage;
    private readonly SyncPage _syncPage;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        _applicationsPage = new ApplicationsPage(viewModel.ApplicationsViewModel);
        _keyGroupsPage = new KeyGroupsPage(viewModel.KeyGroupsViewModel);
        _syncPage = new SyncPage(viewModel.SyncViewModel);

        PageHost.Content = _applicationsPage;
    }

    private void ApplicationsNavItem_Click(object sender, RoutedEventArgs e)
        => PageHost.Content = _applicationsPage;

    private void KeyGroupsNavItem_Click(object sender, RoutedEventArgs e)
        => PageHost.Content = _keyGroupsPage;

    private void SyncNavItem_Click(object sender, RoutedEventArgs e)
        => PageHost.Content = _syncPage;
}
