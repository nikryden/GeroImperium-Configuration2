using System.Windows;
using GeroImperium.App.Services;
using GeroImperium.App.ViewModels;
using GeroImperium.App.Views;
using GeroImperium.Core.Data;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace GeroImperium.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, updateAccent: true);

        var database = new GeroImperiumDatabase(AppPaths.AuthoringDatabasePath);
        database.EnsureSchemaCreated();
        var repository = new GeroImperiumRepository(database);

        var mainViewModel = new MainViewModel(repository);
        var mainWindow = new MainWindow(mainViewModel);
        mainWindow.Show();
    }
}
