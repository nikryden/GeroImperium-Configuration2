namespace GeroImperium.Core.Models;

/// <summary>
/// App-only table -- script bodies never cross the wire to the device. The firmware only ever reports
/// "ActionType.Script fired for this key"; the Service resolves KeyAction.ScriptId -> Script.Body locally.
/// </summary>
public sealed class Script
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Interpreter { get; set; } = "PowerShell";
}
