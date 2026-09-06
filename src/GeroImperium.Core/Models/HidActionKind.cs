namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's KeyActions.Type column (doc/windows_app_api_guide.md) -- not to be confused with the
/// app-only ActionType enum (Shortcut/LaunchApp/Script). This is the wire-level distinction the device itself
/// understands: a chord-syntax string sent as HID keystrokes, or raw text typed character-by-character.
/// </summary>
public enum HidActionKind
{
    Hid = 0,
    RawText = 1,
}
