namespace Gestapo.SteamNetworking.Game;

// The running game, for the build in GameAddresses. Use it and the objects it returns only on the game thread.
internal static unsafe class GameInstance
{
    private const int InputOffset = 0x10;
    private const int MenuManagerOffset = 0x14;
    private const int MatchMenuOffset = 0x18;
    private const int MessageBoxMenuOffset = 0x5C;
    private const int PausedOffset = 0x89;
    private const int MultiplayerOffset = 0xD0;
    private const int OpenMessageBoxOffset = 0xF0;
    private const int MessageBoxAnswerOffset = 0xF4;
    private const int MessageBoxOptionsOffset = 0x8A0;

    // The game copies the text into a 1024 character buffer without checking its length.
    private const int MaxMessageLength = 1023;

    private static nint Pointer => *(nint*)GameAddresses.GameInstancePointer;

    internal static GameInput Input => new(*(nint*)(Pointer + InputOffset));

    internal static MenuManager Menus => new(*(nint*)(Pointer + MenuManagerOffset));

    // The match HUD menu stays active under every menu shown over a match. The box asks the same way.
    internal static bool IsInMatch =>
        ((delegate* unmanaged[Thiscall]<nint, nint, byte, byte>)GameAddresses.IsMenuActiveFunction)(
            *(nint*)(Pointer + MenuManagerOffset), *(nint*)(Pointer + MatchMenuOffset), 0) != 0;

    internal static bool IsMultiplayer => *(byte*)(Pointer + MultiplayerOffset) != 0;

    internal static bool IsPaused => *(byte*)(Pointer + PausedOffset) != 0;

    // Pauses or resumes the sound too, as the pause menu does.
    internal static void TogglePause() =>
        ((delegate* unmanaged[Thiscall]<nint, byte, void>)GameAddresses.TogglePauseFunction)(Pointer, 1);

    internal static MessageBoxMenu? OpenMessageBox
    {
        get
        {
            var box = *(nint*)(Pointer + OpenMessageBoxOffset);
            return box == 0 ? null : new MessageBoxMenu(box);
        }
    }

    internal static int MessageBoxAnswer
    {
        get => *(int*)(Pointer + MessageBoxAnswerOffset);
        set => *(int*)(Pointer + MessageBoxAnswerOffset) = value;
    }

    internal static void ShowMessageBox(string text, uint buttons) => ShowMessageBox(text, buttons, options: 0);

    internal static void ShowMessageBoxWithSameOptions(string text, uint buttons) =>
        ShowMessageBox(text, buttons, *(nint*)(Pointer + MessageBoxMenuOffset) + MessageBoxOptionsOffset);

    private static void ShowMessageBox(string text, uint buttons, nint options)
    {
        fixed (char* chars = text.Length > MaxMessageLength ? text[..MaxMessageLength] : text)
        {
            ((delegate* unmanaged[Thiscall]<nint, char*, uint, nint, void>)GameAddresses.ShowMessageBoxFunction)(Pointer, chars, buttons, options);
        }
    }
}
