using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty]
    private string? _shortcutToken;

    [ObservableProperty]
    private bool _ctrlModifier;

    [ObservableProperty]
    private bool _shiftModifier;

    [ObservableProperty]
    private bool _altModifier;

    [ObservableProperty]
    private bool _winModifier;

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

        // TextContent is a chord-syntax string (see ChordSyntax) -- this editor only exposes a single step
        // (one key + 4 modifier checkboxes) today, so only the first parsed step round-trips into the UI.
        if (action is { Type: HidActionKind.Hid, TextContent.Length: > 0 }
            && ChordSyntax.TryParse(action.TextContent, out var steps) && steps.Count > 0)
        {
            var step = steps[0];
            _ctrlModifier = step.Ctrl;
            _shiftModifier = step.Shift;
            _altModifier = step.Alt;
            _winModifier = step.Win;
            _shortcutToken = step.Keys.Count > 0 ? step.Keys[0] : null;
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

    partial void OnShortcutTokenChanged(string? value) => PersistAction();

    partial void OnCtrlModifierChanged(bool value) => PersistAction();

    partial void OnShiftModifierChanged(bool value) => PersistAction();

    partial void OnAltModifierChanged(bool value) => PersistAction();

    partial void OnWinModifierChanged(bool value) => PersistAction();

    partial void OnLaunchPathChanged(string? value) => PersistAction();

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

        string? textContent = null;
        if (ActionType == ActionType.Shortcut && !string.IsNullOrEmpty(ShortcutToken))
        {
            var step = new ChordStep(CtrlModifier, ShiftModifier, AltModifier, WinModifier, [ShortcutToken]);
            textContent = ChordSyntax.Build([step]);
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
}
