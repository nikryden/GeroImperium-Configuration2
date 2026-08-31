namespace GeroImperium.Core.Protocol;

/// <summary>
/// Bitmask for the 'T' command's modifiers byte -- see pc_app_integration.md "T -- set or clear a key's shortcut".
/// </summary>
[Flags]
public enum ShortcutModifiers : byte
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Win = 1 << 3,
}
