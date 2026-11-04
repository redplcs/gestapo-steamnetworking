using Steamworks;

namespace Gestapo.SteamNetworking.Steam;

internal sealed class SteamManager : IDisposable
{
    private static readonly AppId_t s_appId = new(480);

    // SteamTransport runs the callbacks: they must come from a single thread.
    internal SteamManager()
    {
        // Replaces steam_appid.txt, which would land in VirtualStore when the game is in Program Files.
        Environment.SetEnvironmentVariable("SteamAppId", s_appId.ToString());

        if (SteamAPI.InitEx(out var msg) != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            throw new InvalidOperationException(msg);
        }
    }

    void IDisposable.Dispose()
    {
        SteamAPI.Shutdown();
    }
}