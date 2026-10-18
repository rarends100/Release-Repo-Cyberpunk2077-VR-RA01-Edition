using RA01.Core;

namespace RA01.Actions;

// RA01: M24-3 - PHYSICAL LADDER CLIMBING (VR Controls tab "Physical ladder", "Climb strength"). Only while the game says V is on a ladder (BlackboardProbe.Locomotion
// 10 Ladder or 11 LadderSprint); sliding down (12) and jumping off (13) are the game's own, and off a ladder this does nothing at all.
//  - GRIP AND PULL: a hand only counts while its grip is squeezed (you are holding a rung). Each new headset frame, how far each holding hand moved up or
//    down in the room since the last frame is measured: pulling DOWN climbs V UP, pushing UP lowers her - exactly what your arms do on a real ladder.
//    Hand travel slower than StrokeSpeed (holding still, adjusting your grip) is not a pull.
//  - Builder picture: a FLYWHEEL, like swimming's - every metre pulled spins it up (times "Climb strength"), and it slows by itself (GlideSeconds), so
//    V keeps climbing smoothly between hand-over-hand pulls instead of jerking rung by rung. Its speed is how far the left stick is pushed up or down.
//  - While climbing, the grips hold the rungs: they never reach the game as LB/RB (ControllerMapping.SetClimb). Your thumb on the left stick always wins.
// Hands are measured in the room (up is up whatever your head does). Never in a menu or the F10 window.
// Cost - CPU: extremely low (two subtractions and compares per controller read, about 60 a second, only on a ladder); GPU: none.
internal static class PhysicalLadder
{
    const float StrokeSpeed = 0.3f;          // RA01: m/s - slower hand travel is not a pull
    const float GlideSeconds = 0.35f;        // RA01: the flywheel loses about two thirds of its speed in this long without pulls (shorter than swimming: a ladder has no glide)
    const float PushPerMetre = 3f;           // RA01: stick-widths per metre pulled (times "Climb strength") - at 100% a 33 cm pull is a full push
    const float GripHeld = 0.5f;             // RA01: a grip squeezed past this is holding a rung
    const float MaxStepSeconds = 0.1f;       // RA01: a longer gap between frames (a hitch, a pause) is not measured as hand movement
    const int Ladder = 10, LadderSprint = 11;

    static float lastLeftY, lastRightY;      // RA01: each hand's height at the last measured frame
    static long lastFrameTime;
    static float push;                       // RA01: the flywheel: -1 (down) .. 1 (up)
    static bool climbing;

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one look: on a ladder? then the holding hands since the last frame, the flywheel, and the push handed to ControllerMapping. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        int state = BlackboardProbe.Locomotion;
        bool onLadder = state == Ladder || state == LadderSprint;
        if (!Tuning.PhysicalLadder || !onLadder || SettingsMenu.Open || MenuSense.ShowsMenu) { Stop(onLadder); return; }
        if (!climbing) { climbing = true; push = 0f; lastFrameTime = 0; Log.Write("RA01 ladder: on a ladder - grip and pull down to climb up, push up to climb down"); }
        if (Controllers.TryReadFrame(out Controllers.Frame f) && f.LeftValid && f.RightValid && f.Time != lastFrameTime) Measure(f);
        ControllerMapping.SetClimb(true, push);
    }

    // RA01: one new headset frame: each holding hand's drop since the last one spins the flywheel up (a rise spins it down); time slows it.
    // Cost - CPU: extremely low (a few subtractions and compares); GPU: none.
    static void Measure(Controllers.Frame f)
    {
        float dt = lastFrameTime == 0 ? 0f : (f.Time - lastFrameTime) / (float)System.Diagnostics.Stopwatch.Frequency;
        lastFrameTime = f.Time;
        float leftY = f.Left.Position.Y, rightY = f.Right.Position.Y;          // RA01: OpenXR: Y is up
        float dropL = lastLeftY - leftY, dropR = lastRightY - rightY;
        lastLeftY = leftY; lastRightY = rightY;
        if (dt <= 0f || dt > MaxStepSeconds) return;
        float minStep = StrokeSpeed * dt;
        float pulled = Pull(dropL, Controllers.LeftGrip, minStep) + Pull(dropR, Controllers.RightGrip, minStep);
        push = Math.Clamp(push * MathF.Exp(-dt / GlideSeconds) + pulled * PushPerMetre * Tuning.LadderStrength, -1f, 1f);
    }

    // RA01: metres this hand pulled down (+) or pushed up (-) this frame, 0 if its grip isn't holding a rung or it moved too slowly to be a pull.
    // Cost - CPU: extremely low (two compares); GPU: none.
    static float Pull(float drop, float grip, float minStep) => grip > GripHeld && MathF.Abs(drop) > minStep ? drop : 0f;

    // RA01: off the ladder (or switched off, or a menu): the controls go back to normal, logged once.
    // Cost - CPU: extremely low (a compare; a call and a log line on a change); GPU: none.
    static void Stop(bool onLadder)
    {
        if (!climbing) return;
        climbing = false;
        push = 0f;
        ControllerMapping.SetClimb(false, 0f);
        Log.Write(onLadder ? "RA01 ladder: paused (a menu, or switched off)" : "RA01 ladder: off the ladder - controls back to normal");
    }
}
