using System.Runtime.InteropServices;

namespace GeroImperium.Core.Ble;

/// <summary>Retries a GATT call through the transient COMException Windows throws in the few seconds after a
/// fresh BLE connection while it finishes its own link/HID-bond negotiation (doc/windows_app_api_guide.md
/// Part 2's Retry gotcha) -- proven necessary on real hardware in GeroImperium.WifiProvisionCli (6 attempts,
/// 2s apart), copied verbatim rather than re-derived.</summary>
public static class BleRetry
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, int maxAttempts = 6, CancellationToken ct = default)
    {
        for (int attempt = 1; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (COMException)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        return await operation().ConfigureAwait(false); // last attempt: let any exception propagate
    }
}
