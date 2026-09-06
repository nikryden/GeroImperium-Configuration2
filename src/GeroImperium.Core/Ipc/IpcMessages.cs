namespace GeroImperium.Core.Ipc;

public enum IpcRequestKind
{
    GetStatus,
    RequestBleForProvisioning,
    ReleaseBleAfterProvisioning,
}

/// <summary>Request sent from the App to the Sync Service over the named pipe. All three kinds carry no extra
/// fields -- the CDC-era per-command payloads (image bytes, shortcut tokens, the whole-database bulk upload)
/// are gone now that REST CRUD goes straight from the App to the device over HTTP (doc/plan2.md's IPC
/// redesign). The Service's only remaining job is owning the device's one BLE connection slot and handing it
/// to the App for provisioning on request.</summary>
public sealed class IpcRequest
{
    public IpcRequestKind Kind { get; set; }
}

public enum IpcResponseKind
{
    Status,
    AppLaunchEvent,
    CommandResult,
    Error,
}

/// <summary>Response from the Service. Status/CommandResult/Error each answer one specific request.
/// AppLaunchEvent is unsolicited -- pushed the instant the device's App-Launch characteristic notifies,
/// independent of whatever request (if any) is currently in flight on the same pipe. IpcDeviceClient's read
/// loop demultiplexes on Kind rather than treating every incoming message as "the" response.</summary>
public sealed class IpcResponse
{
    public IpcResponseKind Kind { get; set; }

    // Status
    public bool BleConnected { get; set; }
    public bool AppLaunchSubscribed { get; set; }

    // AppLaunchEvent -- the pressed app's real applications.Id (device-side), not a page-relative position.
    public int? AppId { get; set; }

    // CommandResult / Error
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
