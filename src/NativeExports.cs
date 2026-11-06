using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Gestapo.SteamNetworking.Game;
using Gestapo.SteamNetworking.Hooking;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Gestapo.SteamNetworking;

internal static unsafe class NativeExports
{
    private static bool s_attached;

    // Exports that make this DLL a drop-in dinput8.dll proxy: the game statically imports them,
    // so they must exist here. Each one forwards to the real System32 dinput8.dll.
    [UnmanagedCallersOnly(EntryPoint = nameof(DirectInput8Create), CallConvs = [typeof(CallConvStdcall)])]
    internal static HRESULT DirectInput8Create(HINSTANCE hinst, uint dwVersion, Guid* riidltf, void** ppvOut, IUnknown* punkOuter)
    {
        AttachToGame();
        return PInvoke.DirectInput8Create(hinst, dwVersion, riidltf, ppvOut, punkOuter);
    }

    // The game's first call into this DLL comes from inside CGame::Create.
    private static void AttachToGame()
    {
        if (s_attached)
        {
            return;
        }

        s_attached = true;

        // The game loaded us as dinput8.dll, so a plain "dinput8.dll" would resolve to us, not the real one.
        NativeLibrary.SetDllImportResolver(
            typeof(NativeExports).Assembly,
            static (name, _, _) =>
                name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dinput8.dll"))
                    : nint.Zero);

        // One file per game launch. Two instances started within the same second share it.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppContext.BaseDirectory, $"steamnet-{DateTime.Now:yyyyMMdd-HHmmss}.log"), shared: true)
            .CreateLogger();

        Log.Information("Starting the mod {Version}", typeof(NativeExports).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        try
        {
            GameTargets.CreateEnd.RunBefore(OnGameCreated);
            GameTargets.Destroy.RunBefore(OnGameDestroying);
        }
        catch (Exception e)
        {
            Log.Fatal(e, "The mod does not support this game build and stays off");
        }
    }

    private static void OnGameCreated(HookContext context)
    {
        // EBX holds the result CGame::Create is about to return.
        if ((byte)context.Registers.Ebx == 0)
        {
            return;
        }

        try
        {
            Host.Start();
        }
        catch (Exception e)
        {
            Log.Fatal(e, "The mod failed to start, the game runs without it");
        }
    }

    private static void OnGameDestroying(HookContext context)
    {
        Host.Stop();
        Log.CloseAndFlush();
    }
}
