using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Data;
using Microsoft.Win32;

namespace GeroImperium.App.ViewModels;

public sealed partial class ApplicationsViewModel : ObservableObject
{
    /// <summary>A page shows up to 6 Applications together (doc/windows_app_api_guide.md: "ApplicationsPage --
    /// a 'page': up to 6 Applications shown together") -- this is a hard device-side layout limit, not a
    /// preference, so it's enforced here rather than left to the device to reject/misrender past it.</summary>
    public const int MaxApplicationsPerPage = 6;

    private readonly GeroImperiumRepository _repository;

    /// <summary>Every application across every page -- Applications (below) is filtered to SelectedPage from
    /// this list rather than re-querying the repository on every page switch.</summary>
    private readonly List<ApplicationItemViewModel> _allApplications = [];

    public ObservableCollection<ApplicationPageItemViewModel> Pages { get; } = [];

    [ObservableProperty]
    private ApplicationPageItemViewModel? _selectedPage;

    public ObservableCollection<ApplicationItemViewModel> Applications { get; } = [];

    [ObservableProperty]
    private ApplicationItemViewModel? _selectedApplication;

    public string ApplicationCountText => $"{Applications.Count}/{MaxApplicationsPerPage}";

    public IRelayCommand AddPageCommand { get; }
    public IRelayCommand DeletePageCommand { get; }
    public IRelayCommand MovePageEarlierCommand { get; }
    public IRelayCommand MovePageLaterCommand { get; }

    public IRelayCommand AddApplicationCommand { get; }
    public IRelayCommand<ApplicationItemViewModel> DeleteApplicationCommand { get; }
    public IRelayCommand<ApplicationItemViewModel> PickImageCommand { get; }
    public IRelayCommand MoveApplicationEarlierCommand { get; }
    public IRelayCommand MoveApplicationLaterCommand { get; }

    public ApplicationsViewModel(GeroImperiumRepository repository)
    {
        _repository = repository;

        AddPageCommand = new RelayCommand(AddPage);
        DeletePageCommand = new RelayCommand(DeletePage, () => SelectedPage is not null && Pages.Count > 1);
        MovePageEarlierCommand = new RelayCommand(() => MovePage(-1), () => CanMovePage(-1));
        MovePageLaterCommand = new RelayCommand(() => MovePage(1), () => CanMovePage(1));

        AddApplicationCommand = new RelayCommand(AddApplication, () => SelectedPage is not null && Applications.Count < MaxApplicationsPerPage);
        DeleteApplicationCommand = new RelayCommand<ApplicationItemViewModel>(DeleteApplication);
        PickImageCommand = new RelayCommand<ApplicationItemViewModel>(PickImage);
        MoveApplicationEarlierCommand = new RelayCommand(() => MoveApplication(-1), () => CanMoveApplication(-1));
        MoveApplicationLaterCommand = new RelayCommand(() => MoveApplication(1), () => CanMoveApplication(1));

        foreach (var page in _repository.GetApplicationPages())
        {
            Pages.Add(new ApplicationPageItemViewModel(page, _repository));
        }

        foreach (var app in _repository.GetApplications())
        {
            _allApplications.Add(new ApplicationItemViewModel(app, _repository));
        }

        SelectedPage = Pages.Count > 0 ? Pages[0] : null;
    }

    partial void OnSelectedPageChanged(ApplicationPageItemViewModel? value)
    {
        Applications.Clear();
        if (value is not null)
        {
            foreach (var app in _allApplications.Where(a => a.ApplicationPageId == value.Id))
            {
                Applications.Add(app);
            }
        }

        SelectedApplication = Applications.Count > 0 ? Applications[0] : null;
        DeletePageCommand.NotifyCanExecuteChanged();
        NotifyPageNavigationCommands();
        NotifyApplicationCountChanged();
    }

    partial void OnSelectedApplicationChanged(ApplicationItemViewModel? value)
    {
        PickImageCommand.NotifyCanExecuteChanged();
        NotifyApplicationNavigationCommands();
    }

    private void AddPage()
    {
        var page = _repository.AddApplicationPage($"Page {Pages.Count + 1}");
        var item = new ApplicationPageItemViewModel(page, _repository);
        Pages.Add(item);
        SelectedPage = item;
    }

    private void DeletePage()
    {
        if (SelectedPage is not { } page || Pages.Count <= 1)
        {
            return;
        }

        _repository.DeleteApplicationPage(page.Id);
        _allApplications.RemoveAll(a => a.ApplicationPageId == page.Id);
        var index = Pages.IndexOf(page);
        Pages.Remove(page);
        SelectedPage = Pages.Count > 0 ? Pages[Math.Min(index, Pages.Count - 1)] : null;
    }

    private bool CanMovePage(int direction)
    {
        if (SelectedPage is null)
        {
            return false;
        }

        var targetIndex = Pages.IndexOf(SelectedPage) + direction;
        return targetIndex >= 0 && targetIndex < Pages.Count;
    }

    /// <summary>Swaps SelectedPage's "Order" with its neighbor and persists both -- GetApplicationPages sorts
    /// by "Order", so this is what actually changes the page's position.</summary>
    private void MovePage(int direction)
    {
        if (SelectedPage is not { } page)
        {
            return;
        }

        var currentIndex = Pages.IndexOf(page);
        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= Pages.Count)
        {
            return;
        }

        var neighbor = Pages[targetIndex];
        (string pageOrder, string neighborOrder) = (page.Order, neighbor.Order);
        page.SetOrder(neighborOrder);
        neighbor.SetOrder(pageOrder);

        Pages.Move(currentIndex, targetIndex);
        NotifyPageNavigationCommands();
    }

    private void AddApplication()
    {
        if (SelectedPage is not { } page || Applications.Count >= MaxApplicationsPerPage)
        {
            return;
        }

        var app = _repository.AddApplication("New Application", page.Id);
        var item = new ApplicationItemViewModel(app, _repository);
        _allApplications.Add(item);
        Applications.Add(item);
        SelectedApplication = item;
        NotifyApplicationCountChanged();
    }

    private void DeleteApplication(ApplicationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _repository.DeleteApplication(item.Id);
        _allApplications.Remove(item);
        Applications.Remove(item);

        if (ReferenceEquals(SelectedApplication, item))
        {
            SelectedApplication = Applications.Count > 0 ? Applications[0] : null;
        }

        NotifyApplicationCountChanged();
    }

    private bool CanMoveApplication(int direction)
    {
        if (SelectedApplication is null)
        {
            return false;
        }

        var targetIndex = Applications.IndexOf(SelectedApplication) + direction;
        return targetIndex >= 0 && targetIndex < Applications.Count;
    }

    /// <summary>Swaps SelectedApplication's "Order" with its neighbor within the current page and persists
    /// both -- GetApplications sorts by "Order", mirroring KeyGroupsViewModel.MoveKeyGroup.</summary>
    private void MoveApplication(int direction)
    {
        if (SelectedApplication is not { } app)
        {
            return;
        }

        var currentIndex = Applications.IndexOf(app);
        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= Applications.Count)
        {
            return;
        }

        var neighbor = Applications[targetIndex];
        (string appOrder, string neighborOrder) = (app.Order, neighbor.Order);
        app.SetOrder(neighborOrder);
        neighbor.SetOrder(appOrder);

        Applications.Move(currentIndex, targetIndex);
        NotifyApplicationNavigationCommands();
    }

    private static void PickImage(ApplicationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg;*.svg)|*.png;*.jpg;*.jpeg;*.svg",
        };

        if (dialog.ShowDialog() == true)
        {
            item.SetImageFromFile(dialog.FileName);
        }
    }

    private void NotifyPageNavigationCommands()
    {
        DeletePageCommand.NotifyCanExecuteChanged();
        MovePageEarlierCommand.NotifyCanExecuteChanged();
        MovePageLaterCommand.NotifyCanExecuteChanged();
    }

    private void NotifyApplicationNavigationCommands()
    {
        MoveApplicationEarlierCommand.NotifyCanExecuteChanged();
        MoveApplicationLaterCommand.NotifyCanExecuteChanged();
    }

    private void NotifyApplicationCountChanged()
    {
        OnPropertyChanged(nameof(ApplicationCountText));
        AddApplicationCommand.NotifyCanExecuteChanged();
    }
}
