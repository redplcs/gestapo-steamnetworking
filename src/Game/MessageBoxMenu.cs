namespace Gestapo.SteamNetworking.Game;

internal readonly unsafe struct MessageBoxMenu(nint pointer)
{
    private const int ButtonsOffset = 0x7C;
    private const int TextOffset = 0x90;

    internal uint Buttons => *(uint*)(pointer + ButtonsOffset);

    internal string Text => new((char*)(pointer + TextOffset));
}
