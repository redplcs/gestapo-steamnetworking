using Gestapo.SteamNetworking.Sockets;
using Gestapo.SteamNetworking.Steam;

namespace Gestapo.SteamNetworking;

internal static class Host
{
    private static VirtualWinsock? s_winsock;

    internal static void Start()
    {
        Log.Information("The game is ready");

        s_winsock = new VirtualWinsock(Environment.GetEnvironmentVariable("GESTAPO_TRANSPORT") == "loopback"
            ? new LoopbackAdapter()
            : new SteamAdapter());
    }

    internal static void Stop()
    {
        Log.Information("The game is shutting down");

        s_winsock?.Dispose();
    }
}
