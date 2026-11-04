using Steamworks;

namespace Gestapo.SteamNetworking.Steam;

internal sealed class SteamManager : IDisposable
{
    private static readonly AppId_t s_appId = new(480);

    // SteamTransport runs the callbacks: they must come from a single thread.
    internal SteamManager()
    {
        EnsureAppIdExists();

        if (SteamAPI.InitEx(out var msg) != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            throw new InvalidOperationException(msg);
        }
    }

    private static void EnsureAppIdExists()
    {
        const string fileName = "steam_appid.txt";

        if (File.Exists(fileName)) return;
        
        File.WriteAllText(fileName, s_appId.ToString());
    }

    void IDisposable.Dispose()
    {
        SteamAPI.Shutdown();
    }
}