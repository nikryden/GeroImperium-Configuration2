namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's Applications table. Device reads Id, ImageDataRgb565 (preferred), ImageData (fallback).
/// Name and BackgroundColorArgb are app-only columns the device ignores.
/// </summary>
public sealed class Application
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[]? ImageData { get; set; }
    public byte[]? ImageDataRgb565 { get; set; }
    public int? BackgroundColorArgb { get; set; }
}
