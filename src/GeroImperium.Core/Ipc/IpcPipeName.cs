namespace GeroImperium.Core.Ipc;

/// <summary>Named pipe the Sync Service listens on and the App connects to -- the App borrows the Service's
/// BLE connection slot for provisioning through this pipe rather than opening its own competing connection
/// (see doc/plan2.md's "Why the Service still exists": NimBLE allows exactly one concurrent GATT connection).</summary>
public static class IpcPipeName
{
    public const string PipeName = "GeroImperiumSyncService";
}
