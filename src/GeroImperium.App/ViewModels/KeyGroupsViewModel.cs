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

    /// <summary>Bindable mirror of SelectedKeyGroup.Name -- KeyGroup is a plain model (no property-change
    /// notification of its own), so editing its Name needs a separate observable property that writes
    /// through to the model and persists. Guarded by _isLoadingGroupName so switching SelectedKeyGroup
    /// (which reassigns this) doesn't re-save the value it just loaded.</summary>
    [ObservableProperty]
    private string _selectedKeyGroupName = string.Empty;

    private bool _isLoadingGroupName;

    public ObservableCollection<KeySlotViewModel> KeySlots { get; } = [];

    [ObservableProperty]
    private KeySlotViewModel? _selectedKeySlot;

    public IRelayCommand AddKeyGroupCommand { get; }
    public IRelayCommand DeleteKeyGroupCommand { get; }
    public IRelayCommand PreviousKeyGroupCommand { get; }
    public IRelayCommand NextKeyGroupCommand { get; }
    public IRelayCommand MoveKeyGroupEarlierCommand { get; }
    public IRelayCommand MoveKeyGroupLaterCommand { get; }
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
        PreviousKeyGroupCommand = new RelayCommand(() => StepKeyGroup(-1), () => KeyGroups.Count > 1);
        NextKeyGroupCommand = new RelayCommand(() => StepKeyGroup(1), () => KeyGroups.Count > 1);
        MoveKeyGroupEarlierCommand = new RelayCommand(() => MoveKeyGroup(-1), () => CanMoveKeyGroup(-1));
        MoveKeyGroupLaterCommand = new RelayCommand(() => MoveKeyGroup(1), () => CanMoveKeyGroup(1));
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
        NotifyKeyGroupNavigationCommands();
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

        SelectedKeySlot = KeySlots.Count > 0 ? KeySlots[0] : null;

        _isLoadingGroupName = true;
        SelectedKeyGroupName = value?.Name ?? string.Empty;
        _isLoadingGroupName = false;

        DeleteKeyGroupCommand.NotifyCanExecuteChanged();
        NotifyKeyGroupNavigationCommands();
    }

    partial void OnSelectedKeyGroupNameChanged(string value)
    {
        if (_isLoadingGroupName || SelectedKeyGroup is null)
        {
            return;
        }

        SelectedKeyGroup.Name = value;
        _repository.UpdateKeyGroup(SelectedKeyGroup);
    }

    private void NotifyKeyGroupNavigationCommands()
    {
        PreviousKeyGroupCommand.NotifyCanExecuteChanged();
        NextKeyGroupCommand.NotifyCanExecuteChanged();
        MoveKeyGroupEarlierCommand.NotifyCanExecuteChanged();
        MoveKeyGroupLaterCommand.NotifyCanExecuteChanged();
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
        NotifyKeyGroupNavigationCommands();
    }

    private void DeleteKeyGroup()
    {
        if (SelectedKeyGroup is null)
        {
            return;
        }

        _repository.DeleteKeyGroup(SelectedKeyGroup.Id);
        KeyGroups.Remove(SelectedKeyGroup);
        SelectedKeyGroup = KeyGroups.Count > 0 ? KeyGroups[0] : null;
        NotifyKeyGroupNavigationCommands();
    }

    /// <summary>Cycles SelectedKeyGroup by +/-1 within KeyGroups, wrapping around at either end -- backs the
    /// banner's &lt;/&gt; navigation now that the standalone Key Group dropdown is gone.</summary>
    private void StepKeyGroup(int direction)
    {
        if (KeyGroups.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedKeyGroup is null ? -1 : KeyGroups.IndexOf(SelectedKeyGroup);
        var nextIndex = ((currentIndex + direction) % KeyGroups.Count + KeyGroups.Count) % KeyGroups.Count;
        SelectedKeyGroup = KeyGroups[nextIndex];
    }

    private bool CanMoveKeyGroup(int direction)
    {
        if (SelectedKeyGroup is null)
        {
            return false;
        }

        var currentIndex = KeyGroups.IndexOf(SelectedKeyGroup);
        var targetIndex = currentIndex + direction;
        return targetIndex >= 0 && targetIndex < KeyGroups.Count;
    }

    /// <summary>Swaps SelectedKeyGroup's "Order" with its neighbor in the given direction and persists both
    /// -- GetKeyGroups sorts by "Order", so this is what actually changes the group's position (the banner's
    /// &lt;/&gt; arrows just browse the current order, they don't change it).</summary>
    private void MoveKeyGroup(int direction)
    {
        if (SelectedKeyGroup is null)
        {
            return;
        }

        var currentIndex = KeyGroups.IndexOf(SelectedKeyGroup);
        var targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= KeyGroups.Count)
        {
            return;
        }

        var current = KeyGroups[currentIndex];
        var neighbor = KeyGroups[targetIndex];

        (current.Order, neighbor.Order) = (neighbor.Order, current.Order);
        _repository.UpdateKeyGroup(current);
        _repository.UpdateKeyGroup(neighbor);

        KeyGroups.Move(currentIndex, targetIndex);
        NotifyKeyGroupNavigationCommands();
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
