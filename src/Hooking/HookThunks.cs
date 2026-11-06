using Windows.Win32;
using Windows.Win32.System.Memory;

namespace Gestapo.SteamNetworking.Hooking;

internal static unsafe class HookThunks
{
    // pushad; pushfd; push esp; call callback; popfd; popad; jmp next
    internal static nint CreateEntry(nint callback, nint next)
    {
        ReadOnlySpan<byte> template = [0x60, 0x9C, 0x54, 0xE8, 0, 0, 0, 0, 0x9D, 0x61, 0xE9, 0, 0, 0, 0];
        var code = Allocate(template);
        WriteRelative(code + 4, callback);
        WriteRelative(code + 11, next);
        return MakeExecutable(code, template.Length);
    }

    // push eax, as room for the return address; pushad; pushfd; push esp; call callback; popfd; popad; ret
    internal static nint CreateReturn(nint callback)
    {
        ReadOnlySpan<byte> template = [0x50, 0x60, 0x9C, 0x54, 0xE8, 0, 0, 0, 0, 0x9D, 0x61, 0xC3];
        var code = Allocate(template);
        WriteRelative(code + 5, callback);
        return MakeExecutable(code, template.Length);
    }

    private static byte* Allocate(ReadOnlySpan<byte> template)
    {
        var code = PInvoke.VirtualAlloc(
            null,
            (nuint)template.Length,
            VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT | VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE,
            PAGE_PROTECTION_FLAGS.PAGE_READWRITE);

        if (code is null)
        {
            throw new OutOfMemoryException();
        }

        template.CopyTo(new Span<byte>(code, template.Length));
        return (byte*)code;
    }

    private static void WriteRelative(byte* operand, nint target) =>
        *(int*)operand = (int)(target - (nint)(operand + sizeof(int)));

    private static nint MakeExecutable(byte* code, int length)
    {
        PInvoke.VirtualProtect(code, (nuint)length, PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READ, out _);
        return (nint)code;
    }
}
