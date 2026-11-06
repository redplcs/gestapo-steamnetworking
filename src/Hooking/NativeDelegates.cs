using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Networking.WinSock;

namespace Gestapo.SteamNetworking.Hooking;

// Signatures of the exports we hook, written by hand because CsWin32 can't generate them.
// Each delegate is named after its export, so nameof() gives the entry point.
#pragma warning disable CS8981 // lowercase names come from the exports
internal static unsafe class NativeDelegates
{
    // ws2_32.dll

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WSASendTo(
        SOCKET s,
        WSABUF* lpBuffers,
        uint dwBufferCount,
        uint* lpNumberOfBytesSent,
        uint dwFlags,
        SOCKADDR* lpTo,
        int iTolen,
        NativeOverlapped* lpOverlapped,
        delegate* unmanaged[Stdcall]<uint, uint, NativeOverlapped*, uint, void> lpCompletionRoutine);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int WSARecvFrom(
        SOCKET s,
        WSABUF* lpBuffers,
        uint dwBufferCount,
        uint* lpNumberOfBytesRecvd,
        uint* lpFlags,
        SOCKADDR* lpFrom,
        int* lpFromlen,
        NativeOverlapped* lpOverlapped,
        delegate* unmanaged[Stdcall]<uint, uint, NativeOverlapped*, uint, void> lpCompletionRoutine);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate BOOL WSAGetOverlappedResult(
        SOCKET s,
        NativeOverlapped* lpOverlapped,
        uint* lpcbTransfer,
        BOOL fWait,
        uint* lpdwFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int bind(SOCKET s, SOCKADDR* name, int namelen);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int getsockname(SOCKET s, SOCKADDR* name, int* namelen);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int closesocket(SOCKET s);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate HOSTENT* gethostbyname(PCSTR name);
}
