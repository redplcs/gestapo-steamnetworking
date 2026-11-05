using System.Net;

namespace Gestapo.SteamNetworking.Sockets;

internal interface INetworkAdapter : IDisposable
{
    IPAddress Address { get; }

    IAsyncEnumerable<Packet> ReceiveAllAsync(CancellationToken cancellationToken = default);

    // data is only borrowed for the call: an adapter copies what it keeps.
    void Send(IPAddress to, ReadOnlySpan<byte> data);
}

internal readonly record struct Packet(IPAddress From, ReadOnlyMemory<byte> Data);
