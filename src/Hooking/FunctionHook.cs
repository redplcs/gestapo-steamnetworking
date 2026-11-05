using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace Gestapo.SteamNetworking.Hooking;

internal sealed class FunctionHook<TDelegate> : IDisposable where TDelegate : Delegate
{
    private readonly TDelegate _hook;
    private readonly FARPROC _original;
    private readonly nint _slot;
    private readonly string _name;

    internal FunctionHook(string libraryName, string entryPoint, TDelegate hook)
        : this(Process.GetCurrentProcess().MainModule!, libraryName, entryPoint, hook)
    {
    }

    internal FunctionHook(ProcessModule module, string libraryName, string entryPoint, TDelegate hook)
    {
        _name = $"{libraryName}!{entryPoint}";
        _hook = hook;

        if ((_original = GetExport(libraryName, entryPoint)).IsNull)
        {
            throw new InvalidOperationException($"{_name} not exported");
        }

        if ((_slot = FindSlot(module, _original)) == nint.Zero)
        {
            throw new InvalidOperationException($"{_name} not found");
        }

        var trunk = (FARPROC)Marshal.GetFunctionPointerForDelegate(hook);
        Write(trunk);
        Log.Information("Hooked {Hook} in IAT slot 0x{Slot:x}: {Original} -> {Trunk}", this, _slot, _original, trunk);
    }

    public void Dispose()
    {
        Write(_original);
        Log.Information("Unhooked {Hook}, IAT slot 0x{Slot:x} restored to {Original}", this, _slot, _original);
    }

    private unsafe void Write(FARPROC trunk)
    {
        PInvoke.VirtualProtect(_slot.ToPointer(), (nuint)sizeof(nint), PAGE_PROTECTION_FLAGS.PAGE_READWRITE, out var old);
        Marshal.WriteIntPtr(_slot, trunk.Value);
        PInvoke.VirtualProtect(_slot.ToPointer(), (nuint)sizeof(nint), old, out _);
    }

    private static FARPROC GetExport(string libraryName, string entryPoint)
    {
        var moduleHandle = PInvoke.GetModuleHandle(libraryName);
        return PInvoke.GetProcAddress(moduleHandle, entryPoint);
    }

    private static unsafe nint FindSlot(ProcessModule module, FARPROC target)
    {
        using var pe = new PEReader((byte*)module.BaseAddress, module.ModuleMemorySize, isLoadedImage: true);
        var iatDir = pe.PEHeaders.PEHeader!.ImportAddressTableDirectory;
        var iat = pe.GetSectionData(iatDir.RelativeVirtualAddress);
        var index = new ReadOnlySpan<nint>(iat.Pointer, iatDir.Size / sizeof(nint)).IndexOf(target);
        return index is not -1 ? (nint)(iat.Pointer + index * sizeof(nint)) : nint.Zero;
    }

    public override string ToString() => _name;
}
