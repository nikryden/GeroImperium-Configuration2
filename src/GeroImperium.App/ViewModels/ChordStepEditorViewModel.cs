using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using GeroImperium.Core.Http;

namespace GeroImperium.App.ViewModels;

/// <summary>One step of a chord sequence being built in the UI (see KeySlotViewModel's chord editor) --
/// compiles to a ChordSyntax.ChordStep. A step with no SelectedKey compiles to nothing: that's how an
/// in-progress step (a modifier checked but no key chosen yet) avoids being written to the database as a
/// dangling modifier -- see KeySlotViewModel.PersistAction, which filters on HasKey before building.</summary>
public sealed partial class ChordStepEditorViewModel : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    private bool _ctrl;

    [ObservableProperty]
    private bool _shift;

    [ObservableProperty]
    private bool _alt;

    [ObservableProperty]
    private bool _win;

    [ObservableProperty]
    private string? _selectedKey;

    [ObservableProperty]
    private string _keySearchText = string.Empty;

    /// <summary>Own ListCollectionView per step (not CollectionViewSource.GetDefaultView(ChordSyntax.KnownBareKeys),
    /// which would hand back the same shared view -- and therefore the same filter -- to every step editor since
    /// WPF caches default views per source instance).</summary>
    public ICollectionView FilteredKeys { get; }

    public bool HasKey => !string.IsNullOrEmpty(SelectedKey);

    public ChordStepEditorViewModel(Action onChanged)
    {
        _onChanged = onChanged;
        FilteredKeys = new ListCollectionView(ChordSyntax.KnownBareKeys.ToList())
        {
            Filter = o => string.IsNullOrEmpty(KeySearchText)
                || ((string)o!).Contains(KeySearchText, StringComparison.OrdinalIgnoreCase),
        };
    }

    public ChordStep ToChordStep() =>
        new(Ctrl, Shift, Alt, Win, string.IsNullOrEmpty(SelectedKey) ? [] : [SelectedKey]);

    partial void OnCtrlChanged(bool value) => _onChanged();

    partial void OnShiftChanged(bool value) => _onChanged();

    partial void OnAltChanged(bool value) => _onChanged();

    partial void OnWinChanged(bool value) => _onChanged();

    partial void OnSelectedKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(HasKey));
        _onChanged();
    }

    partial void OnKeySearchTextChanged(string value) => FilteredKeys.Refresh();
}
