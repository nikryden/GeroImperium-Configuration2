using System.IO.Ports;

namespace GeroImperium.Core.Protocol;

/// <summary>
/// Owns an open SerialPort to the device and the GeroImperiumProtocolClient wrapping its stream. Baud rate
/// is left at the SerialPort default -- it's a USB CDC virtual port, so any value is ignored (see
/// pc_app_integration.md "Connectivity"). Not unit-testable without real hardware, same as DevicePortLocator.
/// </summary>
public sealed class DeviceConnection : IDisposable
{
    private readonly SerialPort _port;

    public string PortName { get; }
    public GeroImperiumProtocolClient Client { get; }

    private DeviceConnection(SerialPort port, GeroImperiumProtocolClient client)
    {
        _port = port;
        Client = client;
        PortName = port.PortName;
    }

    public static DeviceConnection Open(string portName)
    {
        var port = new SerialPort(portName);
        port.Open();
        return new DeviceConnection(port, new GeroImperiumProtocolClient(port.BaseStream));
    }

    public void Dispose()
    {
        Client.Dispose();
        _port.Dispose();
    }
}
