using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.App.Converters;
using GeroImperium.Core.Data;
using GeroImperium.Core.Http;
using GeroImperium.Core.Imaging;
using GeroImperium.Core.Models;
using SixLabors.ImageSharp.PixelFormats;

namespace GeroImperium.App.ViewModels;

/// <summary>One of a KeyGroup's 6 fixed slots (positions 0-5, matching the physical GPA0-GPA5 layout).</summary>
public sealed partial class KeySlotViewModel : ObservableObject
{
    private readonly GeroImperiumRepository _repository;
    private readonly GeroImperiumKey _model;
    private bool _isLoading;

    public int Position => _model.Position;

    public IReadOnlyList<ActionType> ActionTypeOptions { get; } = Enum.GetValues<ActionType>();

    [ObservableProperty]
    private ImageSource? _imagePreview;

    [ObservableProperty]
    private Color? _backgroundColor;

    [ObservableProperty]
    private ActionType _actionType;

    /// <summary>The chord sequence being edited, one entry per step (see ChordSyntax's step grammar --
    /// "[ctrl]+k[ctrl]+l" is two steps). Always has at least one entry so there's a row to fill in; a step
    /// with no key selected compiles to nothing (see PersistAction) rather than being saved.</summary>
    public ObservableCollection<ChordStepEditorViewModel> ChordSteps { get; } = [];

    /// <summary>Human-readable readout of what ChordSteps currently compiles to (e.g. "Ctrl+K, Ctrl+C"), so
    /// the user can see the sequence they're building without reading raw TextContent syntax.</summary>
    public string ChordPreview => BuildChordPreview();

    [ObservableProperty]
    private string? _launchPath;

    /// <summary>LaunchApp/Script require a firmware update and the Sync Service running -- see
    /// pc_app_plan.md "Action types and the firmware gap". Shown, not hidden, but flagged.</summary>
    public bool IsUnsupportedActionType => ActionType != ActionType.Shortcut;

    public KeySlotViewModel(GeroImperiumKey model, GeroImperiumRepository repository)
    {
        _model = model;
        _repository = repository;

        _isLoading = true;
        _imagePreview = ImageBytesConverter.ToImageSource(model.ImageData);
        _backgroundColor = ApplicationItemViewModel.ArgbToColor(model.BackgroundColorArgb);

        var action = model.KeyActionId is long id ? repository.GetKeyAction(id) : null;
        _actionType = action?.ActionType ?? ActionType.Shortcut;
        _launchPath = action?.LaunchPath;

        // TextContent is a chord-syntax string (see ChordSyntax) -- every parsed step round-trips into its
        // own ChordStepEditorViewModel so multi-step sequences ("[ctrl]+k[ctrl]+l") are editable, not just
        // the first step.
        if (action is { Type: HidActionKind.Hid, TextContent.Length: > 0 }
            && ChordSyntax.TryParse(action.TextContent, out var steps))
        {
            foreach (var step in steps)
            {
                ChordSteps.Add(new ChordStepEditorViewModel(OnChordStepChanged)
                {
                    Ctrl = step.Ctrl,
                    Shift = step.Shift,
                    Alt = step.Alt,
                    Win = step.Win,
                    SelectedKey = step.Keys.Count > 0 ? step.Keys[0] : null,
                });
            }
        }

        if (ChordSteps.Count == 0)
        {
            ChordSteps.Add(new ChordStepEditorViewModel(OnChordStepChanged));
        }

        _isLoading = false;
    }

    partial void OnBackgroundColorChanged(Color? value)
    {
        if (_isLoading)
        {
            return;
        }

        _model.BackgroundColorArgb = ApplicationItemViewModel.ColorToArgb(value);

        if (_model.SourceImageData is not null)
        {
            ReconvertImage();
        }
        else
        {
            _repository.UpdateKey(_model);
        }
    }

    partial void OnActionTypeChanged(ActionType value)
    {
        OnPropertyChanged(nameof(IsUnsupportedActionType));
        PersistAction();
    }

    partial void OnLaunchPathChanged(string? value) => PersistAction();

    [RelayCommand]
    private void AddChordStep() => ChordSteps.Add(new ChordStepEditorViewModel(OnChordStepChanged));

    [RelayCommand]
    private void RemoveChordStep(ChordStepEditorViewModel? step)
    {
        if (step is null || !ChordSteps.Remove(step))
        {
            return;
        }

        if (ChordSteps.Count == 0)
        {
            ChordSteps.Add(new ChordStepEditorViewModel(OnChordStepChanged));
        }

        PersistAction();
    }

    private void OnChordStepChanged()
    {
        OnPropertyChanged(nameof(ChordPreview));
        PersistAction();
    }

    public void SetImageFromFile(string path)
    {
        _model.SourceImageData = File.ReadAllBytes(path);
        ReconvertImage();
    }

    private void ReconvertImage()
    {
        var background = BackgroundColor ?? Colors.Black;
        var (previewPng, rgb565) = ImagePipeline.ConvertToPreviewAndRgb565(
            _model.SourceImageData!, new Rgba32(background.R, background.G, background.B, background.A));

        _model.ImageData = previewPng;
        _model.ImageDataRgb565 = rgb565;
        _model.ImageChangedAtUtc = DateTime.UtcNow;
        _repository.UpdateKey(_model);

        ImagePreview = ImageBytesConverter.ToImageSource(previewPng);
    }

    private void PersistAction()
    {
        if (_isLoading)
        {
            return;
        }

        // Steps with no key selected (a modifier checked but nothing chosen yet, or a freshly-added blank
        // row) are dropped here rather than saved -- this is what keeps an in-progress/trailing modifier
        // out of the database.
        string? textContent = null;
        if (ActionType == ActionType.Shortcut)
        {
            var completeSteps = ChordSteps.Where(s => s.HasKey).Select(s => s.ToChordStep()).ToList();
            textContent = completeSteps.Count > 0 ? ChordSyntax.Build(completeSteps) : null;
        }

        var action = _repository.UpsertAction(
            ActionType,
            HidActionKind.Hid,
            textContent,
            ActionType == ActionType.LaunchApp ? LaunchPath : null,
            scriptId: null);

        _model.KeyActionId = action.Id;
        _repository.UpdateKey(_model);
    }

    private string BuildChordPreview()
    {
        var stepLabels = ChordSteps.Where(s => s.HasKey).Select(s =>
        {
            var parts = new List<string>();
            if (s.Ctrl) parts.Add("Ctrl");
            if (s.Shift) parts.Add("Shift");
            if (s.Alt) parts.Add("Alt");
            if (s.Win) parts.Add("Win");
            parts.Add(s.SelectedKey!);
            return string.Join("+", parts);
        }).ToList();

        return stepLabels.Count > 0 ? string.Join(", ", stepLabels) : "(no shortcut set)";
    }
}
