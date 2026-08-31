using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Data;
using GeroImperium.Core.Models;
using Microsoft.Win32;
using CoreApplication = GeroImperium.Core.Models.Application;

namespace GeroImperium.App.ViewModels;

public sealed partial class KeyGroupsViewModel : ObservableObject
{
    private readonly GeroImperiumRepository _repository;

    public ObservableCollection<CoreApplication> Applications { get; } = [];

    [ObservableProperty]
    private CoreApplication? _selectedApplication;

    public ObservableCollection<KeyGroup> KeyGroups { get; } = [];

    [ObservableProperty]
    private KeyGroup? _selectedKeyGroup;

    public ObservableCollection<KeySlotViewModel> KeySlots { get; } = [];

    public IRelayCommand AddKeyGroupCommand { get; }
    public IRelayCommand DeleteKeyGroupCommand { get; }
    public IRelayCommand<KeySlotViewModel> PickImageCommand { get; }

    public KeyGroupsViewModel(GeroImperiumRepository repository)
    {
        _repository = repository;

        foreach (var app in _repository.GetApplications())
        {
            Applications.Add(app);
        }

        AddKeyGroupCommand = new RelayCommand(AddKeyGroup, () => SelectedApplication is not null);
        DeleteKeyGroupCommand = new RelayCommand(DeleteKeyGroup, () => SelectedKeyGroup is not null);
        PickImageCommand = new RelayCommand<KeySlotViewModel>(PickImage);

        if (Applications.Count > 0)
        {
            SelectedApplication = Applications[0];
        }
    }

    partial void OnSelectedApplicationChanged(CoreApplication? value)
    {
        KeyGroups.Clear();
        SelectedKeyGroup = null;

        if (value is not null)
        {
            foreach (var group in _repository.GetKeyGroups(value.Id))
            {
                KeyGroups.Add(group);
            }

            if (KeyGroups.Count > 0)
            {
                SelectedKeyGroup = KeyGroups[0];
            }
        }

        AddKeyGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedKeyGroupChanged(KeyGroup? value)
    {
        KeySlots.Clear();

        if (value is not null)
        {
            foreach (var key in _repository.GetKeys(value.Id))
            {
                KeySlots.Add(new KeySlotViewModel(key, _repository));
            }
        }

        DeleteKeyGroupCommand.NotifyCanExecuteChanged();
    }

    private void AddKeyGroup()
    {
        if (SelectedApplication is null)
        {
            return;
        }

        var group = _repository.AddKeyGroup(SelectedApplication.Id, $"Group {KeyGroups.Count + 1}");
        KeyGroups.Add(group);
        SelectedKeyGroup = group;
    }

    private void DeleteKeyGroup()
    {
        if (SelectedKeyGroup is null)
        {
            return;
        }

        _repository.DeleteKeyGroup(SelectedKeyGroup.Id);
        KeyGroups.Remove(SelectedKeyGroup);
        SelectedKeyGroup = null;
    }

    private static void PickImage(KeySlotViewModel? slot)
    {
        if (slot is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "Images (*.png;*.jpg;*.jpeg;*.svg)|*.png;*.jpg;*.jpeg;*.svg",
        };

        if (dialog.ShowDialog() == true)
        {
            slot.SetImageFromFile(dialog.FileName);
        }
    }
}
