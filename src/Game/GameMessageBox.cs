namespace Gestapo.SteamNetworking.Game;

// Use only on the game thread.
internal static class GameMessageBox
{
    private const uint OkButton = 1;

    // Lets Esc close the box. It also keeps OK from being the only flag, which would make a click
    // anywhere press it.
    private const uint EscapeKey = 0x800;

    private const uint OurButtons = OkButton | EscapeKey;

    // OK to the last slot button; the bits above are keys and flags.
    private const uint PressableButtons = 0x7FF;

    private static Menu? s_mutedMenu;
    private static bool s_isMouseSwitched;
    private static bool s_isMatchPaused;
    private static (string Text, uint Buttons)? s_coveredDialog;

    // A box without buttons closes from code, so it could not be closed after bringing it back.
    internal static bool CanShow => GameInstance.OpenMessageBox is not { } box
        || (box.Buttons != OurButtons && (box.Buttons & PressableButtons) != 0);

    internal static void Update()
    {
        if (GameInstance.OpenMessageBox is { } box)
        {
            // The game replaced our text with its own box, which takes the old dialog's place.
            if (box.Buttons != OurButtons)
            {
                s_coveredDialog = null;
            }

            return;
        }

        RestoreAfterClose();

        if (s_coveredDialog is { } dialog)
        {
            s_coveredDialog = null;

            // Showing clears the answer our box left, before the dialog's menu updates and reads it.
            GameInstance.ShowMessageBoxWithSameOptions(dialog.Text, dialog.Buttons);
        }
    }

    internal static void Show(string text)
    {
        MuteScreenBelow();
        SwitchToMenuMouseSensitivity();
        PauseMatch();

        if (GameInstance.OpenMessageBox is { } covered)
        {
            s_coveredDialog = (covered.Text, covered.Buttons);
            GameInstance.ShowMessageBoxWithSameOptions(text, OurButtons);
            return;
        }

        // Showing clears the answer from the game's last box, and its menu may not have read it yet.
        var answer = GameInstance.MessageBoxAnswer;
        GameInstance.ShowMessageBox(text, OurButtons);
        GameInstance.MessageBoxAnswer = answer;
    }

    // Some screens switch their buttons every frame, which checks hover again past the block on input.
    // A panel with the mouse off stops tracking the cursor.
    private static void MuteScreenBelow()
    {
        s_mutedMenu = GameInstance.Menus.FirstModalMenu;
        s_mutedMenu?.Panel?.SetMouseEnabled(false);
    }

    // Menus over a match switch the mouse from aiming speed the same way.
    private static void SwitchToMenuMouseSensitivity()
    {
        var input = GameInstance.Input;
        var menuSensitivity = PlatformSettings.MenuMouseSensitivity;

        if (input.MouseSensitivity != menuSensitivity)
        {
            input.SetMouseSensitivity(menuSensitivity);
            s_isMouseSwitched = true;
        }
    }

    // The single player pause menu pauses the same way, and a multiplayer match never pauses.
    private static void PauseMatch()
    {
        if (GameInstance.IsInMatch && !GameInstance.IsMultiplayer && !GameInstance.IsPaused)
        {
            GameInstance.TogglePause();
            s_isMatchPaused = true;
        }
    }

    private static void RestoreAfterClose()
    {
        // Entering a match frees the panels of the screens it leaves, so ask the menu for its panel again.
        s_mutedMenu?.Panel?.SetMouseEnabled(true);
        s_mutedMenu = null;

        if (s_isMouseSwitched)
        {
            GameInstance.Input.ResetMouseSensitivity();
            s_isMouseSwitched = false;
        }

        // A scene change clears the pause by itself.
        if (s_isMatchPaused && GameInstance.IsPaused)
        {
            GameInstance.TogglePause();
        }

        s_isMatchPaused = false;
    }
}
