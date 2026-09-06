using GeroImperium.Core.Protocol;

namespace GeroImperium.Service;

/// <summary>Owns the live DeviceConnection, opening/closing it as DeviceWatcher reports the CDC port
/// appearing/disappearing. This is the single owner of the port -- the App never opens it directly, it talks
/// to whatever this class currently holds via the named pipe (see PipeServer).</summary>
public sealed class DeviceConnectionManager : IDisposable
{
    private readonly DeviceWatcher _watcher;
    private DeviceConnection? _connection;

    public bool IsConnected => _connection is not null;
    public string? PortName => _connection?.PortName;
    public GeroImperiumProtocolClient? Client => _connection?.Client;

    public event EventHandler? StatusChanged;

    public DeviceConnectionManager(DeviceWatcher watcher)
    {
        _watcher = watcher;
        _watcher.PresenceChanged += OnPresenceChanged;
    }

    private void OnPresenceChanged(object? sender, DevicePresenceChangedEventArgs e)
    {
        _connection?.Dispose();
        _connection = null;

        if (e.IsConnected)
        {
            try
            {
                _connection = DeviceConnection.Open(e.Ports[0]);
            }
            catch (Exception)
            {
                // Port can be transiently busy right after enumeration (e.g. Windows still finishing
                // driver setup) -- next presence tick (poll fallback, at worst 2s) retries.
                _connection = null;
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _watcher.PresenceChanged -= OnPresenceChanged;
        _connection?.Dispose();
    }
}
