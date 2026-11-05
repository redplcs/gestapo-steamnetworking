using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Win32;
using Gestapo.SteamNetworking.Hooking;
using Gestapo.SteamNetworking.Sockets;
using Gestapo.SteamNetworking.Steam;

namespace Gestapo.SteamNetworking;

internal static class Host
{
    private static bool s_started;

    // Static: the game calls our hooks through these objects' delegates, GC must not collect them.
    // ReSharper disable once NotAccessedField.Local
    private static FunctionHook<ExitProcess>? s_exit;
    private static VirtualWinsock? s_winsock;

    internal static void Start()
    {
        if (s_started)
        {
            return;
        }

        s_started = true;

        // The game loaded us as dinput8.dll, so a plain "dinput8.dll" would resolve to us, not the real one.
        NativeLibrary.SetDllImportResolver(
            typeof(Host).Assembly,
            static (name, _, _) =>
                name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dinput8.dll"))
                    : nint.Zero);

        // One file per game launch. Two instances started within the same second share it.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppContext.BaseDirectory, $"steamnet-{DateTime.Now:yyyyMMdd-HHmmss}.log"), shared: true)
            .CreateLogger();

        Log.Information("Starting the mod {Version}", typeof(Host).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

        try
        {
            s_exit = new FunctionHook<ExitProcess>("kernel32.dll", nameof(ExitProcess), Stop);
            s_winsock = new VirtualWinsock(Environment.GetEnvironmentVariable("GESTAPO_TRANSPORT") == "loopback"
                ? new LoopbackAdapter()
                : new SteamAdapter());
        }
        catch (Exception e)
        {
            Log.Fatal(e, "The mod failed to start, the game runs without it");
        }
    }

    private static void Stop(uint exitCode)
    {
        Log.Information("The game is exiting with code {ExitCode}", exitCode);

        try
        {
            s_winsock?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error(e, "The mod failed to stop");
        }
        finally
        {
            Log.CloseAndFlush();
            PInvoke.ExitProcess(exitCode);
        }
    }
}
