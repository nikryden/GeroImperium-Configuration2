using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace GeroImperium.Core.Ble;

/// <summary>Retries a GATT call through the transient failures Windows exhibits in the few seconds after a
/// fresh BLE connection while it finishes its own link/HID-bond negotiation (doc/windows_app_api_guide.md
/// Part 2's Retry gotcha). Two distinct failure shapes both need covering, both hardware-confirmed on this
/// device: a thrown COMException (the guide's documented case, 6 attempts/2s apart, per
/// GeroImperium.WifiProvisionCli), and -- observed here on 2026-09-06 re-subscribing AppLaunchService and
/// re-discovering its characteristic right after a fresh reconnect in the same process, repeatably, not just
/// once -- a call that returns normally with a non-Success GattCommunicationStatus instead of throwing at
/// all. A retry loop keyed only on catching COMException silently gives up after one attempt against the
/// second shape, so every call here is retried on both.</summary>
public static class BleRetry
{
    /// <summary>General form: getStatus extracts the GattCommunicationStatus from whatever result shape the
    /// operation returns (GattDeviceServicesResult, GattCharacteristicsResult, GattReadResult, ...).</summary>
    public static async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> operation,
        Func<TResult, GattCommunicationStatus> getStatus, int maxAttempts = 6, CancellationToken ct = default)
    {
        TResult result = default!;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                result = await operation().ConfigureAwait(false);
                if (getStatus(result) == GattCommunicationStatus.Success)
                {
                    return result;
                }
            }
            catch (COMException)
            {
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        return result; // last attempt's result (whatever its status), or default if every attempt threw
    }

    /// <summary>Convenience form for the handful of calls (WriteValueAsync,
    /// WriteClientCharacteristicConfigurationDescriptorAsync) that return GattCommunicationStatus directly
    /// rather than wrapping it in a result object.</summary>
    public static Task<GattCommunicationStatus> RunAsync(Func<Task<GattCommunicationStatus>> operation,
        int maxAttempts = 6, CancellationToken ct = default) =>
        RunAsync(operation, status => status, maxAttempts, ct);
}
