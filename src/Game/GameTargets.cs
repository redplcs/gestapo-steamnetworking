using Gestapo.SteamNetworking.Hooking;

namespace Gestapo.SteamNetworking.Game;

// A hook checks its target first, so on another exe it fails to install and memory stays untouched.
internal static class GameTargets
{
    // The last call in CGame::Create, to CHeapManager::SetOOMHandler: all the game's managers exist by now.
    internal static readonly HookTarget CreateEnd =
        HookTarget.Call("CGame::Create end", instruction: GameAddresses.CreateEndCall, expected: GameAddresses.SetOutOfMemoryHandlerFunction);

    internal static readonly HookTarget Destroy =
        HookTarget.Call("CGame::Destroy", instruction: GameAddresses.DestroyCall, expected: GameAddresses.DestroyFunction);

    // The only call to the menu manager's update, so it runs every frame on any screen.
    internal static readonly HookTarget MenuUpdate =
        HookTarget.Call("menu update", instruction: GameAddresses.MenuUpdateCall, expected: GameAddresses.MenuUpdateFunction);
}
