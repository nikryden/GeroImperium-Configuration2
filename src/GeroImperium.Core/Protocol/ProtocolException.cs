namespace GeroImperium.Core.Protocol;

/// <summary>
/// The device sent a byte the wire protocol does not allow at this point (e.g. anything but 'K'/'E' after a
/// command, or a 'K' after 'F' -- which the protocol doc states never replies on success).
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }
}
