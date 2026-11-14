namespace Gestapo.SteamNetworking.Game;

internal static unsafe class PlatformSettings
{
    private const int MenuMouseSensitivityOffset = 0x200;

    internal static float MenuMouseSensitivity => *(float*)(*(nint*)GameAddresses.PlatformSettingsPointer + MenuMouseSensitivityOffset);
}
