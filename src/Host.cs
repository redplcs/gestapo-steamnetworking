using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Gestapo.SteamNetworking.Hooking;
using Gestapo.SteamNetworking.Sockets;
using Gestapo.SteamNetworking.Steam;

namespace Gestapo.SteamNetworking;

internal static class Host
{
    private static readonly Stack<IDisposable> s_services = new();
    
    // ReSharper disable once UnusedMember.Local
    private static readonly FunctionHook<ExitProcess> s_exitProcess = new("kernel32.dll", nameof(ExitProcess), static code =>
    {
        while (s_services.TryPop(out var service))
        {
            try
            {
                service.Dispose();
            }
            catch (Exception e)
            {
                Logger.WriteLine($"Failed to dispose service: {e}");
            }
        }
        
        PInvoke.ExitProcess(code);
    });

    [ModuleInitializer]
    internal static void Start()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(Host).Assembly,
            static (name, _, _) =>
                name.Equals("dinput8.dll", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "dinput8.dll"))
                    : nint.Zero);

        try
        {
            ITransport transport;

            // Loopback runs several game instances on one machine, for testing without a second account.
            if (Environment.GetEnvironmentVariable("GESTAPO_TRANSPORT") == "loopback")
            {
                transport = new LoopbackTransport();
            }
            else
            {
                s_services.Push(new SteamManager());
                transport = new SteamTransport();
            }

            s_services.Push(transport);
            s_services.Push(new VirtualSockets(transport));
        }
        catch (Exception e)
        {
            Logger.WriteLine($"{e}");
        }
    }

    public static TService GetService<TService>() where TService : IDisposable
    {
        return s_services.OfType<TService>().Single();
    }
}
