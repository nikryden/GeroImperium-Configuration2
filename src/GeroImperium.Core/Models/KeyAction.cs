namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's KeyActions table. Device reads Type (HidActionKind) and TextContent, and only
/// interprets them for ActionType.Shortcut, since firmware today executes shortcuts only (see
/// pc_app_plan.md "Action types and the firmware gap"). ActionType, LaunchPath, ScriptId are app-only, as is
/// RemoteId (the row's Id on the device once pushed -- see doc/plan2.md's diff-and-push sync model).
/// TextContent is the chord-syntax string described in doc/windows_app_api_guide.md -- built/parsed via
/// GeroImperium.Core.Http.ChordSyntax, not hand-assembled.
/// </summary>
public sealed class KeyAction
{
    public long Id { get; set; }
    public HidActionKind Type { get; set; } = HidActionKind.Hid;
    public string? TextContent { get; set; }
    public ActionType ActionType { get; set; } = ActionType.Shortcut;
    public string? LaunchPath { get; set; }
    public long? ScriptId { get; set; }
    public long? RemoteId { get; set; }

    /// <summary>See ApplicationPage.Dirty's doc comment. In practice always false once RemoteId is set --
    /// KeyActions are immutable post-creation (UpsertAction dedups instead of mutating), so there's no edit
    /// path that could flip this back to true after the initial push.</summary>
    public bool Dirty { get; set; } = true;
}
