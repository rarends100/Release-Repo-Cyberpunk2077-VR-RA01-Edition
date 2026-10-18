using static RA01.Core.Win32; // RA01: CursorInfo, Rect, GetCursorInfo, GetClipCursor

namespace RA01.Core;

// RA01: tells a game menu from play by the mouse. The game keeps building cameras in its menus, so "no camera was built" (CameraHook.Building) only finds loading screens. Rob's logs showed the mouse does follow
// the menus: in play the cursor is hidden and the mouse is locked to one point; in the pause menu, inventory and the rest the mouse is free over the whole window (the cursor itself is only shown at game start).
// Entering a menu: the mouse must look free for a third of a second. Leaving one (M5a-7): the mouse must look locked for a third of a second AND the game camera must have moved in that time, or it must look
// locked for 4 seconds in a row. With a controller the game locks the mouse whenever the stick moves, even inside a menu; those blips are short and the camera stands still, so they do not count.
// The game starts as a menu (until the player presses breach). While the F10 window is open the last answer is kept, because the F10 window takes the mouse itself.
// M5a-8 - the answer also decides whether the head steers the game's camera (see ShowsMenu): in a menu the game is a plain screen and must not be moved by the head.
// M20-11 - SCENES: dialogue and some scenes free the mouse too. While V is in a scene (GameAnchor.SceneTier 2 or more) a free mouse only counts as a menu once the game's
// camera has stood perfectly still for SceneStillSeconds - a scene's camera keeps moving (V's head animation, camera moves), while a menu opened over a scene freezes it.
// A shown cursor still means a menu at once.
// M37 - THE GAME'S OWN WORD: while the game says a menu is open (BlackboardProbe.GameMenu, its UI_System.IsInMenu switch) menu mode never ends - a stick
// turning a 3D preview (the weapon mod screen) locks the mouse and moves the picture just like play, which flipped RA01 to play; its levelling push and
// body turn then pushed the stick and zoomed the preview, and kept the mouse locked (it fed itself). Like a "room in use" light on the door instead of
// guessing from footsteps. Safety: if the mouse says play for GameMenuIgnoreSeconds with the camera moving, play wins anyway (logged) - a stuck switch can
// never leave the head unable to look.
// M59 - THE GAME'S WORD ALSO OPENS: sometimes a menu opens and the game never frees the mouse (Rob's log 16:27:49: the switch said OPEN, the camera froze for
// 4 s, no menu mode). So the switch can now bring menu mode on too, but only together with a frozen camera (still for HoldSeconds) - a real menu pauses the
// game, play keeps the camera moving. If the 8 s safety ever overrules the switch, the switch is ignored until it reads closed again (gameMenuOverruled).
// Cost - CPU: extremely low (two Windows calls and a few comparisons once per headset frame, about 90 a second for most headsets, thhough 72, 60, and 120 are possibility as well,  still a negligible impact on CPU performance.); GPU: none.
internal static unsafe class MenuSense
{
    const double HoldSeconds = 0.3, PlaySeconds = 4.0, SceneStillSeconds = 1.0, GameMenuIgnoreSeconds = 8.0; // RA01: M37 - the last one: see the class comment
    const int PointSize = 1;      // RA01: a mouse locked to a point has a clip rectangle no wider or taller than this

    static volatile bool inMenu = true; // RA01: written by the headset thread, read by the game's camera thread (through ShowsMenu)
    static long differsSince;     // RA01: the time the mouse first looked different from 'inMenu' (0 = it does not)
    static bool gameMenuOverruled; // RA01: M59 - the 8 s safety overruled the game's menu switch: ignore it until it reads closed

    public static bool InMenu => inMenu;

    // RA01: M5a-8 - true while the game should be treated as a plain menu screen: the mouse says menu (Auto), or the F10 row / F9 forces it. Then the head must not steer the game's camera and the field of view
    // RA01: is not widened, so the main menu, the pause menu and the inventory look the way they do on a monitor (a 3D backdrop steered by the head zoomed in and out as the head moved). "Game" (Tuning.MenuView 2) says never.
    // RA01: Cost - CPU: extremely low (two static reads, called once per camera build and once per headset frame); GPU: none.
    public static bool ShowsMenu => Tuning.MenuView == 1 || (Tuning.MenuView == 0 && inMenu);

    // RA01: called once per headset frame.
    // Cost - CPU: extremely low (GetCursorInfo and GetClipCursor are quick Windows lookups, well under 10 microseconds together; the log line only runs when the answer flips, every few seconds at most); GPU: none.
    public static void Update(long now)
    {
        if (SettingsMenu.Open) { differsSince = 0; return; }
        CursorInfo info = new() { Size = sizeof(CursorInfo) };
        if (!GetCursorInfo(&info)) return;
        Rect clip = default;
        GetClipCursor(&clip);

        bool shown = (info.Flags & 1) != 0;
        bool free = clip.Right - clip.Left > PointSize || clip.Bottom - clip.Top > PointSize;
        bool scene = !shown && free && GameAnchor.SceneTier >= 2 && CameraHook.MovedSince(now - (long)(SceneStillSeconds * System.Diagnostics.Stopwatch.Frequency)); // RA01: M20-11
        bool gameMenu = BlackboardProbe.GameMenu;
        if (!gameMenu) gameMenuOverruled = false;
        bool gameMenuStill = gameMenu && !gameMenuOverruled && !CameraHook.MovedSince(now - (long)(HoldSeconds * System.Diagnostics.Stopwatch.Frequency)); // RA01: M59
        bool menuSignal = shown || (free && !scene) || gameMenuStill;
        if (menuSignal == inMenu) { differsSince = 0; return; }
        if (differsSince == 0) differsSince = now;
        double held = (now - differsSince) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (held < HoldSeconds) return;
        if (inMenu && held < PlaySeconds && !CameraHook.MovedSince(differsSince)) return; // RA01: looks like play, but the camera has not moved yet: maybe a stick blip inside a menu
        bool gameSaysMenu = inMenu && BlackboardProbe.GameMenu;                                  // RA01: M37 - the game says a menu is open: stay
        if (gameSaysMenu && (held < GameMenuIgnoreSeconds || !CameraHook.MovedSince(now - (long)(1.0 * System.Diagnostics.Stopwatch.Frequency)))) return;

        inMenu = !inMenu;
        differsSince = 0;
        if (gameSaysMenu) gameMenuOverruled = true; // RA01: M59 - so the switch cannot pull the head straight back into menu mode
        Log.Write(inMenu
            ? $"RA01 menu: the game looks like a MENU now ({(shown ? "the mouse cursor is shown" : free ? "the mouse is free though the cursor is hidden" : "the game says a menu is open and its camera has stood still, the mouse is still locked (M59)")}; mouse kept inside {clip.Left},{clip.Top} to {clip.Right},{clip.Bottom})"
            : gameSaysMenu ? $"RA01 menu: the game looks like PLAY now - the game's menu switch still says open after {GameMenuIgnoreSeconds} s of play, ignored"
            : scene ? $"RA01 menu: the game looks like PLAY now (a scene, tier {GameAnchor.SceneTier}: the mouse is free but the camera is moving)" : "RA01 menu: the game looks like PLAY now (the cursor is hidden and the mouse is locked to one point)");
    }
}
