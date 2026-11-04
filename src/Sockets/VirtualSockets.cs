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
// and never do I/O: sends go to the transport, receives are completed from it, with the same
// overlapped semantics the game sees from Winsock. gethostbyname of our own host name returns only
// the virtual address, which is what peers find in our station URLs.
internal sealed unsafe class VirtualSockets : IDisposable
{
    private const string Library = "ws2_32.dll";

    // Each transport packet starts with the source and destination ports, in network byte order.
    private const int HeaderSize = 4;
    private const int MaxQueued = 256;

    // NTSTATUS values the real provider leaves in OVERLAPPED.Internal.
    private const nint StatusPending = 0x103;
    private const nint StatusCancelled = unchecked((int)0xC0000120);

    private readonly ITransport _transport;
    private readonly List<IDisposable> _hooks = [];
    private readonly Dictionary<SOCKET, VirtualSocket> _sockets = [];
    private readonly Lock _lock = new();
    private readonly HostEntry* _host;
    private ushort _nextPort = 49152;

    internal VirtualSockets(ITransport transport)
    {
        // Never freed: the game may keep the pointer, and Winsock's own hostent lives as long.
        _host = (HostEntry*)NativeMemory.AllocZeroed((nuint)sizeof(HostEntry));
        _host->Entry.h_name = new PSTR((byte*)&_host->Name);
        _host->Entry.h_aliases = &_host->NoAliases;
        _host->Entry.h_addrtype = (short)ADDRESS_FAMILY.AF_INET;
        _host->Entry.h_length = sizeof(uint);
        _host->Entry.h_addr_list = &_host->Address0;
        _host->Address0 = (sbyte*)&_host->Address;
        _host->Address1 = null;
        _host->Address = transport.Address;

        _transport = transport;
        _transport.Received += Deliver;

        try
        {
            _hooks.Add(new FunctionHook<bind>(Library, nameof(bind), Bind));
            _hooks.Add(new FunctionHook<getsockname>(Library, nameof(getsockname), GetSockName));
            _hooks.Add(new FunctionHook<closesocket>(Library, nameof(closesocket), CloseSocket));
            _hooks.Add(new FunctionHook<gethostbyname>(Library, nameof(gethostbyname), GetHostByName));
            _hooks.Add(new FunctionHook<WSASendTo>(Library, nameof(WSASendTo), SendTo));
            _hooks.Add(new FunctionHook<WSARecvFrom>(Library, nameof(WSARecvFrom), RecvFrom));
            _hooks.Add(new FunctionHook<WSAGetOverlappedResult>(Library, nameof(WSAGetOverlappedResult), GetOverlappedResult));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _transport.Received -= Deliver;

        foreach (var hook in _hooks)
        {
            hook.Dispose();
        }
    }

    private int Bind(SOCKET s, SOCKADDR* name, int namelen)
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

        Logger.WriteLine($"bind {s}: {ToEndPoint(requested)}");
        return 0;
    }

    private int GetSockName(SOCKET s, SOCKADDR* name, int* namelen)
    {
        VirtualSocket? socket;

        lock (_lock)
        {
            _sockets.TryGetValue(s, out socket);
        }

        if (socket is null)
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

    private int CloseSocket(SOCKET s)
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

    private HOSTENT* GetHostByName(PCSTR name)
    {
        if (!IsOwnHostName(name))
        {
            return PInvoke.gethostbyname(name);
        }

        Logger.WriteLine($"gethostbyname {name}: {new IPAddress(_host->Address)}");
        return &_host->Entry;
    }

    // The game passes one buffer and an event-based OVERLAPPED, without a completion routine.
    private int SendTo(
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
        VirtualSocket? socket;

        lock (_lock)
        {
            _sockets.TryGetValue(s, out socket);
        }

        if (socket is null || lpTo is null || iTolen < sizeof(SOCKADDR_IN))
        {
            return PInvoke.WSASendTo(s, lpBuffers, dwBufferCount, lpNumberOfBytesSent, dwFlags, lpTo, iTolen, lpOverlapped, lpCompletionRoutine);
        }

        var to = (SOCKADDR_IN*)lpTo;
        var data = new ReadOnlySpan<byte>(lpBuffers->buf.Value, (int)lpBuffers->len);
        var packet = new byte[HeaderSize + data.Length];

        MemoryMarshal.Write(packet, socket.Address.sin_port);
        MemoryMarshal.Write(packet.AsSpan(2), to->sin_port);
        data.CopyTo(packet.AsSpan(HeaderSize));
        _transport.Send(to->sin_addr.S_un.S_addr, packet);

        if (lpNumberOfBytesSent is not null)
        {
            *lpNumberOfBytesSent = (uint)data.Length;
        }

        Complete(lpOverlapped, 0, data.Length);
        return 0;
    }

    private int RecvFrom(
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
    private BOOL GetOverlappedResult(SOCKET s, NativeOverlapped* lpOverlapped, uint* lpcbTransfer, BOOL fWait, uint* lpdwFlags)
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

    // Runs on the transport's thread.
    private void Deliver(uint from, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize)
        {
            return;
        }

        var datagram = new Datagram(from, MemoryMarshal.Read<ushort>(packet), packet[HeaderSize..].ToArray());
        var port = MemoryMarshal.Read<ushort>(packet[2..]);

        lock (_lock)
        {
            if (FindByPort(port) is not { } socket)
            {
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
        }
    }

    // ponytail: a datagram longer than the buffer is truncated without WSAEMSGSIZE
    private static int Fill(PendingReceive receive, Datagram datagram)
    {
        var length = Math.Min(datagram.Data.Length, (int)receive.Buffer.len);
        datagram.Data.AsSpan(0, length).CopyTo(new Span<byte>(receive.Buffer.buf.Value, length));

        if (receive.From is not null && receive.FromLength is not null && *receive.FromLength >= sizeof(SOCKADDR_IN))
        {
            var from = (SOCKADDR_IN*)receive.From;
            *from = default;
            from->sin_family = ADDRESS_FAMILY.AF_INET;
            from->sin_port = datagram.FromPort;
            from->sin_addr.S_un.S_addr = datagram.From;
            *receive.FromLength = sizeof(SOCKADDR_IN);
        }

        return length;
    }

    private static void Complete(NativeOverlapped* overlapped, nint status, int transferred)
    {
        if (overlapped is null)
        {
            return;
        }

        overlapped->InternalLow = status;
        overlapped->InternalHigh = transferred;
        PInvoke.WSASetEvent(new WSAEVENT(overlapped->EventHandle));
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
    private static bool IsOwnHostName(PCSTR name)
    {
        if (name.Value is null || *name.Value == 0)
        {
            return true;
        }

        var own = stackalloc byte[256];

        return PInvoke.gethostname(new PSTR(own), 256) == 0
            && Ascii.EqualsIgnoreCase(name.AsSpan(), MemoryMarshal.CreateReadOnlySpanFromNullTerminated(own));
    }

    private static IPEndPoint ToEndPoint(SOCKADDR_IN address) =>
        new(new IPAddress(address.sin_addr.S_un.S_addr), BinaryPrimitives.ReverseEndianness(address.sin_port));

    private sealed class VirtualSocket
    {
        public SOCKADDR_IN Address;
        public PendingReceive? Pending;
        public readonly Queue<Datagram> Queue = new();
    }

    private readonly record struct Datagram(uint From, ushort FromPort, byte[] Data);

    private struct PendingReceive
    {
        public WSABUF Buffer;
        public SOCKADDR* From;
        public int* FromLength;
        public NativeOverlapped* Overlapped;
    }

    // One block, so the pointers inside the hostent stay valid.
    private struct HostEntry
    {
        public HOSTENT Entry;
        public sbyte* NoAliases;
        public sbyte* Address0;
        public sbyte* Address1;
        public uint Address;
        public sbyte Name;
    }
}
