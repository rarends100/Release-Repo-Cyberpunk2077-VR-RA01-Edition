using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M22-1 - PHYSICAL SWIMMING (VR Controls tab "Physical swimming", "Forward stroke strength", "Side stroke strength" - M22-1b: two dials, Rob's design 2026-10-07). Only while the game says V is in the water
// (BlackboardProbe.SwimState: surface or diving); out of the water this does nothing at all.
//  - STROKES PUSH FORWARD: each new headset frame, each hand's movement since the last frame is split along your facing (your head's compass direction) into
//    "backward" and "sideways". Hand travel backward faster than StrokeSpeed is a stroke, and the hand coming forward again (the recovery) is ignored.
//    M22-1c - ANY STROKE STYLE (Rob): there is no stroke recognition - every style pulls water backward somewhere, and only that part is measured:
//    front crawl (alternate long pulls), doggy paddle (short quick alternate paws), breaststroke (both hands out and back - the outward parts cancel),
//    butterfly (both arms pulled back together) and a one-arm stroke all push V forward. Short strokes push less per stroke, more of them push the same. Builder picture: a FLYWHEEL - every metre of water pulled back spins it up by one notch (times "Forward stroke strength", up to 500%),
//    and it coasts down by itself (GlideSeconds), so V glides on between strokes instead of stopping dead. Its speed is how far the left stick is pushed forward.
//  - SIDE PUSH TURNS THE OTHER WAY: a hand sweeping out to the right turns V left, and the reverse - like pushing water away in a real pool. A breaststroke sweeps
//    both hands out at once, so the two cancel and V swims straight. Both hands pushing to the SAME side at once turn at 1.5 times one hand's speed (Rob).
//    M22-1c - a hand's movement only counts as a side push when it is MORE sideways than backward: the inward curve of a crawl or butterfly pull (each
//    hand sweeps under the body) is part of a forward stroke, so it must not turn V.
//    M22-1d - TURN DEAD ZONE (Rob): a side push only counts while that hand is more than Tuning.SwimTurnDeadZoneDeg (M22-1e: a dial, 10-30, default 20) out to the side - measured from your head, from
//    straight ahead along your facing. Small wobbles in front of your chest (a doggy paddle, the start of a crawl pull) never turn V.
//    A second, quicker flywheel; fed onto the right stick's left/right like a stick turn (the view turns with V).
//  - DIVE AND SURFACE: the game swims where V looks and your head steers her view, so looking down dives and looking up rises with no code here (to confirm in test).
// Hands are measured in the room, not against your head: turning your head with still hands must not look like a sideways sweep. Your own thumb always wins
// (ControllerMapping: the real stick pushed past its "busy" line replaces the swim push). Never in a menu or the F10 window.
// Cost - CPU: extremely low (a few subtractions, two dot products and compares per controller read, about 60 a second, only while V swims); GPU: none.
internal static class PhysicalSwim
{
    const float StrokeSpeed = 0.4f;          // RA01: m/s - slower hand travel (drifting, small adjustments) is not a stroke (M22-1c: was 0.5 - a gentle doggy paddle is about 0.5)
    const float GlideSeconds = 1.0f;         // RA01: the forward flywheel loses about two thirds of its speed in this long without strokes
    const float TurnFadeSeconds = 0.4f;      // RA01: the turn flywheel stops quicker - a turn should end soon after the push
    const float TurnPerMetre = 2.5f;         // RA01: stick-widths of turn per metre of hand swept sideways (times "Side stroke strength") - at 100% a 40 cm sweep is a full push
    const float BothHandsTurn = 1.5f;        // RA01: Rob - both hands pushing the same way turn at 1.5 times one hand's speed
    const float MaxStepSeconds = 0.1f;       // RA01: a gap longer than this between frames (a hitch, a pause) is not measured as hand movement

    static readonly float[] lastHands = new float[6]; // RA01: left x,y,z then right x,y,z at the last measured frame (an array, not Vector3 fields - the ILC quirk)
    static long lastFrameTime;
    static float forward, turn;              // RA01: the two flywheels: forward 0..1, turn -1 (left) .. 1 (right)
    static bool swimming;

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one look: in the water? then the hands since the last frame, the two flywheels, and the push handed to ControllerMapping. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        int state = BlackboardProbe.SwimState;
        if (!Tuning.PhysicalSwim || state == 0 || SettingsMenu.Open || MenuSense.ShowsMenu) { Stop(state); return; }
        if (!swimming) { swimming = true; forward = turn = 0f; lastFrameTime = 0; Log.Write($"RA01 swim: in the water ({(state == 2 ? "diving" : "on the surface")}) - strokes push V forward, side pushes turn her"); }
        if (!Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid || !f.LeftValid || !f.RightValid) { ControllerMapping.SetSwim(true, forward, turn); return; }
        if (f.Time != lastFrameTime) Measure(f);
        ControllerMapping.SetSwim(true, forward, turn);
    }

    // RA01: one new headset frame: the hands' travel since the last one, split along your facing, spins the flywheels up; time spins them down.
    // Cost - CPU: extremely low (a quaternion turn, two subtractions and dot products per hand); GPU: none.
    static void Measure(Controllers.Frame f)
    {
        float dt = lastFrameTime == 0 ? 0f : (f.Time - lastFrameTime) / (float)System.Diagnostics.Stopwatch.Frequency;
        lastFrameTime = f.Time;
        Vector3 left = new(f.Left.Position.X, f.Left.Position.Y, f.Left.Position.Z);
        Vector3 right = new(f.Right.Position.X, f.Right.Position.Y, f.Right.Position.Z);
        Vector3 wasLeft = new(lastHands[0], lastHands[1], lastHands[2]), wasRight = new(lastHands[3], lastHands[4], lastHands[5]);
        lastHands[0] = left.X; lastHands[1] = left.Y; lastHands[2] = left.Z; lastHands[3] = right.X; lastHands[4] = right.Y; lastHands[5] = right.Z;
        if (dt <= 0f || dt > MaxStepSeconds) return;

        Quaternion head = Quaternion.Normalize(new Quaternion(f.Head.Orientation.X, f.Head.Orientation.Y, f.Head.Orientation.Z, f.Head.Orientation.W));
        Vector3 facing = Vector3.Transform(-Vector3.UnitZ, head);   // RA01: OpenXR looks down -Z, Y is up
        facing.Y = 0;
        if (facing.LengthSquared() < 1e-4f) return;                   // RA01: looking straight up or down: no compass direction this frame
        facing = Vector3.Normalize(facing);
        Vector3 rightward = Vector3.Cross(facing, Vector3.UnitY);    // RA01: your right, level with the floor

        float minStep = StrokeSpeed * dt;                            // RA01: hand travel this frame below this is not a stroke
        Vector3 moveL = left - wasLeft, moveR = right - wasRight;

        float pulled = Backward(moveL, facing, minStep) + Backward(moveR, facing, minStep);
        forward = Math.Clamp(forward * MathF.Exp(-dt / GlideSeconds) + pulled * Tuning.SwimForwardStrength, 0f, 1f); // RA01: 1 = the stick fully forward, the game's top swim speed

        Vector3 headAt = new(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z);
        float sideL = OutToSide(left - headAt, facing, rightward) ? Sideways(moveL, facing, rightward, minStep) : 0f;   // RA01: M22-1d - the dead zone
        float sideR = OutToSide(right - headAt, facing, rightward) ? Sideways(moveR, facing, rightward, minStep) : 0f;
        float pushed = sideL != 0f && sideR != 0f && MathF.Sign(sideL) == MathF.Sign(sideR)
            ? MathF.Sign(sideL) * MathF.Max(MathF.Abs(sideL), MathF.Abs(sideR)) * BothHandsTurn   // RA01: both the same way: 1.5 times the stronger hand
            : sideL + sideR;                                                                     // RA01: one hand, or a breaststroke's two opposite sweeps cancelling
        turn = Math.Clamp(turn * MathF.Exp(-dt / TurnFadeSeconds) - pushed * TurnPerMetre * Tuning.SwimSideStrength, -1f, 1f); // RA01: minus - push right, turn left
    }

    // RA01: metres this hand moved backward (against your facing) this frame, 0 if forward or too slow to be a stroke.
    // Cost - CPU: extremely low (one dot product and a compare); GPU: none.
    static float Backward(Vector3 move, Vector3 facing, float minStep)
    {
        float back = -Vector3.Dot(move, facing);
        return back > minStep ? back : 0f;
    }

    // RA01: metres this hand moved sideways this frame (+ right, - left), 0 if too slow to be a push, or (M22-1c) if it moved more backward than sideways -
    // then it is part of a forward stroke, not a turn.
    // Cost - CPU: extremely low (two dot products and compares); GPU: none.
    static float Sideways(Vector3 move, Vector3 facing, Vector3 rightward, float minStep)
    {
        float side = Vector3.Dot(move, rightward);
        float back = MathF.Abs(Vector3.Dot(move, facing));
        return MathF.Abs(side) > minStep && MathF.Abs(side) > back ? side : 0f;
    }

    // RA01: M22-1d - is this hand (its place from your head) more than Tuning.SwimTurnDeadZoneDeg out to the side of straight ahead? Measured flat, like a compass bearing.
    // Cost - CPU: extremely low (two dot products and one arctangent); GPU: none.
    static bool OutToSide(Vector3 fromHead, Vector3 facing, Vector3 rightward)
    {
        float bearing = MathF.Atan2(MathF.Abs(Vector3.Dot(fromHead, rightward)), Vector3.Dot(fromHead, facing)) * (180f / MathF.PI);
        return bearing > Tuning.SwimTurnDeadZoneDeg;
    }

    // RA01: out of the water (or switched off, or a menu): the controls go back to normal, logged once.
    // Cost - CPU: extremely low (a compare; a call and a log line on a change); GPU: none.
    static void Stop(int state)
    {
        if (!swimming) return;
        swimming = false;
        forward = turn = 0f;
        ControllerMapping.SetSwim(false, 0f, 0f);
        Log.Write(state == 0 ? "RA01 swim: out of the water - controls back to normal" : "RA01 swim: paused (a menu, or switched off)");
    }
}
