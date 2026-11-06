using System.Runtime.InteropServices;

namespace Gestapo.SteamNetworking.Hooking;

// Valid only during the callback, which the ref struct enforces.
internal readonly ref struct HookContext
{
    private readonly ref HookRegisters _registers;
    private readonly ref readonly HookRegisters _entry;

    internal HookContext(ref HookRegisters registers, ref readonly HookRegisters entry)
    {
        _registers = ref registers;
        _entry = ref entry;
    }

    internal ref HookRegisters Registers => ref _registers;

    internal ref readonly HookRegisters Entry => ref _entry;
}

// Laid out by the thunk's pushfd + pushad, don't reorder.
[StructLayout(LayoutKind.Sequential)]
internal struct HookRegisters
{
    public uint EFlags;
    public nint Edi;
    public nint Esi;
    public nint Ebp;
    public nint Esp;
    public nint Ebx;
    public nint Edx;
    public nint Ecx;
    public nint Eax;
}
