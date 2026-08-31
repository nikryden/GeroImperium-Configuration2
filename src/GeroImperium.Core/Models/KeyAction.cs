namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's KeyActions table. Device reads ShortcutKey, CtrlModifier, AltModifier, ShiftModifier,
/// WinModifier -- and only interprets them for ActionType.Shortcut, since firmware today executes shortcuts only
/// (see pc_app_plan.md "Action types and the firmware gap"). ActionType, LaunchPath, ScriptId are app-only.
/// </summary>
public sealed class KeyAction
{
    public long Id { get; set; }
    public string? ShortcutKey { get; set; }
    public bool CtrlModifier { get; set; }
    public bool AltModifier { get; set; }
    public bool ShiftModifier { get; set; }
    public bool WinModifier { get; set; }
    public ActionType ActionType { get; set; } = ActionType.Shortcut;
    public string? LaunchPath { get; set; }
    public long? ScriptId { get; set; }
}
