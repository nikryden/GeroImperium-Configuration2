namespace GeroImperium.Core.Models;

/// <summary>
/// Not yet supported by firmware for LaunchApp/Script -- see pc_app_plan.md "Action types and the firmware gap".
/// </summary>
public enum ActionType
{
    Shortcut = 0,
    LaunchApp = 1,
    Script = 2,
}
