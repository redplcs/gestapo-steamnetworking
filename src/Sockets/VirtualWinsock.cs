using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Gestapo.SteamNetworking.Hooking;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.WinSock;

namespace Gestapo.SteamNetworking.Sockets;

// Moves the game's UDP sockets onto a virtual network. Real sockets are bound to an OS-assigned port
// and never do I/O: sends go to the network adapter, receives are completed from it, with the same
// overlapped semantics the game sees from Winsock. gethostbyname of our own host name returns only
// the virtual address, which is what peers find in our station URLs.
// These hooks cover all the game's network calls: its other ws2_32 imports work as they are on the
// real socket and events, and it imports nothing for TCP, so every socket here is UDP.
internal sealed class VirtualWinsock : IDisposable
{
    private const string Library = "ws2_32.dll";

    // Each adapter packet starts with the source and destination ports, in network byte order.
    private const int HeaderSize = 4;
    private const int MaxStackPacket = 2048;
    private const int MaxQueued = 256;

    // LANSessionDiscovery's port, g_nLANDiscoveryPort in the game.
    private const ushort DiscoveryPort = 9100;

    // NTSTATUS values the real provider leaves in OVERLAPPED.Internal.
    private const nint StatusPending = 0x103;
    private const nint StatusCancelled = unchecked((int)0xC0000120);

    private readonly INetworkAdapter _adapter;
    private readonly Dictionary<SOCKET, VirtualSocket> _sockets = [];
    private readonly Lock _lock = new();
    private readonly unsafe HostEntry* _host;
    private ushort _nextPort = 49152;

    internal unsafe VirtualWinsock(INetworkAdapter adapter)
    {
        _adapter = adapter;
        _host = HostEntry.Allocate(adapter.Address);

        List<IDisposable> hooks = [];

        try
        {
            hooks.Add(HookTarget.Import(Library, nameof(bind)).Replace<bind>(Bind));
            hooks.Add(HookTarget.Import(Library, nameof(getsockname)).Replace<getsockname>(GetSockName));
            hooks.Add(HookTarget.Import(Library, nameof(closesocket)).Replace<closesocket>(CloseSocket));
            hooks.Add(HookTarget.Import(Library, nameof(gethostbyname)).Replace<gethostbyname>(GetHostByName));
            hooks.Add(HookTarget.Import(Library, nameof(WSASendTo)).Replace<WSASendTo>(SendTo));
            hooks.Add(HookTarget.Import(Library, nameof(WSARecvFrom)).Replace<WSARecvFrom>(RecvFrom));
            hooks.Add(HookTarget.Import(Library, nameof(WSAGetOverlappedResult)).Replace<WSAGetOverlappedResult>(GetOverlappedResult));
        }
        catch
        {
            foreach (var hook in hooks)
            {
                hook.Dispose();
            }

            adapter.Dispose();
            throw;
        }

        Task.Run(ReceiveAsync);
    }

    // The hooks stay: the game closes its sockets after this, and CloseSocket completes their pending receives.
    public void Dispose() => _adapter.Dispose();

    private unsafe int Bind(SOCKET s, SOCKADDR* name, int namelen)
    {
        if (namelen < sizeof(SOCKADDR_IN) || name->sa_family != ADDRESS_FAMILY.AF_INET)
        {
            return PInvoke.bind(s, name, namelen);
        }

        var requested = *(SOCKADDR_IN*)name;
        var real = new SOCKADDR_IN { sin_family = ADDRESS_FAMILY.AF_INET };

        lock (_lock)
        {
            if (requested.sin_port == 0)
            {
                requested.sin_port = AllocatePort();
            }
            else if (FindByPort(requested.sin_port) is not null)
            {
                PInvoke.WSASetLastError((int)WSA_ERROR.WSAEADDRINUSE);
                return PInvoke.SOCKET_ERROR;
            }

            if (PInvoke.bind(s, (SOCKADDR*)&real, sizeof(SOCKADDR_IN)) != 0)
            {
                return PInvoke.SOCKET_ERROR;
            }

            _sockets[s] = new VirtualSocket { Address = requested };
        }

        Log.Information("Bound socket {Socket} to {EndPoint}", s, ToEndPoint(requested));
        return 0;
    }

    private unsafe int GetSockName(SOCKET s, SOCKADDR* name, int* namelen)
    {
        if (FindSocket(s) is not { } socket)
        {
            return PInvoke.getsockname(s, name, namelen);
        }

        if (*namelen < sizeof(SOCKADDR_IN))
        {
            PInvoke.WSASetLastError((int)WSA_ERROR.WSAEFAULT);
            return PInvoke.SOCKET_ERROR;
        }

        *(SOCKADDR_IN*)name = socket.Address;
        *namelen = sizeof(SOCKADDR_IN);
        return 0;
    }

    private unsafe int CloseSocket(SOCKET s)
    {
        lock (_lock)
        {
            // Like the real provider: closing aborts the pending receive and signals its event.
            if (_sockets.Remove(s, out var socket) && socket.Pending is { } receive)
            {
                Complete(receive.Overlapped, StatusCancelled, 0);
            }
        }

        return PInvoke.closesocket(s);
    }

    private unsafe HOSTENT* GetHostByName(PCSTR name)
    {
        if (!IsOwnHostName(name))
        {
            return PInvoke.gethostbyname(name);
        }

        Log.Information("Resolved own host name {Name} to {Address}", name, _adapter.Address);
        return &_host->Entry;
    }

    // The game passes one buffer and an event-based OVERLAPPED, without a completion routine.
    private unsafe int SendTo(
        SOCKET s,
        WSABUF* lpBuffers,
        uint dwBufferCount,
        uint* lpNumberOfBytesSent,
        uint dwFlags,
        SOCKADDR* lpTo,
        int iTolen,
        NativeOverlapped* lpOverlapped,
        delegate* unmanaged[Stdcall]<uint, uint, NativeOverlapped*, uint, void> lpCompletionRoutine)
    {
        var socket = FindSocket(s);

        if (socket is null || lpTo is null || iTolen < sizeof(SOCKADDR_IN))
        {
            return PInvoke.WSASendTo(s, lpBuffers, dwBufferCount, lpNumberOfBytesSent, dwFlags, lpTo, iTolen, lpOverlapped, lpCompletionRoutine);
        }

        var to = (SOCKADDR_IN*)lpTo;
        var data = new ReadOnlySpan<byte>(lpBuffers->buf.Value, (int)lpBuffers->len);
        var length = HeaderSize + data.Length;
        Span<byte> packet = length <= MaxStackPacket ? stackalloc byte[length] : new byte[length];

        MemoryMarshal.Write(packet, socket.Address.sin_port);
        MemoryMarshal.Write(packet[2..], to->sin_port);
        data.CopyTo(packet[HeaderSize..]);

        if (IsDiscovery(socket.Address.sin_port, to->sin_port))
        {
            Log.Debug("Sent {Length} discovery bytes from port {Port} to {To}", data.Length, Port(socket.Address.sin_port), ToEndPoint(*to));
        }

        _adapter.Send(new IPAddress(to->sin_addr.S_un.S_addr), packet);

        if (lpNumberOfBytesSent is not null)
        {
            *lpNumberOfBytesSent = (uint)data.Length;
        }

        Complete(lpOverlapped, 0, data.Length);
        return 0;
    }

    private unsafe int RecvFrom(
        SOCKET s,
        WSABUF* lpBuffers,
        uint dwBufferCount,
        uint* lpNumberOfBytesRecvd,
        uint* lpFlags,
        SOCKADDR* lpFrom,
        int* lpFromlen,
        NativeOverlapped* lpOverlapped,
        delegate* unmanaged[Stdcall]<uint, uint, NativeOverlapped*, uint, void> lpCompletionRoutine)
    {
        var receive = new PendingReceive
        {
            Buffer = *lpBuffers,
            From = lpFrom,
            FromLength = lpFromlen,
            Overlapped = lpOverlapped
        };

        lock (_lock)
        {
            if (!_sockets.TryGetValue(s, out var socket))
            {
                return PInvoke.WSARecvFrom(s, lpBuffers, dwBufferCount, lpNumberOfBytesRecvd, lpFlags, lpFrom, lpFromlen, lpOverlapped, lpCompletionRoutine);
            }

            if (socket.Queue.TryDequeue(out var datagram))
            {
                var length = Fill(receive, datagram);

                if (lpNumberOfBytesRecvd is not null)
                {
                    *lpNumberOfBytesRecvd = (uint)length;
                }

                if (lpFlags is not null)
                {
                    *lpFlags = 0;
                }

                Complete(lpOverlapped, 0, length);
                return 0;
            }

            socket.Pending = receive;

            if (lpOverlapped is not null)
            {
                lpOverlapped->InternalLow = StatusPending;
            }
        }

        // Last, so nothing in between can overwrite the error the game reads next.
        PInvoke.WSASetLastError((int)WSA_ERROR.WSA_IO_PENDING);
        return PInvoke.SOCKET_ERROR;
    }

    // ponytail: fWait is ignored, the game only asks after the request's event fired
    private unsafe BOOL GetOverlappedResult(SOCKET s, NativeOverlapped* lpOverlapped, uint* lpcbTransfer, BOOL fWait, uint* lpdwFlags)
    {
        nint status;

        lock (_lock)
        {
            if (!_sockets.ContainsKey(s))
            {
                return PInvoke.WSAGetOverlappedResult(s, lpOverlapped, lpcbTransfer, fWait, lpdwFlags);
            }

            status = lpOverlapped->InternalLow;
            *lpcbTransfer = (uint)lpOverlapped->InternalHigh;
        }

        *lpdwFlags = 0;

        if (status == 0)
        {
            return true;
        }

        PInvoke.WSASetLastError((int)(status == StatusPending ? WSA_ERROR.WSA_IO_INCOMPLETE : WSA_ERROR.WSA_OPERATION_ABORTED));
        return false;
    }

    private async Task ReceiveAsync()
    {
        await foreach (var packet in _adapter.ReceiveAllAsync())
        {
            try
            {
                Deliver(packet);
            }
            catch (Exception e)
            {
                Log.Error(e, "Delivering a packet from {From} failed", packet.From);
            }
        }
    }

    private unsafe void Deliver(Packet packet)
    {
        if (packet.Data.Length < HeaderSize)
        {
            return;
        }

        var header = packet.Data.Span;
        var datagram = new Datagram(packet.From, MemoryMarshal.Read<ushort>(header), packet.Data[HeaderSize..]);
        var port = MemoryMarshal.Read<ushort>(header[2..]);

        if (IsDiscovery(datagram.FromPort, port))
        {
            Log.Debug("Received {Length} discovery bytes from {From} on port {Port}", datagram.Data.Length, ToEndPoint(datagram.From, datagram.FromPort), Port(port));
        }

        lock (_lock)
        {
            if (FindByPort(port) is not { } socket)
            {
                Log.Debug("Dropped a packet from {From} to port {Port}: no socket is bound", ToEndPoint(datagram.From, datagram.FromPort), Port(port));
                return;
            }

            if (socket.Pending is { } receive)
            {
                socket.Pending = null;
                Complete(receive.Overlapped, 0, Fill(receive, datagram));
            }
            else if (socket.Queue.Count < MaxQueued)
            {
                socket.Queue.Enqueue(datagram);
            }
            else
            {
                Log.Debug("Dropped a packet from {From} to port {Port}: the receive queue is full", ToEndPoint(datagram.From, datagram.FromPort), Port(port));
            }
        }
    }

    // Discovery only: the game's own traffic would flood the log.
    private static bool IsDiscovery(ushort sourcePort, ushort destinationPort) =>
        Port(sourcePort) == DiscoveryPort || Port(destinationPort) == DiscoveryPort;

    private static unsafe int Fill(PendingReceive receive, Datagram datagram)
    {
        var length = Math.Min(datagram.Data.Length, (int)receive.Buffer.len);
        datagram.Data.Span[..length].CopyTo(new Span<byte>(receive.Buffer.buf.Value, length));

        if (receive.From is null || receive.FromLength is null || *receive.FromLength < sizeof(SOCKADDR_IN)) return length;
        
        var from = (SOCKADDR_IN*)receive.From;
        *from = default;
        from->sin_family = ADDRESS_FAMILY.AF_INET;
        from->sin_port = datagram.FromPort;
        datagram.From.TryWriteBytes(new Span<byte>(&from->sin_addr, sizeof(IN_ADDR)), out _);
        *receive.FromLength = sizeof(SOCKADDR_IN);

        return length;
    }

    private static unsafe void Complete(NativeOverlapped* overlapped, nint status, int transferred)
    {
        if (overlapped is null)
        {
            return;
        }

        overlapped->InternalLow = status;
        overlapped->InternalHigh = transferred;
        PInvoke.WSASetEvent(new WSAEVENT(overlapped->EventHandle));
    }

    private VirtualSocket? FindSocket(SOCKET s)
    {
        lock (_lock)
        {
            return _sockets.GetValueOrDefault(s);
        }
    }

    private VirtualSocket? FindByPort(ushort port) => _sockets.Values.FirstOrDefault(v => v.Address.sin_port == port);

    // Returns the port in network byte order.
    private ushort AllocatePort()
    {
        ushort port;

        do
        {
            port = BinaryPrimitives.ReverseEndianness(_nextPort++);
        }
        while (FindByPort(port) is not null);

        return port;
    }

    // NULL and "" mean the local host too, see the gethostbyname docs.
    private static unsafe bool IsOwnHostName(PCSTR name)
    {
        if (name.Value is null || *name.Value == 0)
        {
            return true;
        }

        var own = stackalloc byte[256];

        return PInvoke.gethostname(new PSTR(own), 256) == 0
            && Ascii.EqualsIgnoreCase(name.AsSpan(), MemoryMarshal.CreateReadOnlySpanFromNullTerminated(own));
    }

    private static ushort Port(ushort networkOrder) => BinaryPrimitives.ReverseEndianness(networkOrder);

    private static IPEndPoint ToEndPoint(IPAddress address, ushort port) => new(address, Port(port));

    private static IPEndPoint ToEndPoint(SOCKADDR_IN address) => ToEndPoint(new IPAddress(address.sin_addr.S_un.S_addr), address.sin_port);

    private sealed class VirtualSocket
    {
        public SOCKADDR_IN Address;
        public PendingReceive? Pending;
        public readonly Queue<Datagram> Queue = new();
    }

    private readonly record struct Datagram(IPAddress From, ushort FromPort, ReadOnlyMemory<byte> Data);

    private unsafe struct PendingReceive
    {
        public WSABUF Buffer;
        public SOCKADDR* From;
        public int* FromLength;
        public NativeOverlapped* Overlapped;
    }

    // One block, so the pointers inside the hostent stay valid.
    private unsafe struct HostEntry
    {
        public HOSTENT Entry;
        public sbyte* NoAliases;
        public sbyte* Address0;
        public sbyte* Address1;
        public uint Address;
        public sbyte Name;

        // Never freed: the game may keep the pointer, and Winsock's own hostent lives as long.
        public static HostEntry* Allocate(IPAddress address)
        {
            var host = (HostEntry*)NativeMemory.AllocZeroed((nuint)sizeof(HostEntry));
            host->Entry.h_name = new PSTR((byte*)&host->Name);
            host->Entry.h_aliases = &host->NoAliases;
            host->Entry.h_addrtype = (short)ADDRESS_FAMILY.AF_INET;
            host->Entry.h_length = sizeof(uint);
            host->Entry.h_addr_list = &host->Address0;
            host->Address0 = (sbyte*)&host->Address;
            host->Address1 = null;
            address.TryWriteBytes(new Span<byte>(&host->Address, sizeof(uint)), out _);
            return host;
        }
    }
}
