using Steamworks;

namespace Gestapo.SteamNetworking.Steam;

internal sealed class SteamManager : IDisposable
{
    private static readonly AppId_t s_appId = new(480);
    
    private readonly Timer _timer;
    
    internal SteamManager()
    {
        EnsureAppIdExists();

        if (SteamAPI.InitEx(out var msg) != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            throw new InvalidOperationException(msg);
        }
        
        _timer = new Timer(
            static _ => SteamAPI.RunCallbacks(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100));
    }

    private static void EnsureAppIdExists()
    {
        const string fileName = "steam_appid.txt";

        if (File.Exists(fileName)) return;
        
        File.WriteAllText(fileName, s_appId.ToString());
    }

    void IDisposable.Dispose()
    {
        _timer.Dispose();
        
        SteamAPI.Shutdown();
    }
}