namespace Gestapo.SteamNetworking.Game;

internal readonly unsafe struct Menu(nint pointer)
{
    private const int PanelGetterSlot = 0x20;

    // A menu the game has detached has none.
    internal MenuPanel? Panel
    {
        get
        {
            var vtable = *(nint*)pointer;
            var getPanel = (delegate* unmanaged[Thiscall]<nint, nint>)*(nint*)(vtable + PanelGetterSlot);
            var panel = getPanel(pointer);
            return panel == 0 ? null : new MenuPanel(panel);
        }
    }
}
