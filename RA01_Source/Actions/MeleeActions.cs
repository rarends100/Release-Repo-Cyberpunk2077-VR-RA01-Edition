using System.Diagnostics;
using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M13-9 - physical melee (Weapons tab "Physical melee", "Swing speed to strike"). Each controller read, your head and both controllers are measured in the
// room (OpenXR's own axes) and turned into "how far in front of my face, how far to the side, how far up" using your head's facing - like a builder
// measuring from his own nose with a tape. From that:
//  - BLOCK holds the left trigger (the game's block) while a guard pose is held, and lets go when you leave it:
//     - bladed melee weapons (Skyrim VR style): the weapon hand in front of your face, the blade held level-ish (within BladeLevelDeg) and crossways (at least
//       BladeCrossDeg away from straight ahead) - a sword held across your face to catch a downward cut;
//     - fists, Gorilla Arms and Mantis Blades: both hands up in front of your face (far enough out that controller rings can't hit the headset) and near each
//       other - a boxer's guard;
//     - Monowire: not attacking (no strike or right trigger lately), your hands at least WireOpen apart, and the line between them tilted diagonally (between
//       WireMinDeg and WireMaxDeg from level) - a garrotte wire held across you at a slant.
//    The pose must hold for EngageMs before the trigger goes down, and has ReleaseMs of grace before it lets go, so the edge of a pose never flickers.
//  - STRIKE taps the right trigger (the game's attack) once when a striking hand moves faster than the swing speed - speed is distance moved between two
//    headset frames divided by the time between them, measured against your head so walking doesn't count. One tap per swing (StrikeGapMs); a swing from the guard drops the block first (M13-20).
//    Melee weapons: the weapon hand only. Fists (M13-34) and melee cyberware (Mantis Blades, Gorilla Arms, Monowire): either hand. The Projectile Launcher: never.
//  - GUN BASH: with a gun drawn, the same swing with the weapon hand gives LB (PlayStation L1) a short tap - the game's quick melee with a gun (a long
//    press of LB is the scanner, so the tap is kept short).
// The weapon hand is the right hand, or the left with Tuning.LeftHanded (for the coming left-hand mode). Your own trigger presses always still work - these
// presses are added on top (ControllerMapping). Nothing happens while a menu or the F10 window is open.
// M13-16 - the blade_slot reach test is gone: a katana read 0-1 cm, the same as fists, so it never got the blade block. Now ANY melee weapon blocks with
// either pose: BladeBlock below, or the two-hands guard. M13-34 - bare fists (now their own kind, from the game's data) use the two-hands guard only.
// M13-9b - runs on its OWN background thread (about 90 looks a second), not inside the game's controller read: run there, it crashed the game the first time
// it switched on. It only sets ControllerMapping's press flags, which the game's controller read picks up; an error here is caught and logged once, never
// reaching the game.
// M13-10 - crash hunt: every stage leaves a CrashWatch breadcrumb (a fixed text, nothing allocated), so a fault anywhere names the stage; a heartbeat line every
// 5 s while active. Off by default (Tuning.PhysicalMelee) until the M13-9 crash is found. M13-11 - the breadcrumbs now go into the black box (CrashWatch.Mark),
// which survives even a silent kill, so the 25 ms pulse is gone.
// M13-18 - M13-15's fist slider and empty-hands fist draw and M13-16/17's finger-read fists are removed for now (the user's call: melee and cyberware
// first); they come back once the whole mod is done.
// M13-16 - the blade block, wider (the user's two poses): the blade across your body, up to BladeLevelDeg from level and at least BladeCrossDeg away from
// straight ahead, with the weapon hand anywhere in a bigger box than the fists' guard - from in front of your face out to arm's length, chest height
// up to just above the eyes (BladeFront/Up/Side below).
// Cost - CPU: extremely low (a few vector sums, dot products and compares about 90 times a second, on our own thread); GPU: none.
internal static unsafe class MeleeActions
{
    // RA01: guard boxes, measured from the centre of your head along your facing (metres)
    const float FrontMin = 0.12f, FrontMax = 0.60f;  // RA01: in front of the face - not closer than this (controller rings vs headset), not further
    const float UpMin = -0.35f, UpMax = 0.20f;       // RA01: from about chin height below to a little above the eyes
    const float SideMax = 0.40f;                     // RA01: not further than this to either side
    const float GuardHandsApart = 0.45f;             // RA01: fists/Gorilla/Mantis: both hands within this of each other
    const float BladeLevelDeg = 50f, BladeCrossDeg = 40f;   // RA01: M13-16 - blade block: the blade within this of level, and at least this away from straight ahead
    const float BladeFrontMin = 0.15f, BladeFrontMax = 0.80f, BladeUpMin = -0.60f, BladeUpMax = 0.25f, BladeSideMax = 0.55f; // RA01: M13-16 - where the weapon hand may be
    const float WireOpen = 0.30f;                    // RA01: Monowire block: hands at least 1 ft apart...
    const float WireMinDeg = 25f, WireMaxDeg = 65f;  // RA01: ...and tilted this far from level
    const long WireQuietMs = 800;                    // RA01: Monowire: no strike or right trigger for this long = not attacking
    const long EngageMs = 30, ReleaseMs = 120;       // RA01: M13-20 / M14-4 / M14-4b - the guard comes up 30 ms after the pose is reached; it lets go after the pose has been gone
                                                     // RA01: 120 ms (50 flickered at the blade block's angle limits) - a swing out of the guard still lets go at once
    const long StrikeGapMs = 250, BashGapMs = 600;   // RA01: M14-4 - strike gap 400 -> 250 ms (and a swing inside the gap is queued, not lost)
    const float RearmSpeed = 1.2f;                   // RA01: M14-4b - after a strike, the hand must slow below this (m/s) before the next swing counts - one swing, one strike
    const int BlockToStrikeMs = 20;                  // RA01: M14-4 - swinging out of the guard: LT lets go, RT goes in this much later (about one game frame)
    const int TapMs = 120;                           // RA01: M14-4 - 80 -> 120 ms, to reach the game's combo window; still short of its hold (power attack) time

    // RA01: M13-12 - last frame's hands, measured from the head (room metres): [0] left, [1] right. Kept in a small array, NOT two static Vector3 fields: the native
    // RA01: compiler (ILC 8.0.31) built the store into the old 'lastLeft' field wrong - its last 4 bytes (Z) were aimed into the DLL's own read-only code, so the
    // RA01: first stored frame ended the game on the spot. That was the M13-9 crash (black box: RA01.dll +0xCDFC3, writing, breadcrumb "melee 6"). An array is
    // RA01: reached through a pointer, a different code path the compiler gets right.
    static readonly Vector3[] lastHands = new Vector3[2];
    static long lastFrameTime;
    static bool blocking;
    static long poseSince, poseGoneSince, nextStrikeMs, lastAttackMs, lastStrikeMs;
    static int started;
    static int traceLeft = 3;                         // RA01: M13-9c - TEMPORARY crash hunt: the first few active looks write numbered steps to the log
    static long looks, nextBeatMs;                    // RA01: M13-10 crash hunt - active looks so far, and when the next heartbeat is due
    static bool errorLogged;

    // RA01: starts the melee thread. Call once at start-up.
    // Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(Loop) { IsBackground = true, Name = "RA01 melee" }.Start();
    }

    // RA01: M13-9b - the melee thread: one look about every 11 ms; any error is caught here and logged once.
    // Cost - CPU: extremely low (see the class comment; asleep between looks); GPU: none.
    static void Loop()
    {
        Log.Write("RA01 melee trace: the melee thread is running"); // RA01: M13-9c - TEMPORARY
        while (true)
        {
            Thread.Sleep(11);
            CrashWatch.Mark("melee: asleep between looks");
            try { Update(); }
            catch (Exception e)
            {
                if (errorLogged) continue;
                errorLogged = true;
                Log.Write($"RA01 melee: error - {e.GetType().Name}: {e.Message} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }
    }

    // RA01: one look: what V holds, whether melee is active, and the tracking snapshot; the work itself is in Look, BlockPose and Strike.
    // M13-10 - written as small plain functions: the native build step (ILC) failed to generate code for the earlier single big Update, and may have
    // generated broken code for it before - the likely cause of the crash with no fault.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        CrashWatch.Mark("melee 0: asking WeaponProbe what V holds");
        WeaponProbe.Kind kind = WeaponProbe.Held;
        ShoulderScope.Update(kind);
        TwoHandGrip.SetKind(kind);                                            // RA01: M18-0 - the two-handed grip only works with a gun                                           // RA01: M17-1 - a shouldered gun opens its scope (no LT)
        bool melee = IsMelee(kind);
        RecordStrike(kind);
        bool active = Tuning.PhysicalMelee && !SettingsMenu.Open && !MenuSense.ShowsMenu && (melee || kind == WeaponProbe.Kind.Gun); // RA01: M13-18 - empty hands no longer count (the fist draw is removed)
        if (!active) { Off(); return; }
        long now = Environment.TickCount64;
        looks++;
        if (now >= nextBeatMs) { nextBeatMs = now + 5000; Log.Write("RA01 melee heartbeat: alive, " + looks + " active looks so far, holding " + kind); }
        bool trace = traceLeft > 0;
        if (trace) { traceLeft--; Log.Write("RA01 melee trace: step 1 - active, holding " + kind); }
        CrashWatch.Mark("melee 1: reading the tracking snapshot");
        Controllers.Frame f;
        bool fresh = Controllers.TryReadFrame(out f);
        if (!fresh || !f.HeadValid || !f.LeftValid || !f.RightValid) { if (trace) Log.Write("RA01 melee trace: step 2 - no fresh tracking"); Off(); return; }
        if (trace) Log.Write("RA01 melee trace: step 2 - tracking read");
        Look(kind, melee, f, now, trace);
        if (trace) Log.Write("RA01 melee trace: step 5 - done");
    }

    // RA01: your head and hands measured from your nose along your facing, then the block pose and the strike check.
    // Cost - CPU: extremely low (a few vector sums and turns); GPU: none.
    static void Look(WeaponProbe.Kind kind, bool melee, Controllers.Frame f, long now, bool trace)
    {
        CrashWatch.Mark("melee 2: head facing and hands from the head");
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, Turn(f.Head.Orientation)); // RA01: OpenXR looks down -Z, Y is up
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-4f) return;
        forward = Vector3.Normalize(forward);
        Vector3 side = Vector3.Cross(forward, Vector3.UnitY);
        Vector3 headAt = At(f.Head);
        Vector3 left = At(f.Left) - headAt;
        Vector3 right = At(f.Right) - headAt;
        Quaternion weaponTurn = Turn(Tuning.LeftHanded ? f.Left.Orientation : f.Right.Orientation);
        if (trace) Log.Write("RA01 melee trace: step 3 - hands measured");
        if (ControllerMapping.FireTrigger > 0.5f) lastAttackMs = now;   // RA01: M28-2 - the weapon hand's trigger (the game's RT)

        CrashWatch.Mark("melee 3: block pose");
        string what = "";
        bool pose = melee && !IsKnife(kind) && BlockPose(kind, left, right, forward, side, weaponTurn, now, ref what); // RA01: M27-3 - knives never block (see IsKnife)
        CrashWatch.Mark("melee 4: block engage/release");
        if (pose) { poseGoneSince = 0; if (poseSince == 0) poseSince = now; }
        else { poseSince = 0; if (poseGoneSince == 0) poseGoneSince = now; }
        if (!blocking && pose && now - poseSince >= EngageMs && handSpeed < Tuning.SwingSpeed) SetBlock(true, what); // RA01: M14-4b - any guard not at strike speed
        else if (blocking && !pose && now - poseGoneSince >= ReleaseMs) SetBlock(false, "guard dropped");
        if (trace) Log.Write(pose ? "RA01 melee trace: step 4 - pose held" : "RA01 melee trace: step 4 - no pose");

        gameState = GameAnchor.TryMeleeState(out int state) ? state : -1;     // RA01: M14-6 - the game's own melee state (-1 = unreadable: timing only)
        if (gameState >= 0 && state != lastGameState)                          // RA01: M14-5b - logged beside our presses
        {
            lastGameState = state;
            Log.Write($"RA01 game melee state: {GameAnchor.MeleeStateName(state)}");
        }
        Settle(now);                                                           // RA01: M14-6 - a pressed swing: did the game take it?
        CrashWatch.Mark("melee 5: strike speed");
        if (lastFrameTime != 0 && f.Time != lastFrameTime) Strike(kind, left, right, f.Time, now);
        CrashWatch.Mark("melee 6: storing this frame's hands");
        if (f.Time != lastFrameTime) { lastHands[0] = left; lastHands[1] = right; lastFrameTime = f.Time; }
        CrashWatch.Mark("melee 7: look finished");
    }

    // RA01: true while the guard pose for what V holds is held (see the class comment); 'what' says which, for the log.
    // Cost - CPU: extremely low (a few dot products and one or two angles); GPU: none.
    static bool BlockPose(WeaponProbe.Kind kind, Vector3 left, Vector3 right, Vector3 forward, Vector3 side, Quaternion weaponTurn, long now, ref string what)
    {
        if (kind == WeaponProbe.Kind.Monowire)
        {
            Vector3 span = left - right;
            float open = span.Length();
            float tilt = open > 1e-3f ? Degrees(MathF.Asin(Math.Clamp(MathF.Abs(span.Y) / open, 0f, 1f))) : 0f;
            what = "Monowire held diagonally";
            return now - lastAttackMs > WireQuietMs && open >= WireOpen && tilt >= WireMinDeg && tilt <= WireMaxDeg;
        }
        if (kind == WeaponProbe.Kind.Melee && BladeBlock(Tuning.LeftHanded ? left : right, weaponTurn, forward, side)) { what = "blade across the body"; return true; } // RA01: M13-16
        what = "both hands up in a guard"; // RA01: any melee weapon, fists, Gorilla Arms, Mantis Blades
        return InGuard(left, forward, side) && InGuard(right, forward, side) && Vector3.Distance(left, right) <= GuardHandsApart;
    }

    // RA01: M13-16 - the blade block: a blade held in the fist runs out along the grip's -Z; it must lie across you (BladeCrossDeg away from straight ahead,
    // within BladeLevelDeg of level) with the weapon hand in the blade box. Measured like a builder's level and square: the tilt from the floor, the angle
    // from your nose's direction.
    // Cost - CPU: extremely low (a vector turn, two angles and a few compares); GPU: none.
    static bool BladeBlock(Vector3 hand, Quaternion weaponTurn, Vector3 forward, Vector3 side)
    {
        float front = Vector3.Dot(hand, forward), across = Vector3.Dot(hand, side);
        if (front < BladeFrontMin || front > BladeFrontMax || hand.Y < BladeUpMin || hand.Y > BladeUpMax || MathF.Abs(across) > BladeSideMax) return false;
        Vector3 blade = Vector3.Transform(-Vector3.UnitZ, weaponTurn);
        float level = Degrees(MathF.Asin(Math.Clamp(MathF.Abs(blade.Y), 0f, 1f)));
        Vector3 flat = new Vector3(blade.X, 0f, blade.Z);
        if (flat.LengthSquared() < 1e-4f) return false;
        float cross = Degrees(MathF.Acos(Math.Clamp(MathF.Abs(Vector3.Dot(Vector3.Normalize(flat), forward)), 0f, 1f)));
        return level <= BladeLevelDeg && cross >= BladeCrossDeg;
    }

    // RA01: one strike check: hand speed against the head between two headset frames; one tap per swing.
    // M13-20 - a strike-speed swing while blocking used to be ignored, and the block's grace outlasted the fast part of the swing, so a cut thrown from the
    // guard never landed. Now the swing itself drops the block (LT up) in the same moment the strike goes in - like a boxer's guard opening as the punch goes.
    // Cost - CPU: extremely low (two distances and a divide); GPU: none.
    static void Strike(WeaponProbe.Kind kind, Vector3 left, Vector3 right, long frameTime, long now)
    {
        float seconds = (frameTime - lastFrameTime) / (float)Stopwatch.Frequency;
        if (seconds <= 0.004f || seconds >= 0.1f) return;
        float leftSpeed = Vector3.Distance(left, lastHands[0]) / seconds;
        float rightSpeed = Vector3.Distance(right, lastHands[1]) / seconds;
        handSpeed = MathF.Max(leftSpeed, rightSpeed);                                  // RA01: M14-4 - for the guard's "hands settled" check
        if (PowerAttack.Busy) { armed = false; queued = false; pending = false; return; } // RA01: M27-2 - a power attack is loading or just went: this swing is its own
        bool eitherHand = IsEitherHand(kind);
        float speed = eitherHand ? handSpeed : (Tuning.LeftHanded ? leftSpeed : rightSpeed);
        if (speed < RearmSpeed) armed = true;                                           // RA01: M14-4b - the last swing has ended: the next one may count
        bool swung = armed && speed >= Tuning.SwingSpeed;
        if (StateAware(kind))                                                            // RA01: M14-6 - the game's own state decides when to press again
        {
            if (!swung) return;
            armed = false;
            CrashWatch.Mark("melee: a swing (state-aware)");
            pending = true; pendingAt = now; pendingLeft = leftSpeed > rightSpeed; pendingKind = kind; pendingTapped = false; pendingRetried = false;
            pendingWhy = "melee strike - " + (eitherHand ? "a hand" : "weapon hand") + " swung at " + speed.ToString("F1") + " m/s";
            if (kind == WeaponProbe.Kind.MantisBlades || kind == WeaponProbe.Kind.GorillaArms) Visuals.CyberwarePose.Strike(pendingLeft); // RA01: M14-1b
            Settle(now);                                                                 // RA01: presses now if the trigger is free
            return;
        }
        if (swung && now < nextStrikeMs && kind != WeaponProbe.Kind.Gun)                 // RA01: M14-4 - too soon after the last strike: queue it, don't lose it
        {
            if (!queued) { queued = true; queuedLeft = leftSpeed > rightSpeed; }
            armed = false;                                                              // RA01: M14-4b - this swing is spoken for
            return;
        }
        bool fromQueue = !swung && queued && now >= nextStrikeMs;
        if (!swung && !fromQueue) return;
        bool strikeLeft = fromQueue ? queuedLeft : leftSpeed > rightSpeed;
        queued = false;
        if (swung) armed = false;                                                       // RA01: M14-4b - one swing, one strike
        CrashWatch.Mark("melee: pressing a strike / gun bash");
        bool wasBlocking = blocking;
        SetBlock(false, "swung out of the guard"); // RA01: M13-20 - no-op if not blocking
        lastAttackMs = now; lastStrikeMs = now;
        if (kind == WeaponProbe.Kind.Gun)
        {
            nextStrikeMs = now + BashGapMs;
            ControllerMapping.TapLeftShoulder(TapMs, "gun bash - weapon hand swung at " + speed.ToString("F1") + " m/s");
        }
        else
        {
            nextStrikeMs = now + StrikeGapMs;
            if (kind == WeaponProbe.Kind.MantisBlades || kind == WeaponProbe.Kind.GorillaArms) Visuals.CyberwarePose.Strike(strikeLeft); // RA01: M14-1b - the striking hand's arm pops out
            ControllerMapping.TapRightTrigger(TapMs, fromQueue ? "melee strike - queued swing fired" : "melee strike - " + (eitherHand ? "a hand" : "weapon hand") + " swung at " + speed.ToString("F1") + " m/s",
                                              wasBlocking ? BlockToStrikeMs : 0); // RA01: M14-4 - out of the guard: RT a frame after LT lets go
        }
    }
    static bool queued, queuedLeft;                   // RA01: M14-4 - a swing that came inside the strike gap, waiting to fire (and which hand) - timing-only mode

    // RA01: M14-6 - STATE-AWARE STRIKES. The M14-5b log showed the game takes a press at once while V is ready, but ignores one that lands while she is busy -
    // mid-attack, recovering, staggered (Parried) or guard-broken (NotReady): 39 of 102 swings were lost that way. A single 120 ms tap during the busy part
    // falls into nothing. So now each swing is kept until the game answers, like a waiter who repeats an order the kitchen didn't hear:
    //  - pressed at once (a press mid-attack sometimes does chain the combo);
    //  - TAKEN when the game's melee state moves into an attack or Hold (its "trigger down" state) different from the state at the press;
    //  - not taken after ResponseMs: pressed ONCE more, the moment the game's state changes (the attack ended, the stagger wore off);
    //  - nobody could use it within ExpireMs: dropped (logged), so a stale swing never fires long after you moved on.
    // One swing is still one strike - the re-press replaces the lost one. A new swing replaces a waiting one. Only while the game's state can be read and the
    // Dev row "Melee: timing only (old)" is off; guns, or an unreadable state, keep the M14-4 timing.
    // Cost - CPU: extremely low (a few compares per look); GPU: none.
    static bool StateAware(WeaponProbe.Kind kind) => !Tuning.MeleeTimingOnly && gameState >= 0 && kind != WeaponProbe.Kind.Gun;

    // RA01: M14-6 - each look: press a waiting swing when the trigger is free, check whether the game took it, press once more when it changes state, or drop it.
    // Cost - CPU: extremely low (a few compares; a press when one is due); GPU: none.
    static void Settle(long now)
    {
        if (!pending) return;
        if (pendingTapped && IsAttack(gameState) && gameState != pendingState) { pending = false; return; } // RA01: the game took it
        if (now - pendingAt > ExpireMs) { pending = false; Log.Write($"RA01 melee strike dropped: the game stayed busy ({GameAnchor.MeleeStateName(gameState)}) for {ExpireMs} ms"); return; }
        if (now < nextStrikeMs) return;                                                  // RA01: the last tap is still going
        bool due = !pendingTapped || (!pendingRetried && now - tappedAt >= ResponseMs && gameState != pendingState);
        if (!due) return;
        string why = pendingTapped ? $"melee strike - pressed again: the game was busy ({GameAnchor.MeleeStateName(pendingState)}), now {GameAnchor.MeleeStateName(gameState)}" : pendingWhy;
        pendingRetried = pendingTapped; pendingTapped = true; tappedAt = now; pendingState = gameState;
        bool wasBlocking = blocking;
        SetBlock(false, "swung out of the guard");
        lastAttackMs = now; lastStrikeMs = now;
        nextStrikeMs = now + TapMs + 30;                                                // RA01: the trigger is free again just after this tap
        ControllerMapping.TapRightTrigger(TapMs, why, wasBlocking ? BlockToStrikeMs : 0);
    }

    // RA01: M14-6 - is this game melee state an attack, or Hold (the trigger-down state every attack starts from)?
    // Cost - CPU: extremely low (a compare); GPU: none.
    static bool IsAttack(int state) => state == 6 || state == 7 || (state >= 11 && state <= 21); // RA01: Hold, ChargedHold, ComboAttack ... EquipAttack (gamePSMMeleeWeapon)
    const long ResponseMs = 200, ExpireMs = 800;
    static bool pending, pendingTapped, pendingRetried, pendingLeft;
    static long pendingAt, tappedAt;
    static int pendingState = -1, gameState = -1;
    static WeaponProbe.Kind pendingKind;
    static string pendingWhy = "";
    static int lastGameState = -1;                    // RA01: M14-5b - the game's melee state last logged
    static bool armed = true;                         // RA01: M14-4b - false from a strike until the hand slows below RearmSpeed (like a gun that must re-cock)
    static float handSpeed;                           // RA01: M14-4 - the faster hand's speed at the last headset frame (m/s): no block at strike speed

    // RA01: block off, speed tracking reset.
    // Cost - CPU: extremely low; GPU: none.
    static void Off() { SetBlock(false, "off"); lastFrameTime = 0; }

    // RA01: the melee kinds: melee weapons, fists and melee cyberware; and the ones either hand strikes with (fists and the cyberware - M13-34).
    // Cost - CPU: extremely low (compares); GPU: none.
    // M14-1a - TEST: when the right trigger goes down (your own press past half way) with Mantis Blades or Gorilla Arms out, Visuals' strike recorder films
    // the game's own strike for the next 1.5 s (see Visuals\StrikeRecorder.cs). Log only.
    // Cost - CPU: extremely low (a compare per look); GPU: none.
    static void RecordStrike(WeaponProbe.Kind kind)
    {
        bool down = ControllerMapping.FireTrigger > 0.5f || PowerAttack.Charging;             // RA01: M27-2 - a held power attack brings the arms out like your own held RT
        bool cyberware = kind == WeaponProbe.Kind.MantisBlades || kind == WeaponProbe.Kind.GorillaArms;
        if (down && !triggerWasDown && cyberware) Visuals.StrikeRecorder.Begin(kind.ToString());
        Visuals.CyberwarePose.Trigger(down && cyberware); // RA01: M14-1g - your own RT: held = both arms out, let go = a quick strike or the power attack
        triggerWasDown = down;
        Visuals.CyberwarePose.Block(blocking || ControllerMapping.LeftTriggerSent); // RA01: M14-1c - the physical guard or your own LT: the game's own block pose
    }
    static bool triggerWasDown;

    // RA01: M27-3 - a throwing knife (the game's item type Wea_Knife, named "Knife"): its LT is AIM-TO-THROW, not a block - knives can only stab or be thrown
    // (Rob 2026-10-08: the block pose sent LT and the knife went into throw mode). So no guard pose ever holds LT with a knife; strikes still work.
    // Cost - CPU: extremely low (a compare and a string compare); GPU: none.
    static bool IsKnife(WeaponProbe.Kind k) => k == WeaponProbe.Kind.Melee && WeaponProbe.HeldName == "Knife";

    static bool IsMelee(WeaponProbe.Kind k) => k == WeaponProbe.Kind.Melee || IsEitherHand(k);
    static bool IsEitherHand(WeaponProbe.Kind k) => k == WeaponProbe.Kind.Fists || IsMeleeCyberware(k);
    static bool IsMeleeCyberware(WeaponProbe.Kind k) => k == WeaponProbe.Kind.MantisBlades || k == WeaponProbe.Kind.GorillaArms || k == WeaponProbe.Kind.Monowire;
    static float Degrees(float radians) => radians * (180f / MathF.PI);

    // RA01: true if a hand (measured from the head) is in the guard box in front of the face.
    // Cost - CPU: extremely low (three dot products); GPU: none.
    static bool InGuard(Vector3 hand, Vector3 forward, Vector3 right)
    {
        float front = Vector3.Dot(hand, forward), side = Vector3.Dot(hand, right);
        return front >= FrontMin && front <= FrontMax && hand.Y >= UpMin && hand.Y <= UpMax && MathF.Abs(side) <= SideMax;
    }

    // RA01: holds or lets go of the left trigger; a log line only when it changes.
    // Cost - CPU: extremely low (a compare, a store, maybe a log line); GPU: none.
    static void SetBlock(bool on, string why)
    {
        if (on == blocking) return;
        CrashWatch.Mark(on ? "melee: holding the left trigger (block)" : "melee: letting go of the left trigger");
        blocking = on;
        if (!on) poseSince = 0;
        ControllerMapping.HoldLeftTrigger(on, on ? $"block - {why}" : $"block released - {why}");
    }

    static Vector3 At(Xr.Posef p) => new Vector3(p.Position.X, p.Position.Y, p.Position.Z);
    static Quaternion Turn(Xr.Quaternionf q) => Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
}
