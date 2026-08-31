using System.Windows;
using GeroImperium.App.ViewModels;
using Wpf.Ui.Controls;

namespace GeroImperium.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly ApplicationsPage _applicationsPage;
    private readonly KeyGroupsPage _keyGroupsPage;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        _applicationsPage = new ApplicationsPage(viewModel.ApplicationsViewModel);
        _keyGroupsPage = new KeyGroupsPage(viewModel.KeyGroupsViewModel);

        // NavigationView's content-presenter isn't ready until its template is applied, which hasn't
        // happened yet at construction time -- ReplaceContent here throws a NullReferenceException.
        Loaded += (_, _) => RootNavigation.ReplaceContent(_applicationsPage, _viewModel.ApplicationsViewModel);
    }

    private void ApplicationsNavItem_Click(object sender, RoutedEventArgs e)
        => RootNavigation.ReplaceContent(_applicationsPage, _viewModel.ApplicationsViewModel);

    private void KeyGroupsNavItem_Click(object sender, RoutedEventArgs e)
        => RootNavigation.ReplaceContent(_keyGroupsPage, _viewModel.KeyGroupsViewModel);
}
