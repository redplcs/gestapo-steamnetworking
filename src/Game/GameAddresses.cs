namespace Gestapo.SteamNetworking.Game;

// Addresses in CommXPC.exe with PE timestamp 0x442BAC38, loaded at its fixed base.
internal static class GameAddresses
{
    internal const nint GameInstancePointer = 0x8B85D0;
    internal const nint ShowMessageBoxFunction = 0x512510;
    internal const nint TogglePauseFunction = 0x510990;

    internal const nint SetMouseSensitivityFunction = 0x71A090;
    internal const nint ResetMouseSensitivityFunction = 0x71A0A0;

    internal const nint PlatformSettingsPointer = 0x8C4354;

    internal const nint FirstModalMenuFunction = 0x721CC0;
    internal const nint IsMenuActiveFunction = 0x721730;

    internal const nint SetMouseEnabledFunction = 0x71EF80;

    internal const nint CreateEndCall = 0x512BA7;
    internal const nint SetOutOfMemoryHandlerFunction = 0x697780;

    internal const nint DestroyCall = 0x50C5DC;
    internal const nint DestroyFunction = 0x50FEB0;

    internal const nint MenuUpdateCall = 0x51080D;
    internal const nint MenuUpdateFunction = 0x724390;
}
