using System.Diagnostics;
using RA01.Core;

namespace RA01.Actions;   

// RA01: M11-2 - physical crouch: crouch down for real and V crouches, stand up and she stands (VR Controls tab "Physical crouch" / "Crouch depth").
// Your standing height is the head height Core's HeadPose took at the last recentre (EyeHeight); each controller read compares your head's height now with it,
// like a thermostat with a bit of play: your head CrouchDepthCm below where it was at the recentre -> crouch, back within that less StandMarginCm -> stand.
// M11-3b - measured as a DROP in centimetres, not a share of your height: the headset's own recentre can move the runtime's floor (a 1.72 m head read 2.14 m),
// which made a percentage line far too deep; the drop from the recentre is the same wherever the runtime thinks the floor is. The game's crouch is set to
// Toggle, so each change is one short press of R3, the right stick click (ControllerMapping.TapR3). M11-2b: it was B in the first drop - B isn't the crouch.
// While crouched this way, Core is told to hold the view at your real height (CameraBodyProbe.HoldStandingHeight): the game's own crouch drop is added back,
// so the view goes exactly as low as your head does and never further.
// We can't read the game's own crouch state yet, so if the game stands V up by itself (a sprint, say) this toggle can get out of step - crouching and standing
// once lines it up again; the view hold is harmless then (with V standing there is no crouch drop to add back).
// Never taps while a game menu or the F10 window is open, while you hold the right stick in yourself, or within RefireMs of the last tap. A recentre (new standing height) resets
// it to standing without a tap.
// Cost - CPU: extremely low (a ring read, a divide and a few compares per controller read, about 60 a second, on the game's XInput thread); GPU: none.
internal static unsafe class PhysicalCrouch
{
    const int TapMs = 120;                   // RA01: how long each R3 press lasts
    const long RefireMs = 400;               // RA01: no second tap sooner than this after one
    const float StandMarginCm = 10f;         // RA01: M11-3b - stand again this much above the crouch line
    const float MinStandingHeight = 0.8f;    // RA01: a recentre lower than this (lying down, a glitch) switches physical crouch off until the next recentre

    static bool crouched;                    // RA01: what we believe V's crouch is, as far as our own taps go
    static float standing;                   // RA01: the EyeHeight this belief was built on - a recentre changes it
    static long nextTapMs;

    // RA01: M11-3 - true while V is crouched by our own taps; VRIK reads it to lift her to your height (Visuals reads Actions, never the other way round).
    // RA01: Cost - CPU: extremely low (one bool read); GPU: none.
    public static bool Crouched => crouched;

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one look at your head height, maybe one tap. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        if (!Tuning.PhysicalCrouch || !HeadPose.Ready || VehicleState.Mounted) { Reset(); return; } // RA01: M19-1 - not in a vehicle: sitting down in your real chair isn't a crouch
        float eye = HeadPose.EyeHeight;
        if (eye != standing)
        {
            if (crouched) Log.Write("RA01 crouch: you recentred while crouched - standing height taken again; if V is still crouched, click the right stick once to stand up");
            Reset();
            standing = eye;
        }
        if (eye < MinStandingHeight || SettingsMenu.Open || MenuSense.ShowsMenu || ControllerMapping.CrouchClick) return;
        if (!HeadPose.TryRead(Stopwatch.GetTimestamp(), out HeadPose.Sample head)) return;
        long now = Environment.TickCount64;
        if (now < nextTapMs) return;
        float dropCm = -head.Up * 100f; // RA01: M11-3b - how far your head is below where it was at the recentre
        if (!crouched && dropCm > Tuning.CrouchDepthCm) Tap(true, dropCm, now);
        else if (crouched && dropCm < Tuning.CrouchDepthCm - StandMarginCm) Tap(false, dropCm, now);
    }

    // RA01: one R3 press, and the new belief (and the view hold) with it.
    // Cost - CPU: extremely low (a store, a clock-based write and a log line); GPU: none.
    static void Tap(bool crouch, float dropCm, long now)
    {
        crouched = crouch;
        CameraBodyProbe.HoldStandingHeight = crouch;
        nextTapMs = now + RefireMs;
        ControllerMapping.TapR3(TapMs, $"physical crouch - your head {dropCm:F0} cm below standing -> V {(crouch ? "crouches" : "stands")}");
    }

    // RA01: back to standing, without a tap, and the view hold off.
    // Cost - CPU: extremely low (two stores); GPU: none.
    static void Reset()
    {
        crouched = false;
        CameraBodyProbe.HoldStandingHeight = false;
    }
}
