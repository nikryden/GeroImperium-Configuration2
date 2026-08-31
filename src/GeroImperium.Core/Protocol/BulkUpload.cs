namespace GeroImperium.Core.Protocol;

public enum BulkUploadStage
{
    Starting,
    Uploading,
    Finalizing,
}

public readonly record struct BulkUploadProgress(BulkUploadStage Stage, int BytesSent, int TotalBytes);

/// <summary>
/// Outcome of a bulk S/D/F upload. On success the device has already restarted -- see
/// pc_app_integration.md "Bulk upload: S / D / F", step 3 (Finalize).
/// </summary>
public sealed class BulkUploadResult
{
    public bool Success { get; }
    public string? ErrorMessage { get; }

    private BulkUploadResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public static BulkUploadResult RebootingSuccess() => new(true, null);

    public static BulkUploadResult Rejected(string message) => new(false, message);
}
