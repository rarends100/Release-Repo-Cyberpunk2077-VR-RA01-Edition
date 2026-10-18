using RA01.Core;

namespace RA01.Actions;

// RA01: M27-1 - GESTURE MODE (VR Controls tab "Gesture mode", on by default; Rob's design 2026-10-08). Hold B for 5 seconds to switch it on, 5 seconds again to
// switch it off; a big banner says which (Core\Banner.cs). While it is on, your grips and triggers pose V's fingers instead of reaching the game:
//   grip + trigger = middle finger, grip only = pointing finger, trigger only = thumbs up, nothing pressed = open hand - each hand on its own.
// B ITSELF: with this row on, B reaches the game only as a quick tap (under 0.4 s, sent as you let go) while you play, so holding it never makes V do
// anything (ControllerMapping.HoldBackB). Game menus, the F10 window, vehicles and dialogue/cinematic scenes get B exactly as before, and the 5 s hold
// does nothing there.
// SAFETY: drawing a weapon (gun, blade, cyberware - not bare fists) switches gesture mode off at once, so your trigger always shoots when it matters; it
// also pauses (without switching off) in vehicles, menus and the F10 window. The mod's own taps and holds (a physical melee strike) still reach the game.
// The fingers themselves are posed by Visuals\VRIK.cs (each finger a dimmer between the open hand and the game's own fist); the steering wheel and the off
// hand on a weapon keep priority over a gesture.
// Builder picture: a gearbox with a "hand signals" gear - while it is engaged, the levers in your hands move V's fingers instead of the machine.
// Cost - CPU: extremely low (a few compares per controller read, about 60 a second); GPU: none (the banner is rated in Core\Banner.cs).
internal static class GestureMode
{
    const long ToggleMs = 5000;              // RA01: Rob - hold B this long
    const float Pressed = 0.5f;              // RA01: a grip or trigger past this counts as pressed
    const int SceneTierDialogue = 3;         // RA01: GameAnchor.SceneTier 3+ = dialogue and cinematic scenes: B stays the game's there

    static bool active, latched;             // RA01: gesture mode on; this B hold has already toggled (let go to toggle again)
    static long bHeldSince;
    static readonly float[] fingers = new float[10]; // RA01: per hand (left 0-4, right 5-9): index, middle, ring, pinky, thumb - 0 open, 1 the game's fist

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: Visuals (VRIK), per frame: this hand's five finger curls while gesture mode poses it (false = leave the hand alone). 'left' = the left hand.
    // Cost - CPU: extremely low (five reads); GPU: none.
    public static bool TryFingers(bool left, Span<float> five)
    {
        if (!Posing) return false;
        int at = left ? 0 : 5;
        for (int i = 0; i < 5; i++) five[i] = fingers[at + i];
        return true;
    }

    // RA01: gesture mode is on and may pose the hands right now (not paused). Cost - CPU: extremely low (a few reads); GPU: none.
    public static bool Posing => active && Tuning.GestureMode && InPlay && !VehicleState.Mounted;

    // RA01: in free play: no game menu, no F10 window, no dialogue or cinematic scene. M71 - and not in a braindance, so B (holds included) reaches the
    // RA01: braindance as it would without the mod. Cost - CPU: extremely low (four reads); GPU: none.
    static bool InPlay => !MenuSense.ShowsMenu && !SettingsMenu.Open && GameAnchor.SceneTier < SceneTierDialogue && !BlackboardProbe.InBraindance;

    // RA01: one look: the B hold (toggle), the safety switch-off, what Core holds back, and each hand's gesture. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        bool usable = Tuning.GestureMode && InPlay && !VehicleState.Mounted;
        ControllerMapping.HoldBackB(usable);
        long now = Environment.TickCount64;
        bool b = usable && ControllerMapping.BButton;                   // RA01: M28-2 - whichever button is the game's B (left-hand mode: your left Y)
        if (!b) { bHeldSince = 0; latched = false; }
        else if (bHeldSince == 0) bHeldSince = now;
        else if (!latched && now - bHeldSince >= ToggleMs)
        {
            latched = true;
            if (!active && WeaponOut()) Banner.Show("HOLSTER YOUR WEAPON FOR GESTURE MODE"); // RA01: it would switch itself straight off again
            else Switch(!active, "B held 5 s");
        }

        if (active && (!Tuning.GestureMode || WeaponOut())) Switch(false, Tuning.GestureMode ? "a weapon was drawn" : "switched off in the menu");
        ControllerMapping.HoldBackHands(Posing);
        if (!Posing) return;
        Pose(0, Controllers.LeftGrip > Pressed, Controllers.LeftTrigger > Pressed);
        Pose(5, Controllers.RightGrip > Pressed, Controllers.RightTrigger > Pressed);
    }

    // RA01: one hand's gesture from its grip and trigger (Rob's table, see the class comment). Cost - CPU: extremely low (five stores); GPU: none.
    static void Pose(int at, bool grip, bool trigger)
    {
        float index = 1, middle = 1, rest = 1, thumb = 1;              // RA01: start from a fist
        if (grip && trigger) middle = 0;                               // RA01: middle finger
        else if (grip) index = 0;                                      // RA01: pointing finger
        else if (trigger) thumb = 0;                                   // RA01: thumbs up
        else index = middle = rest = thumb = 0;                        // RA01: open hand
        fingers[at] = index; fingers[at + 1] = middle; fingers[at + 2] = rest; fingers[at + 3] = rest; fingers[at + 4] = thumb;
    }

    // RA01: a weapon (gun, blade, cyberware, launcher) is out - bare fists and empty hands are not. Cost - CPU: extremely low (one read); GPU: none.
    static bool WeaponOut() => WeaponProbe.Held is not (WeaponProbe.Kind.None or WeaponProbe.Kind.Fists);

    // RA01: switches gesture mode on or off: the banner, the log line. Cost - CPU: extremely low (a few stores and a log line); GPU: none.
    static void Switch(bool on, string why)
    {
        if (active == on) return;
        active = on;
        Banner.Show(on ? "GESTURE MODE ACTIVATED" : "GESTURE MODE DEACTIVATED");
        Log.Write($"RA01 gesture mode: {(on ? "ON" : "off")} ({why})");
    }
}
