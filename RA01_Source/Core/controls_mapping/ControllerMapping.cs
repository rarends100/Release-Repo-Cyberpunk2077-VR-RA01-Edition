namespace RA01.Core;

// RA01: M6-0, layer 2's mapping table - turns what Controllers.cs (layer 1) is tracking into the shape a real Xbox-style controller reports (XINPUT_GAMEPAD/XINPUT_STATE), so
// XInputHook.cs can hand the game one directly. This file only builds that shape from already-tracked values; XInputHook.cs is the only thing that touches the game's own
// import table or intercepts anything, the same split Keys.cs (keyboard) already has from the hooks that use it.
// the D-pad quirk lives here too: Touch has no physical D-pad, so holding the right thumbstick in turns the left thumbstick's own motion into the four D-pad presses instead
// of its usual axes, until the right thumbstick is let go again. Held this way, the click is never sent to the game (M11-2b: a QUICK click with the
// left stick untouched is sent as a short R3 press - the game's crouch - see QuickClick).
// the menu-chord quirk (added 2026-09-22): holding X and Y together turns the right thumbstick into a shortcut for two of the game's own menus - up presses Start (opens
// the pause menu), down presses Back/View (opens the game menu - inventory, character, crafting, map, journal) - without needing the right stick held in too. Y is swallowed
// while X is also held, the same reasoning as the right stick's click above; X keeps registering normally alongside whichever of the two this fires.
// M6-1 (2026-09-22): the first two drops of this both guessed wrong and neither guess involved Start or Back at all. The very first had up/down sending X+DpadUp/X+DpadDown
// backwards from Rob's own theory; the second still assumed that X+D-pad theory was right, just swapped which pairing it was. A look at the game's real Xbox bindings found
// neither is true - D-pad up/down are bound to dialogue scrolling and aim zoom, nothing to do with either menu; Start alone opens the pause menu and Back alone opens the
// game menu, confirmed against two independent controls references. Not yet tested on a real headset.
// M20-1 - THE MENU CHORD IS NOW X + LT (was X + Y): Y is the game's weapon swap, so every trip to a menu swapped V's gun. Hold X, then squeeze LT
// (in that order - LT first and then X is aiming and reloading, not the chord) and push the right stick: up = game menu, down = pause menu (Rob 2026-10-12, as seen in game), left = the F10
// window. While the chord is held the game sees no LT (no aiming); Y is never swallowed any more. X itself still reaches the game, as before.
// M9-8 - "body follows head" is now by neck ANGLE with a dead zone, not head speed - see BodyFollowTurn. The right stick's up/down is not passed on with motion controls.
// "body follows head" (added 2026-09-22, reworked the same day as M6-1): the headset's rendered picture is always head-locked
// already, but the game's OWN notion of which way the player is facing (what everything else - aim, footsteps, later the arm IK - actually reads) only moves when something
// turns it. This adds the head's own turning to the right stick's X axis, exactly like a second hand sharing the same lever the physical stick already pushes, so a real
// head turn does the same thing a stick turn would. It STACKS with the physical right stick rather than replacing it (Rob's call: some players have tight real-world VR space
// and still need to turn by hand), never fights it (see the M6-1 note below for why the first version did), and only applies when the right stick is not already busy with
// the D-pad or menu-chord quirks above. The sign below (turning the stick the same way the head turned) is the expected one but has not been confirmed on a real headset yet -
// flip BodyFollowSign if a test shows it turning the body away from the head instead of toward it.
// M11-2b - R3 (the right stick clicked, the game's crouch) reaches the game again: a QUICK click (pressed and let go within QuickClickMs, the left stick not
// used meanwhile) sends a short R3 press when it is let go; held with the left stick moved, it stays the D-pad modifier and no R3 is sent. TapR3 lets another
// part of the mod press R3 for a moment, and AddBeforeBuild lets registered functions run just before each build - this file stays neutral: it presses a
// button when asked and knows nothing about crouching (Actions\PhysicalCrouch.cs registers both). (M11-2's "clean B" is gone: B isn't the crouch.)
// M13-9 - the same neutral presses for physical melee (Actions\MeleeActions.cs): HoldLeftTrigger (block), TapRightTrigger (strike), TapLeftShoulder (gun bash) -
// always added on top of your own controls, never replacing them.
// Cost - CPU: extremely low (reads thirteen already-tracked fields plus one CameraHook read, a handful of comparisons/multiplies, once per XInputGetState call the game makes for controller slot 0); GPU: none.
internal static class ControllerMapping
{
    // RA01: XINPUT_GAMEPAD then XINPUT_STATE, same field order as the Windows SDK's xinput.h - this is the exact byte shape XInputGetState hands back, so the game reads it correctly.
    public struct Gamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; }
    public struct State { public uint PacketNumber; public Gamepad Gamepad; }

    // RA01: the XINPUT_GAMEPAD_* button bits, straight from xinput.h - which bit means which button.
    const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    const ushort Start = 0x0010, Back = 0x0020; // RA01: in this game (Rob 2026-10-12, tested in game): Start = the game menu (inventory/character/crafting/map/journal), Back (View) = the pause menu - the 2026-09-22 research note had them the other way round
    const ushort LeftThumb = 0x0040; // RA01: the left stick clicked straight down (L3 on Xbox) - the stick's own tilt is separate, always in ThumbLX/ThumbLY below, never in this Buttons bitmask
    const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200;
    const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    const float GripPressThreshold = 0.5f;         // RA01: grip squeeze past halfway reads as LB/RB down - shoulder buttons are digital, grip squeeze is analog, so this is where it turns into a press
    const float StickPressDeadzone = 0.4f;         // RA01: how far a stick has to move, while the D-pad or menu-chord quirk is repurposing it, before a direction counts as pressed
    const float BodyFollowSign = -1f;              // RA01: -1 pushes the stick the same way the head turned (the expected sign - see the class comment above); flip to +1 if a real test shows it turning the wrong way
    const float CatchUpDoneDeg = 5f;               // RA01: M9-8 - once catching up, V keeps turning until your neck is back within this many degrees of straight
    const float CatchUpFullStickDeg = 45f;         // RA01: M9-8 - a neck angle this big pushes the stick fully over; smaller angles push less, easing in as V lines up
    const float CatchUpMinStick = 0.3f;            // RA01: M9-8 - the push never drops below this while catching up, so the game's own stick dead zone can't stall V a few degrees short
    const float PhysicalStickBusy = 0.1f;          // RA01: M9-8 - the physical right stick held past this: the catch-up waits (your hand always wins)
    static bool catchingUp;                        // RA01: M9-8 - true from passing the dead zone until V has lined up under your head again (only touched on the game's XInput thread)
    const ushort RightThumb = 0x0080;              // RA01: M11-2b - R3, the right stick clicked: the game's crouch
    const long QuickClickMs = 300;                 // RA01: M11-2b - a right-stick click let go within this, with the left stick untouched, is a crouch press
    const int R3TapMs = 120;                       // RA01: M11-2b - how long a crouch press lasts
    static long tapR3Until;                        // RA01: M11-2b - Environment.TickCount64 until which R3 is pressed (TapR3)
    static bool clickWasDown, clickUsedForDpad;    // RA01: M11-2b - the right stick click's state at the last build, and whether the left stick was used while it was held
    static long clickDownAt;
    static readonly nint[] beforeBuild = new nint[12]; // RA01: M11-2, M13-2 - the functions run just before each build (AddBeforeBuild), at most twelve (M19-1 six; M22-1 eight; M24 twelve)
    static volatile bool holdAim;                  // RA01: M35-2 - LT held fully down while the gun is shouldered (HoldAim: Actions\ShoulderScope.cs)
    static volatile bool holdLeftTrigger;          // RA01: M13-9 - the left trigger held fully down (HoldLeftTrigger: physical melee's block)
    static long tapRightTriggerUntil, tapLeftShoulderUntil; // RA01: M13-9 - right trigger / LB pressed until these times (melee strike, gun bash)
    static long tapRightTriggerFrom;               // RA01: M14-4 - and the right trigger's tap starts no earlier than this
    static bool aimStickWas, aimStickWait;
    static volatile bool holdBackShoulders;        // RA01: M18-0 - LB/RB kept from the game (the off hand is reaching for or holding the gun)

    // RA01: M18-0 - Actions (TwoHandGrip): keep LB and RB from the game (on) or let them through again (off). Logged on a change. A gun-bash tap still goes through.
    // Cost - CPU: extremely low (a compare, a store and a log line on a change); GPU: none.
    public static void HoldBackShoulders(bool on, string why)
    {
        if (holdBackShoulders == on) return;
        holdBackShoulders = on;
        Log.Write($"RA01 LB/RB {(on ? "held back" : "let through")}: {why}");
    }         // RA01: M16-B - the right stick was the aiming D-pad at the last build; it waits to be centred after a role change

    static volatile bool steerOn;                  // RA01: M19-1 - physical steering owns the grips and the left stick's left/right (SetSteer)
    static volatile float steerX;
    static volatile int steerFreeGrip = -1;        // RA01: M20-3 - a grip that stays a button while steering (0 left = LB, 1 right = RB; -1 none): the gun hand, driving with a gun out
    static volatile bool steerCarGuns;             // RA01: M84 - the car's own guns are in use (SetSteer): A fires them (RB)
    static long steerAt;
    const long SteerStaleMs = 150;                 // RA01: M19-1 - no SetSteer for this long (VRIK stopped): the controls go back to normal by themselves

    // RA01: M20-3c - DRIVING WITH A GUN OUT: RT and RB swap - your gun hand's TRIGGER presses RB and its GRIP squeeze is RT (the throttle, as deep as you
    // squeeze), so your index finger is free for the gun and the hand holding the gun works the pedal. M28-4 - left-hand mode mirrors it: your LEFT
    // trigger is RB, your LEFT grip is RT, and your RIGHT (off-hand) trigger is LT; LB stays quiet (that grip is the throttle).
    // M19-1 - Actions (PhysicalSteering), every frame while V is seated: on = the grips hold the wheel, so they are never LB/RB, and the left stick's
    // left/right is 'x' (-1 left .. 1 right) unless you push the real stick past PhysicalStickBusy (your thumb always wins). Off = everything back to normal.
    // M84 - THE CAR'S OWN GUNS (Rob 2026-10-15): 'carGuns' = drive-by mode with V's hands empty (both hands on the wheel, M83). The game fires in drive-by
    // mode with RB (RT stays the throttle), and with both grips on the wheel nothing pressed it - so then A is sent as RB: hold A to fire. A's own job in a car
    // (the handbrake) is covered by LT, which brakes and reverses. Only in that mode; everywhere else A is A.
    // Logged on a change. Cost - CPU: extremely low (a clock read, four stores, a log line on a change); GPU: none.
    public static void SetSteer(bool on, float x, int freeGrip = -1, bool carGuns = false)
    {
        Volatile.Write(ref steerAt, Environment.TickCount64);
        steerX = x;
        steerFreeGrip = freeGrip;
        if (carGuns != steerCarGuns) { steerCarGuns = carGuns; Log.Write(carGuns ? "RA01 controls: the car's own guns - A fires them (sent as RB) (M84)" : "RA01 controls: A is A again (M84)"); }
        if (steerOn == on) return;
        steerOn = on;
        Log.Write(on ? "RA01 controls: physical steering - the grips hold the wheel (no LB/RB), your hands steer the left stick's left/right" : "RA01 controls: physical steering off - grips and left stick back to normal");
    }

    static volatile bool swimOn;                   // RA01: M22-1 - physical swimming owns the left stick's forward push and adds a turn (SetSwim)
    static volatile float swimForward, swimTurn;
    static long swimAt;

    // RA01: M22-1 - Actions (PhysicalSwim), every controller read while V is in the water: on = the left stick's forward is 'forward' (0..1) unless you push the
    // real left stick past PhysicalStickBusy (your thumb always wins), and 'turn' (-1 left .. 1 right) is added onto the right stick's left/right like a
    // stick turn. Off = everything back to normal. Goes quiet for SteerStaleMs = off by itself. Logged on a change.
    // Cost - CPU: extremely low (a clock read, three stores, a log line on a change); GPU: none.
    public static void SetSwim(bool on, float forward, float turn)
    {
        Volatile.Write(ref swimAt, Environment.TickCount64);
        swimForward = forward;
        swimTurn = turn;
        if (swimOn == on) return;
        swimOn = on;
        Log.Write(on ? "RA01 controls: physical swimming - strokes push the left stick forward, side pushes turn" : "RA01 controls: physical swimming off - sticks back to normal");
    }

    static volatile bool holdBackB, holdBackHands;   // RA01: M27-1 - B only as a tap (HoldBackB); the triggers and grips kept from the game (HoldBackHands)
    static bool bWasDown;
    static long bDownAt, tapBUntil;
    const long BTapMs = 400, BTapLengthMs = 120;      // RA01: M27-1 - a B press shorter than this reaches the game (as a tap this long, sent on release)

    // RA01: M27-1 - Actions (GestureMode): on = in play (not a game menu or the F10 window), B reaches the game only as a short tap sent when you let go of a
    // press under BTapMs, so a long hold (gesture mode's 5 s) never makes V do anything. Off = B passes straight through, as before. Logged on a change.
    // Cost - CPU: extremely low (a compare, a store and a log line on a change); GPU: none.
    public static void HoldBackB(bool on)
    {
        if (holdBackB == on) return;
        holdBackB = on;
        Log.Write(on ? "RA01 controls: B reaches the game as a quick tap only (a long hold is for the mod)" : "RA01 controls: B passes straight through again");
    }

    // RA01: M27-1 - Actions (GestureMode): on = your triggers and grips pose fingers and never reach the game (no LT/RT/LB/RB; the mod's own taps and holds -
    // a melee strike, a block - still do). Only in play: game menus and the F10 window always get them. Logged on a change.
    // Cost - CPU: extremely low (a compare, a store and a log line on a change); GPU: none.
    public static void HoldBackHands(bool on)
    {
        if (holdBackHands == on) return;
        holdBackHands = on;
        Log.Write(on ? "RA01 controls: triggers and grips held back from the game (gesture mode)" : "RA01 controls: triggers and grips reach the game again");
    }

    // RA01: M27-1 - HoldBackHands in force right now (in play, not a menu or the F10 window). Cost - CPU: extremely low (three reads); GPU: none.
    static bool HandsHeldBack => holdBackHands && !MenuSense.ShowsMenu && !SettingsMenu.Open;

    static volatile bool climbOn;                  // RA01: M24-3 - physical ladder climbing owns the left stick's up/down and the grips (SetClimb)
    static volatile float climbPush;
    static long climbAt;
    static bool remoteViewWas;                     // RA01: M24-1 - the right stick was in braindance / camera mode at the last build

    // RA01: M24-3 - Actions (PhysicalLadder), every controller read while V is on a ladder: on = the left stick's up/down is 'push' (-1 down .. 1 up) unless
    // you push the real stick past PhysicalStickBusy (your thumb always wins), and the grips hold the rungs - they are never LB/RB. Off = all back to
    // normal. Goes quiet for SteerStaleMs = off by itself. Logged on a change.
    // Cost - CPU: extremely low (a clock read, two stores, a log line on a change); GPU: none.
    public static void SetClimb(bool on, float push)
    {
        Volatile.Write(ref climbAt, Environment.TickCount64);
        climbPush = push;
        if (climbOn == on) return;
        climbOn = on;
        Log.Write(on ? "RA01 controls: physical ladder - grip and pull to climb (the grips hold the rungs, no LB/RB)" : "RA01 controls: physical ladder off - grips and left stick back to normal");
    }

    // RA01: M24-3 - physical ladder climbing in charge this build. Cost - CPU: extremely low (two reads and a compare); GPU: none.
    static bool Climbing => climbOn && Environment.TickCount64 - Volatile.Read(ref climbAt) < SteerStaleMs;

    // RA01: M24-1 - is the view a braindance's or a taken-over camera's (BlackboardProbe.RemoteView)? Logged on a change. Not in the F10 window.
    // Cost - CPU: extremely low (a few reads, a log line on a change); GPU: none.
    static bool RemoteView()
    {
        bool on = BlackboardProbe.RemoteView && !SettingsMenu.Open;
        if (on != remoteViewWas)
        {
            remoteViewWas = on;
            Log.Write(on ? $"RA01 controls: {(BlackboardProbe.InBraindance ? "braindance" : "taken-over camera")} - the right stick looks up/down too, body follow and the camera height shift pause" : "RA01 controls: own view again - right stick and camera height back to normal");
        }
        return on;
    }

    // RA01: M79 - IN A TANK (the Basilisk, VehicleState Kind.Tank; Rob 2026-10-14): it is always third person and its gun aims wherever the game's camera points,
    // which your head can't tilt - so the right stick's up/down always reaches the game (as in a braindance, M24-1), and the aim-stick D-pad swap (M16-B)
    // stays off. Left/right, steering and everything else unchanged. Logs when it starts and stops.
    // Cost - CPU: extremely low (two cached reads and a compare per controller read); GPU: none.
    static bool InTank()
    {
        bool on = VehicleState.Mounted && VehicleState.Current == VehicleState.Kind.Tank;
        if (on != tankWas) { tankWas = on; Log.Write(on ? "RA01 controls: in a tank (Basilisk) - the right stick looks up/down too, no D-pad swap while aiming (M79)" : "RA01 controls: out of the tank - right stick back to normal (M79)"); }
        return on;
    }
    static bool tankWas;

    // RA01: M86 - AN ARCADE GAME (Rob 2026-10-15): the arcade machines' games shoot with X - the same hand as the moving stick. While one runs (the game says a
    // menu is open AND V is using a device - BlackboardProbe; a plain menu never has the device switch on) your RT trigger (the left one in left-hand mode)
    // presses X, and the game gets no RT. X itself still works as before; nothing else changes. Not while the F10 window is open. Logs when it starts and stops.
    // Cost - CPU: extremely low (three reads and a compare per controller read); GPU: none.
    static bool ArcadeGame()
    {
        bool on = BlackboardProbe.GameMenu && BlackboardProbe.InteractingWithDevice && !SettingsMenu.Open;
        if (on != arcadeWas) { arcadeWas = on; Log.Write(on ? "RA01 controls: arcade game - the trigger shoots (sent as X); X works as before (M86)" : "RA01 controls: arcade over - the trigger is RT again (M86)"); }
        return on;
    }
    static bool arcadeWas;

    // RA01: M22-1 - physical swimming in charge this build (on, and heard from within SteerStaleMs). Cost - CPU: extremely low (two reads and a compare); GPU: none.
    static bool Swimming => swimOn && Environment.TickCount64 - Volatile.Read(ref swimAt) < SteerStaleMs;

    // RA01: M19-1 - physical steering in charge this build (on, and heard from within SteerStaleMs). Cost - CPU: extremely low (two reads and a compare); GPU: none.
    static bool Steering => steerOn && Environment.TickCount64 - Volatile.Read(ref steerAt) < SteerStaleMs;

    // RA01: M11-2, M13-2 - registers a function run just before each build, on the game's XInput thread (Actions' PhysicalCrouch and WeaponRules). Call once each at start-up.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void AddBeforeBuild(delegate*<void> function)
    {
        for (int i = 0; i < beforeBuild.Length; i++) if (beforeBuild[i] == 0) { beforeBuild[i] = (nint)function; return; }
        Log.Write("RA01 controls: NO ROOM for another before-build function - raise beforeBuild's size"); // RA01: M24 - it used to fail silently
    }

    // RA01: M11-2b - presses R3 (the game's crouch) for 'ms' milliseconds. 'why' goes in the log.
    // Cost - CPU: extremely low (one clock read, one store and a log line); GPU: none.
    public static void TapR3(int ms, string why)
    {
        Volatile.Write(ref tapR3Until, Environment.TickCount64 + ms);
        Log.Write($"RA01 R3 (crouch) pressed: {why}");
    }

    // RA01: M13-9 - holds the left trigger fully down (on) or lets it go back to your own finger (off). 'why' goes in the log when it changes.
    // Cost - CPU: extremely low (a store and a log line); GPU: none.
    public static void HoldLeftTrigger(bool on, string why)
    {
        if (holdLeftTrigger == on) return;
        holdLeftTrigger = on;
        Log.Write($"RA01 left trigger {(on ? "held" : "let go")}: {why}");
    }

    // RA01: M35-2 - holds LT fully down while the gun is shouldered (on) or lets it go back to your own finger (off). Its own switch, so melee's block never
    // lets it go. 'why' goes in the log when it changes. Cost - CPU: extremely low (a store and a log line); GPU: none.
    public static void HoldAim(bool on, string why)
    {
        if (holdAim == on) return;
        holdAim = on;
        Log.Write($"RA01 left trigger {(on ? "held" : "let go")}: {why}");
    }

    // RA01: M27-2 - holds the right trigger fully down (on) or lets it go back to your own finger (off) - Actions' power attack (charge while held, the strong
    // attack goes on release). 'why' goes in the log when it changes. Cost - CPU: extremely low (a store and a log line); GPU: none.
    public static void HoldRightTrigger(bool on, string why)
    {
        if (holdRightTrigger == on) return;
        holdRightTrigger = on;
        Log.Write($"RA01 right trigger {(on ? "held" : "let go")}: {why}");
    }
    static volatile bool holdRightTrigger;

    // RA01: M27-5 - presses X for 'ms' milliseconds (Actions' physical reload: the game's reload button). 'why' goes in the log.
    // Cost - CPU: extremely low (one clock read, one store and a log line); GPU: none.
    public static void TapX(int ms, string why)
    {
        Volatile.Write(ref tapXUntil, Environment.TickCount64 + ms);
        Log.Write($"RA01 X tapped: {why}");
    }
    static long tapXUntil;

    // RA01: M30-1 - Actions (PhysicalSteering's physical horn): holds X - the game's horn while driving (M30-1b, Rob's test: not L3, which starts auto drive) -
    // for HornHoldMs from now. Called every frame while a
    // palm presses the steering wheel's hub, so it lets go by itself within HornHoldMs once the calls stop (steering off, out of the car, a menu): it
    // can't stick on. Logged by PhysicalSteering. Cost - CPU: extremely low (one clock read and one store); GPU: none.
    public static void HoldHorn() => Volatile.Write(ref hornUntil, Environment.TickCount64 + HornHoldMs);
    static long hornUntil;
    const int HornHoldMs = 100;

    // RA01: M51 - keeps your own finger on the game's LT from the game (on) or lets it through again (off): physical reloading, while you reload with the
    // off hand (its trigger pulls and carries magazines then). The mod's own LT holds (block, shoulder aim) still go through. Logged on a change.
    // Cost - CPU: extremely low (a compare, a store and a log line on a change); GPU: none.
    public static void HoldBackAimTrigger(bool on, string why)
    {
        if (holdBackAimTrigger == on) return;
        holdBackAimTrigger = on;
        Log.Write($"RA01 off-hand trigger (LT) {(on ? "held back" : "let through")}: {why}");
    }
    static volatile bool holdBackAimTrigger;

    // RA01: M27-5 - keeps ONE hand's grip from the game (0 left = LB, 1 right = RB), separate from HoldBackShoulders so two features never undo each other
    // (physical reload: the off hand at the mag pouch or carrying a magazine must not press LB - a long LB press is the scanner). Logged on a change.
    // Cost - CPU: extremely low (a compare, a store and a log line on a change); GPU: none.
    public static void HoldBackGrip(int hand, bool on, string why)
    {
        if (holdGrip[hand] == on) return;
        holdGrip[hand] = on;
        Log.Write($"RA01 {(hand == 0 ? "left grip (LB)" : "right grip (RB)")} {(on ? "held back" : "let through")}: {why}");
    }
    static readonly bool[] holdGrip = new bool[2];

    // RA01: M28-2/3 - LEFT-HAND CONTROLS: which of your physical controls plays which game button (VR Controls tab "Left-hand mode"; Rob 2026-10-08: one switch,
    // RA01: everything mirrors). With it on: the TRIGGERS swap (your left trigger is the game's RT - fire / attack), the GRIPS swap (LB / RB), the FACE BUTTONS swap
    // RA01: sides (A and X, B and Y) and the STICKS swap with their clicks (move right, turn left). Triggers and grips keep their normal places in menus and the F10
    // RA01: window (tab paging stays left = left); in vehicles the triggers stay swapped (M28-5, Rob: left trigger = throttle, right = brake / reverse, the
    // RA01: same side as driving with a gun out) but the grips don't; face buttons and sticks stay swapped everywhere. The settings window
    // RA01: chord (X then LT, then the turning stick) moves with them, so it stays on one hand: right A, right trigger, left stick.
    // RA01: Everything after these few lines is unchanged: it just reads these instead of the raw controls.
    // RA01: Builder picture: re-labelling the switches on a panel instead of rewiring the machine behind it.
    // RA01: Cost - CPU: extremely low (a few setting reads and compares per controller read); GPU: none.
    static bool InPlay => !MenuSense.ShowsMenu && !SettingsMenu.Open && !VehicleState.Mounted;
    static bool TriggersSwapped => Tuning.LeftHanded && !MenuSense.ShowsMenu && !SettingsMenu.Open; // RA01: M28-5 - vehicles too (Rob: left trigger = throttle, right = brake / reverse, as with a gun out)
    static bool GripsSwapped => Tuning.LeftHanded && InPlay;
    static bool ButtonsSwapped => Tuning.LeftHanded && !Controllers.FrameActive; // RA01: M50-2 - a Steam Frame has every face button on the right hand, so they keep their labels
    static float LTrig => TriggersSwapped ? Controllers.RightTrigger : Controllers.LeftTrigger;     // RA01: the physical trigger that is the game's LT
    static float RTrig => TriggersSwapped ? Controllers.LeftTrigger : Controllers.RightTrigger;     // RA01: ... and RT
    static float LGrip => GripsSwapped ? Controllers.RightGrip : Controllers.LeftGrip;              // RA01: ... LB
    static float RGrip => GripsSwapped ? Controllers.LeftGrip : Controllers.RightGrip;              // RA01: ... RB
    // RA01: M50-3 - LEFT-HAND MODE PER CONTROLLER (Rob 2026-10-12). Steam Frame: A/B/X/Y (right hand) and the D-pad (left hand) trade places by position
    // RA01: (A = D-pad down, B = right, X = left, Y = up), so the jump sits under the thumb that isn't moving. WMR (no face buttons): the two menu buttons swap
    // RA01: (left menu = B, right menu = Start). Index: Start moves to the right trackpad press. Touch and G2 mirror through the swaps above, as before.
    // RA01: Cost - CPU: extremely low (a few setting reads and compares per controller read); GPU: none.
    static bool FrameSwap => Tuning.LeftHanded && Controllers.FrameActive;
    static bool MenusSwapped => Tuning.LeftHanded && Controllers.CurrentLayout == Controllers.Layout.Wmr;
    static bool XButton => FrameSwap ? Controllers.DpadLeft : (ButtonsSwapped ? Controllers.RightFaceButton : Controllers.LeftFaceButton) || Controllers.FrameX; // RA01: M50-2 - or the Steam Frame's own X (right hand)
    static bool YButton => FrameSwap ? Controllers.DpadUp : MenusSwapped ? false : (ButtonsSwapped ? Controllers.RightUpperFaceButton : Controllers.LeftUpperFaceButton) || Controllers.FrameY;
    static bool AButton => FrameSwap ? Controllers.DpadDown : ButtonsSwapped ? Controllers.LeftFaceButton : Controllers.RightFaceButton;
    static bool BButtonDown => FrameSwap ? Controllers.DpadRight : MenusSwapped ? Controllers.MenuButton : ButtonsSwapped ? Controllers.LeftUpperFaceButton : Controllers.RightUpperFaceButton;
    static bool StartDown => Controllers.CurrentLayout switch                    // RA01: M50-3 - the Start button (the game menu) per layout (see the block above)
    {
        Controllers.Layout.Index => Tuning.LeftHanded ? Controllers.StartButton : Controllers.MenuButton,
        Controllers.Layout.Wmr => Tuning.LeftHanded ? Controllers.RightUpperFaceButton : Controllers.MenuButton,
        _ => Controllers.MenuButton || Controllers.StartButton,                   // RA01: Touch / G2 / not known: the left menu (StartButton is unbound there); Frame: its Menu
    };
    static Xr.Vector2f LStick => Tuning.LeftHanded ? Controllers.RightStick : Controllers.LeftStick;
    static Xr.Vector2f RStick => Tuning.LeftHanded ? Controllers.LeftStick : Controllers.RightStick;
    static bool LClick => Tuning.LeftHanded ? Controllers.RightStickClick : Controllers.LeftStickClick;
    static bool RClick => Tuning.LeftHanded ? Controllers.LeftStickClick : Controllers.RightStickClick;

    // RA01: M28-3 - how to open the settings window with the controllers right now (the menu title bar and the open log line show it).
    // RA01: Cost - CPU: extremely low (a setting read); GPU: none.
    public static string SettingsChordText => Tuning.LeftHanded ? "right A, then right trigger, then left stick left" : "X, then LT, then right stick left";

    // RA01: M28-2 - for Actions and Visuals: your physical trigger that is the game's RT right now (the weapon hand's), how far squeezed 0..1.
    // RA01: Cost - CPU: extremely low (see the block above); GPU: none.
    public static float FireTrigger => RTrig;
    // RA01: M28-2 - your physical button that is the game's X (reload) right now. Cost - CPU: extremely low; GPU: none.
    public static bool ReloadButton => XButton;
    // RA01: M28-2 - your physical button that is the game's B right now (gesture mode's 5 s hold). Cost - CPU: extremely low; GPU: none.
    public static bool BButton => BButtonDown;
    // RA01: M28-2 - your physical stick click that is the game's R3 (crouch) right now. Cost - CPU: extremely low; GPU: none.
    public static bool CrouchClick => RClick;
    // RA01: M28-2 - your physical stick that turns (the right one, or the left in left-hand mode). Cost - CPU: extremely low; GPU: none.
    public static Xr.Vector2f TurnStick => RStick;

    // RA01: M13-64 - true while the game is being sent a left trigger press: your own finger past a light touch, or the melee block's (M16-E: shoulder to aim removed).
    // RA01: Cost - CPU: extremely low (a read and a compare); GPU: none.
    public static bool LeftTriggerSent => holdLeftTrigger || holdAim || (LTrig > 0.1f && !menuChord && !HandsHeldBack && !holdBackAimTrigger);

    // RA01: M20-1 - true while the menu chord is held: X held, and LT squeezed past ChordTrigger while X was already down (see the class comment). Updated each
    // RA01: build; MenuPointer reads it for the F10 window's gesture. Cost - CPU: extremely low (a read); GPU: none.
    public static bool MenuChord => menuChord;
    static volatile bool menuChord;
    static bool chordTriggerWas, xWas;
    const float ChordTrigger = 0.5f;

    // RA01: M20-1 - the chord's latch: LT pressed while X is already held starts it; letting go of X (or LT) ends it.
    // Cost - CPU: extremely low (a few compares per build); GPU: none.
    static void UpdateMenuChord()
    {
        bool x = XButton, lt = (ButtonsSwapped ? Controllers.RightTrigger : Controllers.LeftTrigger) > ChordTrigger; // RA01: M28-2 - the chord stays on one hand (X's hand)
        if (!x || !lt) menuChord = false;
        else if (lt && !chordTriggerWas && xWas) menuChord = true;  // RA01: LT came down while X was already held
        chordTriggerWas = lt;
        xWas = x;
    }

    // RA01: M13-9 - presses the right trigger fully for 'ms' milliseconds (a melee strike). 'why' goes in the log. M14-4: 'afterMs' starts the press that much
    // later (swinging out of a block: the game sees LT let go first, then RT).
    // Cost - CPU: extremely low (one clock read, two stores and a log line); GPU: none.
    public static void TapRightTrigger(int ms, string why, int afterMs = 0)
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref tapRightTriggerFrom, now + afterMs);
        Volatile.Write(ref tapRightTriggerUntil, now + afterMs + ms);
        Log.Write($"RA01 right trigger tapped: {why}");
    }

    // RA01: M13-9 - presses LB for 'ms' milliseconds (a short tap - the gun's quick melee; a long press would open the scanner). 'why' goes in the log.
    // Cost - CPU: extremely low (one clock read, one store and a log line); GPU: none.
    public static void TapLeftShoulder(int ms, string why)
    {
        Volatile.Write(ref tapLeftShoulderUntil, Environment.TickCount64 + ms);
        Log.Write($"RA01 LB tapped: {why}");
    }

    // RA01: builds one XINPUT_GAMEPAD from whatever Controllers (layer 1) read this headset frame. Called by XInputHook every time the game asks for controller slot 0.
    // Cost - CPU: extremely low (thirteen field reads, a handful of comparisons and multiplies); GPU: none.
    public static unsafe Gamepad Build()
    {
        foreach (nint f in beforeBuild) if (f != 0) { try { ((delegate*<void>)f)(); } catch { } } // RA01: M11-2, M13-2 - e.g. physical crouch deciding whether to TapR3 this frame
        Gamepad pad = new();

        long tick = Environment.TickCount64;
        UpdateMenuChord();                                                         // RA01: M20-1
        bool chordOnLT = ButtonsSwapped == TriggersSwapped;                        // RA01: M28-2 - the chord's trigger is the one feeding the game's LT (else RT): that one stays quiet while it is held
        bool handsBack = HandsHeldBack;                                            // RA01: M27-1 - gesture mode: the fingers, not the game, get your triggers and grips
        pad.LeftTrigger = holdLeftTrigger || holdAim ? (byte)255 : (ControllerMapping.menuChord && chordOnLT) || handsBack || holdBackAimTrigger ? (byte)0 : (byte)(LTrig * 255f); // RA01: M51 - not your finger while physically reloading // RA01: M13-9 - a held block on top of your own finger; M20-1 - none while the menu chord is held
        pad.RightTrigger = SettingsMenu.Open ? (byte)0 : tick >= Volatile.Read(ref tapRightTriggerFrom) && tick < Volatile.Read(ref tapRightTriggerUntil) ? (byte)255 : holdRightTrigger ? (byte)255 : (ControllerMapping.menuChord && !chordOnLT) || handsBack ? (byte)0 : (byte)(RTrig * 255f); // RA01: M27-2 - a held power attack // RA01: M13-9 - a strike tap
        // RA01: the right trigger doubles as the F10 window's own click while it's open (MenuPointer) - the game must never also see that squeeze as a real
        // RA01: trigger pull, or clicking a slider fires whatever's in your hand. Zeroed here only for the game's copy; MenuPointer reads the trigger from
        // RA01: Controllers directly, a separate wire, so the window's own clicks still work exactly as before.

        bool menuChord = ControllerMapping.menuChord;                              // RA01: M20-1 - X then LT held - see the class comment

        if (XButton || tick < Volatile.Read(ref tapXUntil) || tick < Volatile.Read(ref hornUntil)) pad.Buttons |= X; // RA01: M27-5 - or a physical reload's tap; M30-1b - or the physical horn
        if (YButton) pad.Buttons |= Y; // RA01: M20-1 - never swallowed now (the chord is X + LT)
        if (AButton) pad.Buttons |= Steering && steerCarGuns ? RightShoulder : A; // RA01: M84 - the car's own guns: A fires (RB)
        bool bDown = BButtonDown;                                                // RA01: M27-1 - B, or (HoldBackB, in play) B only as a short tap on release
        if (holdBackB && !MenuSense.ShowsMenu && !SettingsMenu.Open)
        {
            if (bDown && !bWasDown) bDownAt = tick;
            if (!bDown && bWasDown && tick - bDownAt < BTapMs) tapBUntil = tick + BTapLengthMs;
        }
        else if (bDown) pad.Buttons |= B;
        if (tick < tapBUntil) pad.Buttons |= B;
        bWasDown = bDown;
        if (LClick) pad.Buttons |= LeftThumb;
        bool steering = Steering;                                                  // RA01: M19-1
        int gunHand = steering ? steerFreeGrip : -1;                              // RA01: M20-3c, M28-4 - driving with a gun out: the gun hand (0 left, 1 right; see SetSteer)
        bool gunDriving = gunHand >= 0;
        if (gunDriving) pad.RightTrigger = SettingsMenu.Open ? (byte)0 : (byte)((gunHand == 0 ? Controllers.LeftGrip : Controllers.RightGrip) * 255f); // RA01: M20-3c - ...and the gun hand's grip squeeze is RT, the throttle (RT and RB swapped)
        if (gunDriving && gunHand == 0) pad.LeftTrigger = holdLeftTrigger || holdAim ? (byte)255 : menuChord || handsBack ? (byte)0 : (byte)(Controllers.RightTrigger * 255f); // RA01: M28-4 - left gun: your right (off-hand) trigger is LT
        bool climbing = Climbing;                                                  // RA01: M24-3
        bool leftFree = steering ? steerFreeGrip == 0 && !gunDriving : !holdBackShoulders && !climbing && !handsBack && !holdGrip[GripsSwapped ? 1 : 0], rightFree = steering ? steerFreeGrip == 1 : !holdBackShoulders && !climbing && !handsBack && !holdGrip[GripsSwapped ? 0 : 1]; // RA01: M27-1 - not in gesture mode; M27-5 - nor a held-back grip // RA01: M20-3 - driving with a gun out: the gun hand's grip (the pedal) passes even while the off hand two-hands the gun // RA01: M18-0 - not while the off hand reaches for or holds the gun; M19-1 - nor while the grips hold the wheel (M20-3: the gun hand's grip stays a button)
        if ((leftFree && LGrip > GripPressThreshold) || tick < Volatile.Read(ref tapLeftShoulderUntil)) pad.Buttons |= LeftShoulder; // RA01: M13-9 - or a gun-bash tap
        if (gunDriving) { if ((gunHand == 0 ? Controllers.LeftTrigger : Controllers.RightTrigger) > GripPressThreshold) pad.Buttons |= RightShoulder; } // RA01: M20-3c, M28-4 - driving with a gun out: the gun hand's trigger is RB...
        else if (rightFree && RGrip > GripPressThreshold) pad.Buttons |= RightShoulder;
        if (StartDown) pad.Buttons |= Start;                                       // RA01: M50-3 - per layout (Touch: the left menu, as before)
        ViewButton(tick, ref pad);                                                 // RA01: M50-2 - Steam Frame View: tap = Back, held = F10
        bool frameSwap = FrameSwap;                                                // RA01: M50-2 - the Steam Frame's real D-pad (M50-3: its A/B/X/Y in left-hand mode)
        if (frameSwap ? Controllers.FrameY : Controllers.DpadUp) pad.Buttons |= DpadUp;
        if (frameSwap ? Controllers.RightFaceButton : Controllers.DpadDown) pad.Buttons |= DpadDown;
        if (frameSwap ? Controllers.FrameX : Controllers.DpadLeft) pad.Buttons |= DpadLeft;
        if (frameSwap ? Controllers.RightUpperFaceButton : Controllers.DpadRight) pad.Buttons |= DpadRight;
        if (Tuning.LeftHanded && InPlay ? Controllers.RightBumper : Controllers.LeftBumper) pad.Buttons |= LeftShoulder;  // RA01: M50-2 - Frame bumpers only ever press LB / RB
        if (Tuning.LeftHanded && InPlay ? Controllers.LeftBumper : Controllers.RightBumper) pad.Buttons |= RightShoulder; // RA01: (mirrored in left-hand mode, like the grips)
        QuickClick();
        if (Environment.TickCount64 < Volatile.Read(ref tapR3Until)) pad.Buttons |= RightThumb; // RA01: M11-2b - R3 only as a short press (QuickClick / TapR3), never while held as the D-pad modifier

        if (RClick && !Controllers.FrameActive) // RA01: M50-2 - the Steam Frame has a real D-pad: there a right-stick click is only crouch (Rob)
        {
            // RA01: the D-pad quirk - while the right stick is held in, the left stick's own axes are replaced by the four D-pad presses instead of moving normally.
            if (LStick.Y > StickPressDeadzone) pad.Buttons |= DpadUp;
            if (LStick.Y < -StickPressDeadzone) pad.Buttons |= DpadDown;
            if (LStick.X < -StickPressDeadzone) pad.Buttons |= DpadLeft;
            if (LStick.X > StickPressDeadzone) pad.Buttons |= DpadRight;
        }
        else
        {
            pad.ThumbLX = ToAxis(steering && MathF.Abs(LStick.X) <= PhysicalStickBusy ? steerX : LStick.X); // RA01: M19-1 - your hands on the wheel
            bool thumbBusy = MathF.Abs(LStick.X) > PhysicalStickBusy || MathF.Abs(LStick.Y) > PhysicalStickBusy;
            pad.ThumbLY = ToAxis(thumbBusy ? LStick.Y : Swimming ? swimForward : climbing ? climbPush : LStick.Y); // RA01: M22-1 - your strokes (M24-3: or ladder pulls), unless your thumb is on the stick
        }

        if (menuChord)
        {
            // RA01: the menu-chord quirk - while the chord (X then LT, M20-1) is held, the right stick's own axes are replaced: up presses Start (the game menu), down presses Back/View (the pause menu)
            // RA01: - Rob 2026-10-12, as seen in game (this note had them the other way round). X is already set above, so it rides along with whichever of the two this fires - not yet known whether the
            // RA01: game minds seeing X down at the same instant Start or Back opens a menu (see the class comment's M6-1 note); if it causes any hiccup, swallow X here too.
            if (RStick.Y > StickPressDeadzone) pad.Buttons |= Start; // RA01: right stick UP -> the game menu (inventory/character/map/etc.)
            if (RStick.Y < -StickPressDeadzone) pad.Buttons |= Back; // RA01: right stick DOWN -> the pause menu
        }
        else
        {
            bool tank = InTank();                                                  // RA01: M79 - the Basilisk: its gun aims with the game's camera, tilted only by the stick
            bool aimStick = !tank && AimStick(menuChord);
            bool remote = RemoteView();                                            // RA01: M24-1 - a braindance or a taken-over camera: the stick is the only way to look
            pad.ThumbRX = ToAxis(RStick.X + (remote ? 0f : BodyFollowTurn()) + (Swimming ? swimTurn : 0f)); // RA01: M22-1 - plus a swim side-push turn
            if (remote || tank) pad.ThumbRY = ToAxis(RStick.Y);   // RA01: M24-1 - up/down too, whatever "Stick look up/down" says (M79: and in a tank)
            else if (aimStick)
            {
                pad.ThumbRY = 0; // RA01: M16-B - aiming a gun: up and down press D-pad up and down instead (left/right still turn)
                if (!aimStickWait && RStick.Y > StickPressDeadzone) pad.Buttons |= DpadUp;
                if (!aimStickWait && RStick.Y < -StickPressDeadzone) pad.Buttons |= DpadDown;
            }
            else pad.ThumbRY = !aimStickWait && (Tuning.StickPitch || MenuSense.ShowsMenu) ? ToAxis(RStick.Y) : ToAxis(LevelPush()); // RA01: M20-5 - else "Keep view level" // RA01: M9-8 - with motion controls your head alone looks up and down (VR Controls tab "Stick look up/down"); game menus still get it
        }

        if (SettingsMenu.Open && Tuning.LeftHanded) pad.LeftTrigger = 0;          // RA01: M28-4 - left-hand mode: your left trigger clicks the F10 window (MenuPointer), so the game never sees it
        if (ArcadeGame()) { if (RTrig > GripPressThreshold) pad.Buttons |= X; pad.RightTrigger = 0; } // RA01: M86 - an arcade game: the trigger shoots (X); X itself unchanged
        if (SettingsMenu.Open) pad.ThumbLX = pad.ThumbLY = pad.ThumbRX = pad.ThumbRY = 0; // RA01: M8-3 - while the F10 window is open the sticks scroll it (MenuPointer.StickScroll), so the game must not also walk or turn V

        return pad;
    }

    // RA01: M16-B - is the right stick's up/down the D-pad now? Only while V holds a gun and LT is held - aiming down the sights -
    // with the Weapons tab row on, and never in a menu, the F10 window or the menu chord (X + LT). When that changes, up/down does nothing until the stick has come
    // back to centre (a stick still pushed can't suddenly fire a D-pad press or tilt the view) - like a safety catch that resets on the detent.
    // Cost - CPU: extremely low (a few compares per controller read, a log line on a change); GPU: none.
    static bool AimStick(bool menuChord)
    {
        bool on = Tuning.AimStickDpad && LeftTriggerSent && !menuChord && !SettingsMenu.Open && !MenuSense.ShowsMenu && WeaponProbe.Held == WeaponProbe.Kind.Gun; // RA01: the weapon is read only while LT is held
        bool pushed = MathF.Abs(RStick.Y) > StickPressDeadzone;
        if (on != aimStickWas)
        {
            aimStickWas = on;
            aimStickWait = pushed;
            Log.Write(on ? "RA01 controls: aiming a gun - right stick up/down is now D-pad up/down" : "RA01 controls: not aiming - right stick back to normal");
        }
        if (aimStickWait && !pushed) aimStickWait = false;
        return on;
    }

    // RA01: M11-2b - watches the right stick click: pressed -> note when; while held, the left stick pushed past StickPressDeadzone marks it as the D-pad modifier;
    // let go quickly without that -> a short R3 press (the game's crouch). Not while the F10 window is open (the click is the window's there).
    // Cost - CPU: extremely low (a clock read and a few compares per build); GPU: none.
    static void QuickClick()
    {
        bool down = RClick;
        long now = Environment.TickCount64;
        if (down && !clickWasDown) { clickDownAt = now; clickUsedForDpad = false; }
        if (down && !Controllers.FrameActive && (MathF.Abs(LStick.X) > StickPressDeadzone || MathF.Abs(LStick.Y) > StickPressDeadzone)) clickUsedForDpad = true; // RA01: M50-2 - not the D-pad modifier on a Frame
        if (!down && clickWasDown && !clickUsedForDpad && now - clickDownAt < QuickClickMs && !SettingsMenu.Open) TapR3(R3TapMs, "quick right-stick click");
        clickWasDown = down;
    }

    // RA01: M9-8 - "body follows head" by angle, with a dead zone (Rob's diagram, 2026-09-30): inside the VR Controls tab's dead zone (default 40 degrees each side) only
    // your head turns and V's body stays planted; once your neck angle (CameraHook.ReadNeckDegrees: head against V's body) passes it, V's body turns toward your head
    // until it is back under it (within CatchUpDoneDeg), the push easing off as she lines up. CameraHook's turntable keeps the world still meanwhile. Replaces M6's
    // head-SPEED push, which fired on any brisk glance ("turns too soon").
    // The physical right stick always wins: while it is held, the catch-up waits. Returns -1..1, added onto the physical stick's X before ToAxis.
    // Cost - CPU: extremely low (two volatile reads, a few compares and one multiply per XInputGetState call); GPU: none.
    static float BodyFollowTurn()
    {
        float neck = CameraHook.ReadNeckDegrees(out bool valid);
        if (!Tuning.BodyFollowEnabled || !valid || SettingsMenu.Open || MathF.Abs(RStick.X) > PhysicalStickBusy) { catchingUp = false; NoteCatchUp(neck, valid ? "your stick, the F10 window or body follow off" : "no neck reading (menu or no head)"); return 0f; }
        float angle = MathF.Abs(neck);
        if (angle > Tuning.BodyTurnDeadZoneDeg) catchingUp = true;
        else if (angle < CatchUpDoneDeg) catchingUp = false;
        NoteCatchUp(neck, "lined up again");
        if (!catchingUp) return 0f;
        CameraHook.NoteFollowPush();
        return BodyFollowSign * MathF.Sign(neck) * System.Math.Clamp(angle / CatchUpFullStickDeg, CatchUpMinStick, 1f);
    }

    // RA01: M77 LOOK (log only, Rob 2026-10-14: a camera shake in Never Fade Away as Johnny; body-follow the suspect - the neck passed 40 deg just before): each
    // catch-up logged as it starts (neck angle, the push, whose body) and as it ends (how long, why). CameraHook logs the turntable's side once a second.
    // Cost - CPU: extremely low (a compare per controller read; a log line per catch-up); GPU: none.
    static void NoteCatchUp(float neck, string endWhy)
    {
        if (catchingUp == catchUpWas) return;
        catchUpWas = catchingUp;
        long now = Environment.TickCount64;
        string body = GameAnchor.InJohnnysBody ? "Johnny's body" : GameAnchor.OnStandIn ? "a stand-in body" : "V's body";
        if (catchingUp) { catchUpAt = now; Log.Write($"RA01 body follow (M77 look): catching up - neck {neck:F0} deg (dead zone {Tuning.BodyTurnDeadZoneDeg:F0}), push {Math.Clamp(MathF.Abs(neck) / CatchUpFullStickDeg, CatchUpMinStick, 1f):F2}, {body}"); }
        else Log.Write($"RA01 body follow (M77 look): stopped after {(now - catchUpAt) / 1000f:F1} s - {endWhy}, neck {neck:F0} deg, {body}");
    }
    static bool catchUpWas;
    static long catchUpAt;

    // RA01: M20-5 - KEEP VIEW LEVEL (VR Controls tab, on by default): with motion controls the right stick's up/down never reaches the game, so when a cutscene
    // leaves the game's own camera tilted (M20-4's log: 26 degrees down for minutes after one) nothing could tilt it back. While playing on foot (no menu, no
    // vehicle), once the game's camera has been tilted more than LevelStartDeg for LevelWaitMs, this pushes the right stick up or down - harder the further
    // off, never under the game's dead zone - until it is within LevelDoneDeg, like a spirit level on a self-levelling tripod. Your head still does all the
    // real looking. If a push doesn't bring the tilt down within LevelCheckMs (the game's look is inverted, or a limit is reached), the push turns round (logged).
    // M20-9 - never while the camera isn't V's own (GameAnchor.CameraNotV: the Militech training body, a taken-over security camera).
    // M74 - except the Practice Makes Perfect training avatar (GameAnchor.InTrainingSim on a stand-in body): its camera was left tilted for minutes (-48 deg),
    // so looking up hit the game's limit; there it levels like on foot - but not while the training avatar looks through a camera (Rob 2026-10-13).
    // M71 - nor in a braindance (BlackboardProbe.InBraindance), even with "Braindance & camera look" off: there you level the view yourself with the stick
    // (Rob 2026-10-13). On foot it works as before.
    // M71-2 - during a scene (GameAnchor.SceneTier LevelSceneTier+: dialogue, first-person cinematic, cinematic) the scene owns its camera, so levelling
    // first waits for the scene to let go: the camera's tilt must stay within SceneStillDeg for SceneQuietMs (SceneQuiet) before any push. If a push then
    // makes no headway either way (the scene is holding the camera), it backs off and waits for stillness again - one gentle try every few seconds instead
    // of flicking the stick up and down (Rob 2026-10-13). Like a builder plumbing a wall: wait until the crane stops swinging it, and if it is still clamped,
    // step back and try again later rather than wrestle it. On foot the scene rules don't apply.
    // Cost - CPU: extremely low (a volatile read, a clock read and a few compares per controller read); GPU: none.
    static float LevelPush()
    {
        bool trainingBody = GameAnchor.InTrainingSim && GameAnchor.OnStandIn;     // RA01: M74
        if (!Tuning.KeepViewLevel || MenuSense.ShowsMenu || VehicleState.Mounted || (GameAnchor.CameraNotV && !trainingBody) || BlackboardProbe.InBraindance
            || !CameraHook.TryEnginePitch(out float pitch)) { levelSince = sceneQuietSince = 0; StopLevel(null); return 0f; }
        long now = Environment.TickCount64;
        bool scene = GameAnchor.SceneTier >= LevelSceneTier;
        float off = MathF.Abs(pitch);
        if (off < (levelling ? LevelDoneDeg : LevelStartDeg)) { levelSince = sceneQuietSince = 0; StopLevel($"level again ({pitch:F1} deg)"); return 0f; }
        if (!scene) sceneQuietSince = 0;
        else if (!levelling && !SceneQuiet(pitch, now)) { levelSince = 0; return 0f; }
        if (levelSince == 0) levelSince = now;
        if (now - levelSince < LevelWaitMs) return 0f;
        if (!levelling)
        {
            levelling = true; levelReversed = false; levelFrom = off; levelCheckAt = now + LevelCheckMs;
            Log.Write($"RA01 controls: the game's camera is tilted {pitch:F1} deg{(scene ? $" and the scene has held it still for {SceneQuietMs / 1000f:F0} s" : "")} - levelling it with the right stick");
        }
        else if (now >= levelCheckAt)
        {
            if (off > levelFrom - 0.5f)
            {
                if (scene && levelReversed)
                {
                    levelSince = sceneQuietSince = 0; StopLevel(null);
                    Log.Write("RA01 controls: levelling backs off - the scene is holding its camera; waiting for it to stay still again (M71-2)");
                    return 0f;
                }
                levelSign = -levelSign; levelReversed = true;
                Log.Write("RA01 controls: levelling made no headway - pushing the other way (the game's look may be inverted)");
            }
            else levelReversed = false;
            levelFrom = off; levelCheckAt = now + LevelCheckMs;
        }
        return -MathF.Sign(pitch) * levelSign * Math.Clamp(off / LevelFullDeg, LevelMinPush, 1f);
    }

    // RA01: M71-2 - has the scene's camera tilt stayed within SceneStillDeg of where it settled for SceneQuietMs? Any bigger move starts the wait over (logged once
    // per wait). Only asked while not pushing, so the levelling's own movement never counts. Cost - CPU: extremely low (a subtraction and two compares); GPU: none.
    static bool SceneQuiet(float pitch, long now)
    {
        if (sceneQuietSince != 0 && MathF.Abs(pitch - sceneQuietPitch) <= SceneStillDeg) return now - sceneQuietSince >= SceneQuietMs;
        if (sceneQuietSince != 0 && !sceneWaitLogged) { sceneWaitLogged = true; Log.Write("RA01 controls: levelling waits - the scene is moving the camera (M71-2)"); }
        if (sceneQuietSince == 0) sceneWaitLogged = false;
        sceneQuietSince = now; sceneQuietPitch = pitch;
        return false;
    }

    // RA01: M20-5 - ends a levelling push (logged with 'why' when one was running). Cost - CPU: extremely low; GPU: none.
    static void StopLevel(string? why)
    {
        if (!levelling) return;
        levelling = false;
        if (why != null) Log.Write($"RA01 controls: {why}");
    }
    const float LevelStartDeg = 1.5f, LevelDoneDeg = 0.7f, LevelFullDeg = 20f, LevelMinPush = 0.3f;
    const long LevelWaitMs = 750, LevelCheckMs = 400;
    const int LevelSceneTier = 3;          // RA01: M71 - GameAnchor.SceneTier 3 (dialogue) and up (cinematics): the scene rules below apply
    const long SceneQuietMs = 2000;        // RA01: M71-2 - a scene's camera must hold still this long before levelling pushes
    const float SceneStillDeg = 0.5f;      // RA01: M71-2 - a tilt change bigger than this means the scene is still moving its camera
    static bool levelling, levelReversed, sceneWaitLogged; // RA01: M71-2 - levelReversed: the last check already turned the push round
    static float levelSign = 1f, levelFrom, sceneQuietPitch;
    static long sceneQuietSince;
    static long levelSince, levelCheckAt;

    static bool viewWasDown, viewHeld;     // RA01: M50-2 - the View button at the last build, and whether this press already became the F10 hold
    static long viewDownAt, tapBackUntil;
    static volatile bool viewToggle;       // RA01: M50-2 - a View hold waiting for MenuPointer (TakeViewToggle)
    const long ViewHoldMs = 1000, BackTapMs = 120;

    // RA01: M50-2 - THE STEAM FRAME'S VIEW BUTTON (Rob 2026-10-12): a quick tap is the game's Back (the pause menu), sent as a short press when you let go; held for
    // ViewHoldMs it opens or closes the F10 window instead (once per hold) and no Back is sent. Elsewhere ViewButton is always false, so nothing changes.
    // Cost - CPU: extremely low (a few compares per build); GPU: none.
    static void ViewButton(long tick, ref Gamepad pad)
    {
        bool down = Controllers.ViewButton;
        if (down && !viewWasDown) { viewDownAt = tick; viewHeld = false; }
        if (down && !viewHeld && tick - viewDownAt >= ViewHoldMs) { viewHeld = true; viewToggle = true; Log.Write("RA01 controls: View held - settings window (M50-2)"); }
        if (!down && viewWasDown && !viewHeld) tapBackUntil = tick + BackTapMs;
        viewWasDown = down;
        if (tick < tapBackUntil) pad.Buttons |= Back;
    }

    // RA01: M50-2 - true once for each View hold (MenuPointer.TogglePressed opens or closes the F10 window with it).
    // Cost - CPU: extremely low (one read and a store); GPU: none.
    public static bool TakeViewToggle()
    {
        if (!viewToggle) return false;
        viewToggle = false;
        return true;
    }

    // RA01: -1..1 to XInput's -32768..32767 thumbstick range.
    // Cost - CPU: extremely low (one multiply, one clamp and one cast); GPU: none.
    static short ToAxis(float value) => (short)System.Math.Clamp(value * 32767f, -32768f, 32767f);
}
