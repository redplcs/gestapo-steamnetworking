namespace Gestapo.SteamNetworking.Game;

internal readonly unsafe struct MenuManager(nint pointer)
{
    // Screens are modal and the match HUD isn't, so in a match without the pause menu there is none.
    internal Menu? FirstModalMenu
    {
        get
        {
            var menu = ((delegate* unmanaged[Thiscall]<nint, nint>)GameAddresses.FirstModalMenuFunction)(pointer);
            return menu == 0 ? null : new Menu(menu);
        }
    }
}
