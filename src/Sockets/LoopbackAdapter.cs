using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Gestapo.SteamNetworking.Sockets;

// Carries packets between game instances on this machine over UDP on 127.0.0.1, so the virtual
// network can be tested without Steam. Instance N listens on BasePort + N and owns 10.0.0.N.
internal sealed class LoopbackAdapter : INetworkAdapter
{
    private const int BasePort = 47000;
    private const int MaxInstances = 8;

    private readonly UdpClient _client = new();
    private volatile bool _closed;

    internal LoopbackAdapter()
    {
        var instance = 1;

        while (true)
        {
            try
            {
                _client.Client.Bind(EndPointOf(instance));
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse && instance < MaxInstances)
            {
                instance++;
            }
        }

        Address = AddressOf(instance);
        Log.Information("Loopback adapter started as instance {Instance} at {Address}", instance, Address);
        ReleaseInstanceMutex();
    }

    public IPAddress Address { get; }

    public void Dispose()
    {
        _closed = true;
        _client.Dispose();
    }

    public async IAsyncEnumerable<Packet> ReceiveAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            UdpReceiveResult result;

            try
            {
                result = await _client.ReceiveAsync(cancellationToken);
            }
            catch (Exception) when (_closed)
            {
                yield break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable left by a send to an instance that isn't running.
                continue;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Error(e, "Loopback receive failed");
                continue;
            }

            var instance = result.RemoteEndPoint.Port - BasePort;

            if (instance is >= 1 and <= MaxInstances)
            {
                yield return new Packet(AddressOf(instance), result.Buffer);
            }
        }
    }

    public void Send(IPAddress to, ReadOnlySpan<byte> data)
    {
        if (to.Equals(IPAddress.Broadcast))
        {
            for (var instance = 1; instance <= MaxInstances; instance++)
            {
                SendTo(instance, data);
            }
        }
        else if (to.GetAddressBytes() is [10, 0, 0, >= 1 and <= MaxInstances and var instance])
        {
            SendTo(instance, data);
        }
        else
        {
            Log.Warning("No loopback instance at {Address}", to);
        }
    }

    private void SendTo(int instance, ReadOnlySpan<byte> data)
    {
        try
        {
            _client.Send(data, EndPointOf(instance));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Sending to loopback instance {Instance} failed", instance);
        }
    }

    // WinMain takes this mutex for good and quits if it's already held, so only one instance runs.
    // We are called on the main thread, which owns it, and releasing it lets the next instance start.
    // WinMain acquires it twice (CreateMutexA with bInitialOwner, then WaitForSingleObject), so release
    // until we no longer own it.
    private static void ReleaseInstanceMutex()
    {
        try
        {
            using var mutex = Mutex.OpenExisting("CommXPC Pyro Studios 2005");
            var count = 0;

            try
            {
                for (; ; count++)
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
            }

            Log.Information("Released the game's single instance mutex {Count} times", count);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not release the game's single instance mutex");
        }
    }

    private static IPAddress AddressOf(int instance) => new([10, 0, 0, (byte)instance]);

    private static IPEndPoint EndPointOf(int instance) => new(IPAddress.Loopback, BasePort + instance);
}
