using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Gestapo.SteamNetworking;

internal static unsafe class NativeExports
{
    // Exports that make this DLL a drop-in dinput8.dll proxy: the game statically imports them,
    // so they must exist here. Each one just forwards to the real System32 dinput8.dll.
    [UnmanagedCallersOnly(EntryPoint = nameof(DirectInput8Create), CallConvs = [typeof(CallConvStdcall)])]
    internal static HRESULT DirectInput8Create(HINSTANCE hinst, uint dwVersion, Guid* riidltf, void** ppvOut, IUnknown* punkOuter)
    {
        Host.Start();
        return PInvoke.DirectInput8Create(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    }
}
