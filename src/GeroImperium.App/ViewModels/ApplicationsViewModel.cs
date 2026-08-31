using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Data;
using Microsoft.Win32;

namespace GeroImperium.App.ViewModels;

public sealed partial class ApplicationsViewModel : ObservableObject
{
    private readonly GeroImperiumRepository _repository;

    public ObservableCollection<ApplicationItemViewModel> Applications { get; } = [];

    [ObservableProperty]
    private ApplicationItemViewModel? _selectedApplication;

    public IRelayCommand AddApplicationCommand { get; }
    public IRelayCommand<ApplicationItemViewModel> DeleteApplicationCommand { get; }
    public IRelayCommand<ApplicationItemViewModel> PickImageCommand { get; }

    public ApplicationsViewModel(GeroImperiumRepository repository)
    {
        _repository = repository;
        AddApplicationCommand = new RelayCommand(AddApplication);
        DeleteApplicationCommand = new RelayCommand<ApplicationItemViewModel>(DeleteApplication);
        PickImageCommand = new RelayCommand<ApplicationItemViewModel>(PickImage);

        foreach (var app in _repository.GetApplications())
        {
            Applications.Add(new ApplicationItemViewModel(app, _repository));
        }

        if (Applications.Count > 0)
        {
            SelectedApplication = Applications[0];
        }
    }

    private void AddApplication()
    {
        var app = _repository.AddApplication("New Application");
        var item = new ApplicationItemViewModel(app, _repository);
        Applications.Add(item);
        SelectedApplication = item;
    }

    private void DeleteApplication(ApplicationItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _repository.DeleteApplication(item.Id);
        Applications.Remove(item);

        if (ReferenceEquals(SelectedApplication, item))
        {
            SelectedApplication = null;
        }
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
}
