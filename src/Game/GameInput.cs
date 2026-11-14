namespace Gestapo.SteamNetworking.Game;

internal readonly unsafe struct GameInput(nint pointer)
{
    private const int MouseSensitivityOffset = 0xC94;

    internal float MouseSensitivity => *(float*)(pointer + MouseSensitivityOffset);

    internal void SetMouseSensitivity(float value) =>
        ((delegate* unmanaged[Thiscall]<nint, float, void>)GameAddresses.SetMouseSensitivityFunction)(pointer, value);

    internal void ResetMouseSensitivity() =>
        ((delegate* unmanaged[Thiscall]<nint, void>)GameAddresses.ResetMouseSensitivityFunction)(pointer);
}
