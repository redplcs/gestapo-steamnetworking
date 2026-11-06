using Gestapo.SteamNetworking.Hooking;

namespace Gestapo.SteamNetworking.Game;

// Addresses in CommXPC.exe with PE timestamp 0x442BAC38, loaded at its fixed base. A hook checks its
// target first, so on another exe it fails to install and memory stays untouched.
internal static class GameTargets
{
    // The last call in CGame::Create, to CHeapManager::SetOOMHandler: all the game's managers exist by now.
    internal static readonly HookTarget CreateEnd =
        HookTarget.Call("CGame::Create end", instruction: 0x512BA7, expected: 0x697780);

    internal static readonly HookTarget Destroy =
        HookTarget.Call("CGame::Destroy", instruction: 0x50C5DC, expected: 0x50FEB0);
}
