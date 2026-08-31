using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GeroImperium.App.Converters;
using GeroImperium.Core.Data;
using GeroImperium.Core.Imaging;
using SixLabors.ImageSharp.PixelFormats;
using CoreApplication = GeroImperium.Core.Models.Application;

namespace GeroImperium.App.ViewModels;

/// <summary>One row in the Applications page grid. Every property setter persists immediately -- this is a
/// local authoring DB, not a device write, so there's no reason to batch behind an explicit Save button.</summary>
public sealed partial class ApplicationItemViewModel : ObservableObject
{
    private readonly GeroImperiumRepository _repository;
    private readonly CoreApplication _model;

    public long Id => _model.Id;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private ImageSource? _imagePreview;

    [ObservableProperty]
    private Color? _backgroundColor;

    public ApplicationItemViewModel(CoreApplication model, GeroImperiumRepository repository)
    {
        _model = model;
        _repository = repository;
        _name = model.Name;
        _backgroundColor = ArgbToColor(model.BackgroundColorArgb);
        _imagePreview = ImageBytesConverter.ToImageSource(model.ImageData);
    }

    partial void OnNameChanged(string value)
    {
        _model.Name = value;
        _repository.UpdateApplication(_model);
    }

    partial void OnBackgroundColorChanged(Color? value)
    {
        _model.BackgroundColorArgb = ColorToArgb(value);

        if (_model.SourceImageData is not null)
        {
            ReconvertImage();
        }
        else
        {
            _repository.UpdateApplication(_model);
        }
    }

    public void SetImageFromFile(string path)
    {
        _model.SourceImageData = File.ReadAllBytes(path);
        ReconvertImage();
    }

    /// <summary>Re-runs the composite from SourceImageData -- re-compositing the already-flattened preview
    /// PNG would be a no-op, since it has no transparency left for a new background to show through.</summary>
    private void ReconvertImage()
    {
        var background = BackgroundColor ?? Colors.Black;
        var (previewPng, rgb565) = ImagePipeline.ConvertToPreviewAndRgb565(
            _model.SourceImageData!, new Rgba32(background.R, background.G, background.B, background.A));

        _model.ImageData = previewPng;
        _model.ImageDataRgb565 = rgb565;
        _repository.UpdateApplication(_model);

        ImagePreview = ImageBytesConverter.ToImageSource(previewPng);
    }

    internal static Color? ArgbToColor(int? argb) => argb is int value
        ? Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value)
        : null;

    internal static int? ColorToArgb(Color? color) => color is Color c
        ? unchecked((int)(((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B))
        : null;
}
