using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Memory;

namespace Gestapo.SteamNetworking.Hooking;

// A place where calls to a function can be intercepted. Creating one touches nothing: Replace,
// RunBefore and RunAfter check it and patch it. RunBefore and RunAfter never keep managed code on
// the stack while the function runs: a game crash unwinding past managed frames kills the process.
internal abstract class HookTarget
{
    private readonly string _name;

    private protected HookTarget(string name) => _name = name;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void ThunkCallback(HookRegisters* registers);

    internal static HookTarget Import(string libraryName, string entryPoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryName);
        ArgumentException.ThrowIfNullOrEmpty(entryPoint);
        return new ImportTarget(libraryName, entryPoint);
    }

    internal static HookTarget Slot(string name, nint address, nint expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new SlotTarget(name, address, expected);
    }

    internal static HookTarget Call(string name, nint instruction, nint expected)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new CallTarget(name, instruction, expected);
    }

    internal FunctionHook Replace<TDelegate>(TDelegate replacement) where TDelegate : Delegate
    {
        ArgumentNullException.ThrowIfNull(replacement);

        var original = Resolve();
        return Install(original, GetPointer(replacement));
    }

    internal unsafe FunctionHook RunBefore(Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var original = Resolve();
        var guarded = Guard(callback);
        ThunkCallback enter = registers => guarded(new HookContext(ref *registers, ref *registers));

        return Install(original, HookThunks.CreateEntry(GetPointer(enter), original));
    }

    internal unsafe FunctionHook RunAfter(Action<HookContext> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var original = Resolve();
        var guarded = Guard(callback);
        var frames = new ThreadLocal<Stack<ReturnFrame>>(static () => new Stack<ReturnFrame>());

        ThunkCallback leave = registers =>
        {
            // The stub pushed room for the return address just before saving the registers.
            var room = (nint*)registers->Esp;
            var esp = (nint)(room + 1);
            var frame = PopFrame(frames.Value!, esp);
            *room = frame.ReturnAddress;
            registers->Esp = esp;
            guarded(new HookContext(ref *registers, in frame.Entry));
        };

        var stub = HookThunks.CreateReturn(GetPointer(leave));

        ThunkCallback enter = registers =>
        {
            var returnAddress = (nint*)registers->Esp;
            var stack = frames.Value!;
            DropAbandonedFrames(stack, registers->Esp, isTailCall: *returnAddress == stub);
            stack.Push(new ReturnFrame { ReturnAddress = *returnAddress, Entry = *registers });
            *returnAddress = stub;
        };

        return Install(original, HookThunks.CreateEntry(GetPointer(enter), original));
    }

    public override string ToString() => _name;

    private protected abstract nint Resolve();

    private protected abstract void Redirect(nint detour);

    private protected abstract void Restore();

    // Rooted for good: another thread may be about to call the pointer even after Dispose.
    private static nint GetPointer<TDelegate>(TDelegate callback) where TDelegate : Delegate
    {
        GCHandle.Alloc(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    // A call that an exception left behind never returns, so its frame stays on our stack. Everything
    // still running is higher on the real stack than a new call, except a function that tail-called
    // itself, which is level with it and has our stub as its return address.
    private static void DropAbandonedFrames(Stack<ReturnFrame> frames, nint esp, bool isTailCall)
    {
        while (frames.TryPeek(out var top) && (top.Entry.Esp < esp || (top.Entry.Esp == esp && !isTailCall)))
        {
            frames.Pop();
        }
    }

    // Calls the returning function made and abandoned are above its own frame, which is the last
    // one lower on the real stack than the address it returned to.
    private static ReturnFrame PopFrame(Stack<ReturnFrame> frames, nint esp)
    {
        var frame = frames.Pop();

        while (frames.TryPeek(out var next) && next.Entry.Esp < esp)
        {
            frame = frames.Pop();
        }

        return frame;
    }

    private Action<HookContext> Guard(Action<HookContext> callback)
    {
        var failed = false;

        return context =>
        {
            if (failed)
            {
                return;
            }

            try
            {
                callback(context);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "The hook on {Hook} failed and was turned off", this);
            }
        };
    }

    private FunctionHook Install(nint original, nint detour)
    {
        Redirect(detour);
        Log.Information("Hooked {Hook}: 0x{Original:x} -> 0x{Detour:x}", this, original, detour);
        return new FunctionHook(this, Restore);
    }

    private void ThrowIfOutsideImage(nint address, int size)
    {
        var module = Process.GetCurrentProcess().MainModule!;
        var offset = address - module.BaseAddress;

        if (offset < 0 || offset > module.ModuleMemorySize - size)
        {
            throw new InvalidOperationException($"{this}: 0x{address:x} is outside the game's image");
        }
    }

    // Keeps the page executable: it may hold code that another thread is running.
    private static unsafe void Write(nint address, nint value)
    {
        PInvoke.VirtualProtect(address.ToPointer(), (nuint)sizeof(nint), PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE, out var old);
        Marshal.WriteIntPtr(address, value);
        PInvoke.VirtualProtect(address.ToPointer(), (nuint)sizeof(nint), old, out _);
    }

    private struct ReturnFrame
    {
        public nint ReturnAddress;
        public HookRegisters Entry;
    }

    // A pointer that calls go through, so a hook is one atomic write.
    private abstract class PointerTarget(string name) : HookTarget(name)
    {
        private nint _slot;
        private nint _original;

        private protected abstract (nint Slot, nint Function) Find();

        private protected sealed override nint Resolve()
        {
            (_slot, _original) = Find();
            return _original;
        }

        private protected sealed override void Redirect(nint detour) => Write(_slot, detour);

        private protected sealed override void Restore() => Write(_slot, _original);
    }

    private sealed class ImportTarget(string libraryName, string entryPoint) : PointerTarget($"{libraryName}!{entryPoint}")
    {
        private protected override (nint Slot, nint Function) Find()
        {
            var moduleHandle = PInvoke.GetModuleHandle(libraryName);
            var export = PInvoke.GetProcAddress(moduleHandle, entryPoint);

            if (export.IsNull)
            {
                throw new InvalidOperationException($"{this} not exported");
            }

            var slot = FindInImportTable(export.Value);

            if (slot == nint.Zero)
            {
                throw new InvalidOperationException($"{this} not found");
            }

            return (slot, export.Value);
        }

        private static unsafe nint FindInImportTable(nint function)
        {
            var module = Process.GetCurrentProcess().MainModule!;
            using var pe = new PEReader((byte*)module.BaseAddress, module.ModuleMemorySize, isLoadedImage: true);
            var iatDir = pe.PEHeaders.PEHeader!.ImportAddressTableDirectory;
            var iat = pe.GetSectionData(iatDir.RelativeVirtualAddress);
            var index = new ReadOnlySpan<nint>(iat.Pointer, iatDir.Size / sizeof(nint)).IndexOf(function);
            return index is not -1 ? (nint)(iat.Pointer + index * sizeof(nint)) : nint.Zero;
        }
    }

    private sealed class SlotTarget(string name, nint address, nint expected) : PointerTarget(name)
    {
        private protected override (nint Slot, nint Function) Find()
        {
            ThrowIfOutsideImage(address, nint.Size);

            var actual = Marshal.ReadIntPtr(address);

            if (actual != expected)
            {
                throw new InvalidOperationException($"{this}: slot 0x{address:x} holds 0x{actual:x}, expected 0x{expected:x}");
            }

            return (address, expected);
        }
    }

    // A direct call, for a function without a slot. Only this call site is redirected, and the write
    // isn't atomic, so install it before that code can run.
    private sealed class CallTarget(string name, nint instruction, nint expected) : HookTarget(name)
    {
        private const byte CallOpcode = 0xE8;
        private const int Length = 5;

        private nint Operand => instruction + 1;

        private protected override nint Resolve()
        {
            ThrowIfOutsideImage(instruction, Length);

            var actual = instruction + Length + Marshal.ReadInt32(Operand);

            if (Marshal.ReadByte(instruction) != CallOpcode || actual != expected)
            {
                throw new InvalidOperationException($"{this}: 0x{instruction:x} is not a call to 0x{expected:x}");
            }

            return expected;
        }

        private protected override void Redirect(nint detour) => Write(Operand, detour - (instruction + Length));

        private protected override void Restore() => Write(Operand, expected - (instruction + Length));
    }
}
