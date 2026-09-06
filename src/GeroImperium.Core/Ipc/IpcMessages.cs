namespace GeroImperium.Core.Ipc;

public enum IpcRequestKind
{
    GetStatus,
    SetApplicationImage,
    SetKeyImage,
    SetShortcut,
    UploadDatabase,
}

/// <summary>Request sent from the App to the Sync Service over the named pipe. Fields are populated
/// per-Kind; unused fields stay null. Mirrors GeroImperiumProtocolClient's command surface (Core/Protocol) --
/// the Service is a thin relay to the device connection it owns.</summary>
public sealed class IpcRequest
{
    public IpcRequestKind Kind { get; set; }

    // SetApplicationImage
    public byte? AppIndex { get; set; }

    // SetKeyImage / SetShortcut
    public int? KeyGroupId { get; set; }
    public byte? Position { get; set; }

    // SetApplicationImage / SetKeyImage
    public byte[]? ImageRgb565 { get; set; }

    // SetShortcut
    public byte? Modifiers { get; set; }
    public string? ShortcutToken { get; set; }

    // UploadDatabase
    public byte[]? DatabaseBytes { get; set; }
}

public enum IpcResponseKind
{
    Status,
    Progress,
    CommandResult,
    Error,
}

/// <summary>Response from the Service. A single request can yield multiple Progress responses (bulk upload)
/// followed by exactly one terminal CommandResult/Error -- callers should keep reading until a non-Progress
/// kind arrives.</summary>
public sealed class IpcResponse
{
    public IpcResponseKind Kind { get; set; }

    // Status
    public bool IsConnected { get; set; }
    public string? PortName { get; set; }

    // Progress (mirrors Core.Protocol.BulkUploadProgress)
    public string? ProgressStage { get; set; }
    public int ProgressBytesSent { get; set; }
    public int ProgressTotalBytes { get; set; }

    // CommandResult / Error
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}
