namespace Gestapo.SteamNetworking.Sockets;

// Moves packets between virtual addresses: uints in network byte order, as in sockaddr_in.
internal interface ITransport : IDisposable
{
    uint Address { get; }

    event Action<uint, ReadOnlySpan<byte>>? Received;

    // 255.255.255.255 sends to every instance, this one included.
    void Send(uint to, ReadOnlySpan<byte> packet);
}
