namespace GeroImperium.Core.Ipc;

/// <summary>Named pipe the Sync Service listens on and the App connects to, so only the Service ever opens
/// the device's CDC port directly -- see pc_app_plan.md "Both App and Service open the CDC port...".</summary>
public static class IpcPipeName
{
    public const string PipeName = "GeroImperiumSyncService";
}
