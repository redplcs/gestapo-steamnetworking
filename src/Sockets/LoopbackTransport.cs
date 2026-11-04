using System.Net;
using System.Net.Sockets;

namespace Gestapo.SteamNetworking.Sockets;

// Carries packets between game instances on this machine over UDP on 127.0.0.1, so the virtual
// network can be tested without Steam. Instance N listens on BasePort + N and owns 10.0.0.N.
internal sealed class LoopbackTransport : ITransport
{
    private const int BasePort = 47000;
    private const int MaxInstances = 8;
    private const uint Broadcast = 0xFFFFFFFF;

    private static readonly IPEndPoint[] s_endPoints =
        [.. Enumerable.Range(0, MaxInstances + 1).Select(n => new IPEndPoint(IPAddress.Loopback, BasePort + n))];

    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
    {
        ExclusiveAddressUse = true
    };

    private volatile bool _closed;

    internal LoopbackTransport()
    {
        var instance = 1;

        while (true)
        {
            try
            {
                _socket.Bind(s_endPoints[instance]);
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse && instance < MaxInstances)
            {
                instance++;
            }
        }

        Address = AddressOf(instance);
        new Thread(Receive) { IsBackground = true, Name = nameof(LoopbackTransport) }.Start();
        Logger.WriteLine($"loopback transport: instance {instance}, address {new IPAddress(Address)}");
        ReleaseInstanceMutex();
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
                for (; count < 8; count++)
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (ApplicationException)
            {
            }

            Logger.WriteLine($"loopback transport: released the single instance mutex {count} times");
        }
        catch (Exception e)
        {
            Logger.WriteLine($"loopback transport: can't release the single instance mutex: {e.Message}");
        }
    }

    public event Action<uint, ReadOnlySpan<byte>>? Received;

    public uint Address { get; }

    public void Dispose()
    {
        _closed = true;
        _socket.Dispose();
    }

    public void Send(uint to, ReadOnlySpan<byte> packet)
    {
        if (to == Broadcast)
        {
            for (var instance = 1; instance <= MaxInstances; instance++)
            {
                SendTo(instance, packet);
            }

            return;
        }

        var target = InstanceOf(to);

        if (target is < 1 or > MaxInstances)
        {
            Logger.WriteLine($"loopback transport: no route to {new IPAddress(to)}");
            return;
        }

        SendTo(target, packet);
    }

    private void SendTo(int instance, ReadOnlySpan<byte> packet)
    {
        try
        {
            _socket.SendTo(packet, SocketFlags.None, s_endPoints[instance]);
        }
        catch (Exception e) when (!_closed)
        {
            Logger.WriteLine($"loopback transport: send to instance {instance} failed: {e.Message}");
        }
        catch (Exception)
        {
        }
    }

    private void Receive()
    {
        var buffer = new byte[65536];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            try
            {
                var length = _socket.ReceiveFrom(buffer, ref from);
                var instance = ((IPEndPoint)from).Port - BasePort;

                if (instance is >= 1 and <= MaxInstances)
                {
                    Received?.Invoke(AddressOf(instance), buffer.AsSpan(0, length));
                }
            }
            catch (Exception) when (_closed)
            {
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable left by a send to an instance that isn't running.
            }
            catch (Exception e)
            {
                Logger.WriteLine($"loopback transport: receive failed: {e}");
            }
        }
    }

    // 10.0.0.N in network byte order.
    private static uint AddressOf(int instance) => (uint)instance << 24 | 0x0A;

    private static int InstanceOf(uint address) => (address & 0x00FFFFFF) == 0x0A ? (int)(address >> 24) : 0;
}
