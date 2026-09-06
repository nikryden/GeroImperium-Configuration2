using System.Management;

namespace GeroImperium.Core.Protocol;

public sealed class DevicePresenceChangedEventArgs(bool isConnected, IReadOnlyList<string> ports) : EventArgs
{
    public bool IsConnected { get; } = isConnected;
    public IReadOnlyList<string> Ports { get; } = ports;
}

/// <summary>
/// Watches for the device's CDC port appearing/disappearing: a WMI ManagementEventWatcher on
/// Win32_DeviceChangeEvent (near-instant) with a 2s poll fallback in case WMI events are missed -- both
/// funnel into one PresenceChanged event, per pc_app_plan.md "Device transport (Core/Protocol)". This is the
/// shared signal the Sync Service's tray icon consumes. Not unit-testable without real hardware, same as
/// DevicePortLocator, which this polls.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly System.Timers.Timer _pollTimer;
    private readonly object _lock = new();
    private ManagementEventWatcher? _wmiWatcher;
    private IReadOnlyList<string> _lastKnownPorts = [];

    public event EventHandler<DevicePresenceChangedEventArgs>? PresenceChanged;

    public DeviceWatcher()
    {
        _pollTimer = new System.Timers.Timer(PollInterval.TotalMilliseconds) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => CheckAndRaise();
    }

    public void Start()
    {
        CheckAndRaise(); // establish baseline immediately, don't wait for the first poll/event

        try
        {
            _wmiWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent"));
            _wmiWatcher.EventArrived += (_, _) => CheckAndRaise();
            _wmiWatcher.Start();
        }
        catch (ManagementException)
        {
            _wmiWatcher = null; // WMI eventing unavailable -- the poll fallback below still covers us
        }

        _pollTimer.Start();
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _wmiWatcher?.Stop();
    }

    private void CheckAndRaise()
    {
        lock (_lock)
        {
            var currentPorts = DevicePortLocator.FindDevicePorts();
            if (currentPorts.SequenceEqual(_lastKnownPorts))
            {
                return;
            }

            _lastKnownPorts = currentPorts;
            PresenceChanged?.Invoke(this, new DevicePresenceChangedEventArgs(currentPorts.Count > 0, currentPorts));
        }
    }

    public void Dispose()
    {
        Stop();
        _pollTimer.Dispose();
        _wmiWatcher?.Dispose();
    }
}
