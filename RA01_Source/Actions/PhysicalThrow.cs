using System.Diagnostics;
using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M22-4 - PHYSICAL GRENADE THROW (Weapons tab "Physical grenade throw" / "Throw strength", Rob approved 2026-10-07; "feels great, works perfectly" 15:06).
// Hold the grip to take a grenade out as usual (the game's own "hold to aim"), swing your arm and LET GO of the grip as your hand comes forward - letting go
// is what throws in the game anyway. The grenade then lands where a ball thrown that way would land.
// How (builder picture: a ball-throwing machine with a tape measure):
//  1. Every new headset frame both hands' positions are kept, a short history of HistoryFrames frames.
//  2. The moment a grip lets go while a grenade is in hand or charging (game grenade state 2 / 3), that hand's velocity over the last VelocityWindowMs is
//     taken - the "release" - with your head's position and facing at that moment.
//  3. When the game's grenade state turns to THROWING or QUICK THROW (4 / 6) within ReleaseToThrowMs of that release, and the hand was moving at least
//     MinThrowSpeed, the throw is worked out:
//      - the hand's velocity is split along your head's facing (forward, right, up) and laid onto the game's view (V's body turn plus your neck angle),
//        times "Throw strength";
//      - from your hand's place, a ball with that velocity and the game's grenade gravity (10.5 m/s2) is followed down to the floor under V's feet - the
//        landing spot (capped at MaxRange, at least MinRange);
//      - the angle it left at (how steeply the hand was rising, kept between MinAngle and MaxAngle, radians - Rob's 0.6 test gave a clear lob).
//  4. That landing spot and angle go on Core's throw lever (ThrowLever.OrderTargetLanding); the grenade's flight path picks them up a moment later and the
//     game flies it there. A tap without a swing (slower than MinThrowSpeed) leaves the throw as the game makes it.
// The landing height is the floor under V, so a throw onto a roof or into a pit lands short or long of the spot - the path still hits whatever is in the way.
// M22-5 - PHYSICAL KNIFE THROW (Weapons tab "Physical knife throw" / "Knife throw speed", Rob approved 2026-10-07). A thrown knife is a dart: 110 m/s with
// 20 m/s2 gravity (the M22-2 probe), so its DIRECTION is what matters. While the knife is held in aim (melee state 9, Targeting) a forward swing of the knife
// hand faster than "Knife throw speed" presses RT for you (the game's throw) and takes that swing; pressing RT yourself works too (the last KnifeWindowMs of
// the hand is taken when the game's state turns to ThrowAttack, 19). The point KnifeAimDistance along your swing's line is the target, and the exact small
// upward angle that makes a 110 m/s knife pass through it is worked out (the standard projectile formula) - ordered on the same lever, the knife flies
// along your arm's line wherever you look. A swing slower than MinThrowSpeed: the game's own throw along your view.
// M22-5b - Rob's test (15:25): at the game's 110 m/s every throw crossed the map and a small hand wobble missed by metres. Now a knife flies like the
// grenades: its speed is your hand's speed times "Knife throw strength", followed down to the floor with the knife's own gravity (20 m/s2) - a flick lobs
// it a few metres, a hard throw flies fast and flatter. And "Knife aim help" mixes the swing's direction with where you look (0% pure arm, 100% where you
// look, default 50%) - only the direction; the strength always comes from your arm.
// M22-5c - "Knife flight" (Rob 15:41: "it lacks the aerodynamics of a knife"): 0% = lob (the knife's own 20 m/s2 gravity), 100% = straight like a dart
// (StraightGravity). The chosen gravity goes both into the landing sum here and onto the knife's own flight path (ThrowLever, order bit 4), so the
// game aims and flies it the same way. In between, the gravity falls evenly on a ratio scale (each 10% about a quarter less pull).
// M36-1 - while a grenade is in hand, Core's throw lever clears its aim lines and landing circle (ThrowLever.HidePreviews).
// Cost - CPU: extremely low (a few stores per headset frame, a few compares per controller read while a knife is aimed, and one short calculation per throw,
// on the game's XInput thread); GPU: none.
internal static class PhysicalThrow
{
    const int HistoryFrames = 16;
    const long VelocityWindowMs = 60, ReleaseToThrowMs = 600;
    const float GripReleased = 0.35f, GripHeld = 0.6f;     // RA01: a grip below 0.35 after being above 0.6 has been let go (a gap, so a half squeeze doesn't flicker)
    const float MinThrowSpeed = 1.5f;                       // RA01: m/s - slower than this at release is a tap, not a throw
    const float GameGravity = 10.5f;                        // RA01: the grenade's own gravity (its flight path's +0xA0, M22-2c)
    const float MaxRange = 40f, MinRange = 1f;              // RA01: metres along the ground
    const float MinAngle = 0.05f, MaxAngle = 1.3f;          // RA01: radians - about 3 to 75 degrees
    const float MinEyeHeight = 1.0f, MaxEyeHeight = 2.1f;   // RA01: your head's height over the floor is taken as V's eye height over her feet, kept sane
    const int MeleeTargeting = 9, MeleeThrowAttack = 19;    // RA01: M22-5 - the game's melee states: the knife held in aim, the knife throw
    const float KnifeGravity = 20f;                         // RA01: M22-5 - the knife's own gravity (its arc settings +0x50, M22-2) - "Knife flight" 0%
    const float StraightGravity = 1.5f;                     // RA01: M22-5c - "Knife flight" 100%: a dart's flight
    const long KnifeWindowMs = 150, KnifeTapMs = 80, KnifeRefireMs = 700;

    // RA01: the hand history: per frame its time, then left x, y, z and right x, y, z (OpenXR floor space, metres) - arrays, not Vector3 fields (the ILC quirk).
    static readonly long[] times = new long[HistoryFrames];
    static readonly float[] hands = new float[HistoryFrames * 6];
    static int newest = -1, count;
    static long lastFrameTime;
    static readonly float[] head = new float[8];            // RA01: the latest head: x, y, z, then orientation x, y, z, w
    static float leftGripWas, rightGripWas;

    // RA01: a taken swing: the hand's velocity and place from the head (floor space), the head's facing (forward x, z) and height.
    static readonly float[] release = new float[12];        // RA01: vx, vy, vz, dx, dy, dz, forward x, forward z, eye height, then (M22-5b) the head's 3D look x, y, z
    static long releaseAt;                                  // RA01: a grenade release waiting for the game's throw (0 = none)
    static long knifeAt, knifeTapAt;                        // RA01: M22-5 - a knife swing taken (0 = none), and our last RT press
    static int lastGadget = -1, lastMelee = -1;

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one look: the lever's bookkeeping, the hand history, then the grenade and the knife. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        ThrowLever.Tick();
        if ((!Tuning.PhysicalThrow && !Tuning.PhysicalKnifeThrow) || !ThrowLever.Ready) { releaseAt = knifeAt = 0; lastGadget = lastMelee = -1; return; }
        Sample();
        if (Tuning.PhysicalThrow) Grenade(); else { releaseAt = 0; lastGadget = -1; ThrowLever.HidePreviews(false); }
        if (Tuning.PhysicalKnifeThrow) Knife(); else { knifeAt = 0; lastMelee = -1; }
    }

    // RA01: the grenade: a grip let go while one is in hand is a release; the game's throw starting soon after is ordered. Cost - CPU: extremely low; GPU: none.
    static void Grenade()
    {
        int gadget = GameAnchor.TryGadgetState(out int g) ? g : -1;
        float left = Controllers.LeftGrip, right = Controllers.RightGrip;
        bool inHand = gadget is 2 or 3;
        ThrowLever.HidePreviews(gadget is 1 or 2 or 3);   // RA01: M36-1 - no aim lines or landing circle on a physically thrown grenade
        if (inHand && rightGripWas > GripHeld && right < GripReleased) { if (Take(1, VelocityWindowMs)) releaseAt = Environment.TickCount64; }
        else if (inHand && leftGripWas > GripHeld && left < GripReleased) { if (Take(0, VelocityWindowMs)) releaseAt = Environment.TickCount64; }
        if (left > GripHeld || left < GripReleased) leftGripWas = left;
        if (right > GripHeld || right < GripReleased) rightGripWas = right;
        if ((gadget is 4 or 6) && gadget != lastGadget && releaseAt != 0)
        {
            if (Environment.TickCount64 - releaseAt <= ReleaseToThrowMs) GrenadeThrow(gadget);
            releaseAt = 0;
        }
        lastGadget = gadget;
    }

    // RA01: M22-5 - the knife: while aimed, a fast forward swing presses RT (the throw) and is taken; the game's knife throw starting is ordered (a swing taken
    // then if we did not press RT ourselves). Cost - CPU: extremely low (a short walk back through the history per controller read while aimed); GPU: none.
    static void Knife()
    {
        int melee = GameAnchor.TryMeleeState(out int m) ? m : -1;
        long now = Environment.TickCount64;
        int hand = Tuning.LeftHanded ? 0 : 1;
        if (melee == MeleeTargeting && now - knifeTapAt > KnifeRefireMs && !SettingsMenu.Open && !MenuSense.ShowsMenu && Take(hand, VelocityWindowMs))
        {
            float speed = new Vector3(release[0], release[1], release[2]).Length();
            float forward = release[0] * release[6] + release[2] * release[7];
            if (speed >= Tuning.KnifeThrowSpeed && forward > 0f)
            {
                knifeAt = knifeTapAt = now;
                ControllerMapping.TapRightTrigger((int)KnifeTapMs, $"knife throw - the knife hand swung forward at {speed:F1} m/s");
            }
        }
        if (melee == MeleeThrowAttack && lastMelee != MeleeThrowAttack)
        {
            if (now - knifeAt > ReleaseToThrowMs && !Take(hand, KnifeWindowMs)) knifeAt = 0;   // RA01: RT pressed by you: the last moment of the hand
            else KnifeThrow();
            knifeAt = 0;
        }
        lastMelee = melee;
    }

    // RA01: keeps the hands (and the head) of each new headset frame. Cost - CPU: extremely low (a dozen stores per new frame); GPU: none.
    static void Sample()
    {
        if (!Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid || !f.LeftValid || !f.RightValid || f.Time == lastFrameTime) return;
        lastFrameTime = f.Time;
        newest = (newest + 1) % HistoryFrames;
        if (count < HistoryFrames) count++;
        times[newest] = f.Time;
        int at = newest * 6;
        hands[at] = f.Left.Position.X; hands[at + 1] = f.Left.Position.Y; hands[at + 2] = f.Left.Position.Z;
        hands[at + 3] = f.Right.Position.X; hands[at + 4] = f.Right.Position.Y; hands[at + 5] = f.Right.Position.Z;
        head[0] = f.Head.Position.X; head[1] = f.Head.Position.Y; head[2] = f.Head.Position.Z;
        head[3] = f.Head.Orientation.X; head[4] = f.Head.Orientation.Y; head[5] = f.Head.Orientation.Z; head[6] = f.Head.Orientation.W;
    }

    // RA01: takes a swing into 'release': the hand's velocity over the last 'windowMs' (newest place minus the place that long ago), its place from the head, and the
    // head's facing and height. False if there is not enough history yet. Cost - CPU: extremely low (a short walk back through the history); GPU: none.
    static bool Take(int hand, long windowMs)
    {
        if (count < 3) return false;
        long window = windowMs * Stopwatch.Frequency / 1000;
        int old = newest;
        for (int i = 1; i < count; i++)
        {
            int k = (newest - i + HistoryFrames) % HistoryFrames;
            old = k;
            if (times[newest] - times[k] >= window) break;
        }
        float dt = (times[newest] - times[old]) / (float)Stopwatch.Frequency;
        if (dt <= 0f) return false;
        int a = newest * 6 + hand * 3, b = old * 6 + hand * 3;
        release[0] = (hands[a] - hands[b]) / dt; release[1] = (hands[a + 1] - hands[b + 1]) / dt; release[2] = (hands[a + 2] - hands[b + 2]) / dt;
        release[3] = hands[a] - head[0]; release[4] = hands[a + 1] - head[1]; release[5] = hands[a + 2] - head[2];
        Vector3 facing = Vector3.Transform(-Vector3.UnitZ, Quaternion.Normalize(new Quaternion(head[3], head[4], head[5], head[6]))); // RA01: OpenXR looks down -Z, Y is up
        facing.Y = 0;
        if (facing.LengthSquared() < 1e-4f) return false;
        facing = Vector3.Normalize(facing);
        release[6] = facing.X; release[7] = facing.Z; release[8] = head[1];
        Vector3 look = Vector3.Transform(-Vector3.UnitZ, Quaternion.Normalize(new Quaternion(head[3], head[4], head[5], head[6])));   // RA01: M22-5b - for the knife's aim help
        release[9] = look.X; release[10] = look.Y; release[11] = look.Z;
        return true;
    }

    // RA01: the taken swing laid into the game world: its velocity (game metres a second, z up), the hand's place, and the floor height under V. False if the
    // game's view can't be read. Cost - CPU: extremely low (two dot-product sets, a quaternion turn, a sine and cosine); GPU: none.
    static bool ToGame(out Vector3 velocity, out Vector3 start, out float feetZ, out float vf, out float vr, out float vu)
    {
        Vector3 v = new(release[0], release[1], release[2]), d = new(release[3], release[4], release[5]);
        Vector3 roomForward = new(release[6], 0f, release[7]);
        Vector3 roomRight = Vector3.Cross(roomForward, Vector3.UnitY);
        vf = Vector3.Dot(v, roomForward); vr = Vector3.Dot(v, roomRight); vu = v.Y;
        float df = Vector3.Dot(d, roomForward), dr = Vector3.Dot(d, roomRight), du = d.Y;
        velocity = start = default; feetZ = 0f;

        // RA01: the game's view, flat: V's body turn (the game camera before head steering; forward is +Y, up is +Z) turned by your neck angle (left positive).
        Vector3 body = Vector3.Transform(Vector3.UnitY, CameraHook.ReadGameCameraTurn());
        body.Z = 0;
        if (body.LengthSquared() < 1e-4f) return false;
        body = Vector3.Normalize(body);
        float neck = CameraHook.ReadNeckDegrees(out bool neckValid) * MathF.PI / 180f;
        if (!neckValid) neck = 0f;
        float c = MathF.Cos(neck), s = MathF.Sin(neck);
        Vector2 viewF = new(body.X * c - body.Y * s, body.X * s + body.Y * c);
        Vector2 viewR = new(viewF.Y, -viewF.X);

        CameraHook.ReadGameCamera(out float cx, out float cy, out float cz);
        Vector2 flat = viewF * vf + viewR * vr, at = new Vector2(cx, cy) + viewF * df + viewR * dr;
        velocity = new Vector3(flat.X, flat.Y, vu);
        start = new Vector3(at.X, at.Y, cz + du);
        feetZ = cz - Math.Clamp(release[8], MinEyeHeight, MaxEyeHeight);
        return true;
    }

    // RA01: the game's grenade throw has started after a release: the landing spot and angle (see the class comment), ordered on the lever.
    // Cost - CPU: extremely low (a few dozen multiplies, a square root, an arctangent and a log line, once per throw); GPU: none.
    static void GrenadeThrow(int gadget)
    {
        float handSpeed = new Vector3(release[0], release[1], release[2]).Length();
        if (handSpeed < MinThrowSpeed) { Log.Write($"RA01 throw: hand at {handSpeed:F1} m/s on release - too slow for a physical throw, the game's own throw"); return; }
        if (!ToGame(out Vector3 velocity, out Vector3 start, out float feetZ, out float vf, out float vr, out float vu)) return;
        Land($"V's {(gadget == 6 ? "quick" : "held")} grenade throw: hand {handSpeed:F1} m/s (forward {vf:F1}, right {vr:F1}, up {vu:F1}) x{Tuning.ThrowStrength:F1}",
            velocity * Tuning.ThrowStrength, start, feetZ, GameGravity);
    }

    // RA01: M22-5b - follows a thrown thing (game velocity, z up) from 'start' down to the floor under V with 'gravity': the landing spot (capped at MaxRange, at
    // least MinRange) and the angle it leaves at (MinAngle..MaxAngle radians), ordered on the lever. Shared by grenades and knives.
    // Cost - CPU: extremely low (a few dozen multiplies, a square root, an arctangent and a log line, once per throw); GPU: none.
    static void Land(string what, Vector3 velocity, Vector3 start, float feetZ, float gravity, bool ownGravity = false)
    {
        Vector2 flatVelocity = new(velocity.X, velocity.Y);
        float upVelocity = velocity.Z;
        // RA01: height(t) = startZ + up x t - g/2 x t^2 = feetZ, the later root.
        float drop = MathF.Max(0f, start.Z - feetZ);
        float t = (upVelocity + MathF.Sqrt(upVelocity * upVelocity + 2f * gravity * drop)) / gravity;
        Vector2 travel = flatVelocity * t;
        float range = travel.Length();
        if (range < 1e-3f) return;                                                   // RA01: thrown straight up or down - the game's own throw
        if (range < MinRange) travel *= MinRange / range;
        else if (range > MaxRange) travel *= MaxRange / range;
        Vector2 land = new Vector2(start.X, start.Y) + travel;
        float angle = Math.Clamp(MathF.Atan2(upVelocity, flatVelocity.Length()), MinAngle, MaxAngle);
        string why = $"{what}, lands {travel.Length():F1} m away at ({land.X:F1}, {land.Y:F1}, {feetZ:F1}), leaving at {angle * 180f / MathF.PI:F0} deg{(ownGravity ? $", gravity {gravity:F1} m/s2" : "")}";
        if (ThrowLever.OrderTargetLanding(land.X, land.Y, feetZ, angle, why, ownGravity ? gravity : 0f)) Log.Write($"RA01 throw: ordered - {why}");
    }

    // RA01: M22-5 - the game's knife throw has started after a swing. M22-5b: the swing's direction mixed with your look by "Knife aim help", its speed (your
    // hand's) times "Knife throw strength", then flown to the floor with the knife's gravity like a grenade (Land).
    // Cost - CPU: extremely low (a few dozen multiplies, two square roots, an arctangent and a log line, once per throw); GPU: none.
    static void KnifeThrow()
    {
        Vector3 hand = new(release[0], release[1], release[2]);
        float handSpeed = hand.Length();
        if (handSpeed < MinThrowSpeed) { Log.Write($"RA01 throw: knife hand at {handSpeed:F1} m/s - too slow for a physical throw, the game's own throw along your view"); return; }
        Vector3 look = new(release[9], release[10], release[11]);
        if (look.LengthSquared() > 1e-4f)
        {
            Vector3 aimed = Vector3.Lerp(hand / handSpeed, Vector3.Normalize(look), Tuning.KnifeAimHelp);   // RA01: M22-5b - only the direction is mixed
            if (aimed.LengthSquared() > 1e-4f) hand = Vector3.Normalize(aimed) * handSpeed;
            release[0] = hand.X; release[1] = hand.Y; release[2] = hand.Z;
        }
        if (!ToGame(out Vector3 velocity, out Vector3 start, out float feetZ, out float vf, out float vr, out float vu)) return;
        Land($"V's knife throw: hand {handSpeed:F1} m/s (after {Tuning.KnifeAimHelp * 100f:F0}% aim help: forward {vf:F1}, right {vr:F1}, up {vu:F1}) x{Tuning.KnifeThrowStrength:F1}",
            velocity * Tuning.KnifeThrowStrength, start, feetZ, KnifeGravity * MathF.Pow(StraightGravity / KnifeGravity, Tuning.KnifeFlight), true);  // RA01: M22-5c
    }
}
