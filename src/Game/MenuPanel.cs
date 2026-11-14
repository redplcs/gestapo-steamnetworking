namespace Gestapo.SteamNetworking.Game;

internal readonly unsafe struct MenuPanel(nint pointer)
{
    internal void SetMouseEnabled(bool enabled) =>
        ((delegate* unmanaged[Thiscall]<nint, byte, void>)GameAddresses.SetMouseEnabledFunction)(pointer, Convert.ToByte(enabled));
}
