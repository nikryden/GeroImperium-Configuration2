using System.Windows;
using GeroImperium.App.ViewModels;

namespace GeroImperium.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
