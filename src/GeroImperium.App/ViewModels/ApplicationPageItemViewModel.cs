using CommunityToolkit.Mvvm.ComponentModel;
using GeroImperium.Core.Data;
using GeroImperium.Core.Models;

namespace GeroImperium.App.ViewModels;

/// <summary>One entry in the Applications page's page selector. Every property setter persists immediately,
/// same convention as ApplicationItemViewModel -- this is a local authoring DB, no explicit Save button.</summary>
public sealed partial class ApplicationPageItemViewModel : ObservableObject
{
    private readonly GeroImperiumRepository _repository;
    private readonly ApplicationPage _model;

    public long Id => _model.Id;
    internal string Order => _model.Order;

    [ObservableProperty]
    private string _name;

    public ApplicationPageItemViewModel(ApplicationPage model, GeroImperiumRepository repository)
    {
        _model = model;
        _repository = repository;
        _name = model.Name;
    }

    partial void OnNameChanged(string value)
    {
        _model.Name = value;
        _repository.UpdateApplicationPage(_model);
    }

    /// <summary>Swaps position with a neighboring page -- used by the Move Earlier/Later commands.</summary>
    internal void SetOrder(string order)
    {
        _model.Order = order;
        _repository.UpdateApplicationPage(_model);
    }
}
