namespace RA01.Core;

// RA01: M4b-3 - every keyboard binding of the mod lives here, so a key can be changed in one place. Controller buttons will be added here later (M6).
// the numbers are Windows virtual-key codes.
// M5a-12 - every key that used to nudge a value (picture size, field of view, prediction, IPD, frame lag) or quick-toggle one (stereo, swap eyes, F9 menu view) is gone: the F10 menu (SettingsMenu.cs) is now the only place any of it is set.
// what is left are the keys the F10 menu cannot replace: Home recentres instantly (useful before the menu is even readable), F10 opens the menu, the mouse button clicks inside it, and End is a fast physical toggle for head steering, kept for the same reason as Home - it is worth being able to reach without stopping to open a window.
// Cost - CPU: extremely low (a table of key numbers plus a few polls per headset frame, each about a microsecond); GPU: none (no GPU work).
internal static class Keys
{
    public const int Recentre = 0x24;          // RA01: Home
    public const int ToggleSteering = 0x23;    // RA01: End
    public const int MouseLeft = 0x01;         // RA01: the left mouse button (M5a-3), read from the keyboard state like a key, for clicks in the F10 window
    public const int Menu = 0x79;              // RA01: F10 (M5a-3) opens and closes the settings window
    public const int BoneFind = 0x7A;          // RA01: M6-6 - F11 starts a one-off heap hunt for the skeleton (see BoneFinder.cs). NOT F9: that is Cyberpunk's own quickload key

    static readonly bool[] wasDown = new bool[256];

    // RA01: whether a key is held down right now. GetAsyncKeyState reports the key's state at this moment, whatever window has focus.
    // Cost - CPU: extremely low (one GetAsyncKeyState call, about a microsecond, per key per headset frame); GPU: none (no GPU work).
    public static bool Held(int key) => (Win32.GetAsyncKeyState(key) & 0x8000) != 0;

    // RA01: true once per press: when the key goes from up to down since the last time it was asked about.
    // Cost - CPU: extremely low (one Held call and two array accesses, per key per headset frame); GPU: none (no GPU work).
    public static bool Pressed(int key)
    {
        bool down = Held(key);
        bool pressed = down && !wasDown[key];
        wasDown[key] = down;
        return pressed;
    }

    // RA01: forgets every key's old state (the headset session stopped, and no key is asked about while it is stopped).
    // Cost - CPU: extremely low (clears 256 flags, only when the headset session stops); GPU: none (no GPU work).
    public static void Forget() => Array.Clear(wasDown);
}
