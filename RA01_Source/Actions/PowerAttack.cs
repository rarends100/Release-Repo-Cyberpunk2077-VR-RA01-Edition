using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M27-2 - PHYSICAL POWER ATTACKS (Weapons tab "Physical power attacks", on by default; Rob's design 2026-10-07/08). The game's strong melee attack is
// "hold the attack trigger, then let go". Here your body does it:
//  - WIND-UP: pull one or both hands back BEHIND your shoulder (behind your head's line, out to the side) and hold it there HoldMs (0.1 s) - like a boxer
//    or a martial artist loading a big strike. The mod holds RT (the game charges the strong attack).
//  - GORILLA POUND: both hands up ABOVE and slightly behind your head for HoldMs - like a gorilla before slamming the ground. RT is held the same way.
//  - RELEASE: the moment the loaded hand(s) leave the pose - the swing forward, or the slam down - RT is let go and the game throws the strong attack.
//    A wind-up held longer than MaxHoldMs is let go by itself, so the trigger can never stick.
// Which hand: fists and melee cyberware (Mantis Blades, Gorilla Arms, Monowire) - either hand; a melee weapon - the weapon hand (or both hands together).
// Not with guns, not in a menu, the F10 window or gesture mode. Physical melee's normal swing strikes (MeleeActions) wait while a power attack is loading
// and just after it goes, so one big swing is one strong attack, never also a quick one. Your own trigger always still works.
// Measured like MeleeActions: your hands from your nose along your facing - "how far in front, how far out to the side, how high".
// Builder picture: drawing back a sledgehammer - the swing only counts once the hammer is fully up, and it lands the moment you bring it down.
// Cost - CPU: extremely low (a few dot products and compares per controller read, about 60 a second, only with a melee weapon out); GPU: none.
internal static class PowerAttack
{
    const long HoldMs = 100;                 // RA01: Rob - the pose must be held this long before RT goes down
    const long MaxHoldMs = 5000;             // RA01: a safety: RT is let go after this however the pose is held
    const long QuietAfterMs = 400;           // RA01: normal swing strikes wait this long after the strong attack goes
    const float BehindHead = 0.02f;          // RA01: wind-up: the hand at least this far behind the head's line (metres along your facing; minus = behind)
    const float OutToSide = 0.12f;           // RA01: wind-up: and at least this far out to the side (the shoulder)
    const float LowestWindUp = -0.75f;       // RA01: wind-up: not lower than this below the head (a hand hanging at your hip is not a wind-up)
    const float AboveHead = 0.10f;           // RA01: gorilla: both hands at least this high above the head...
    const float NotInFront = 0.15f;          // RA01: ...and no further forward than this (above or behind it)

    static long poseSince, heldSince, quietUntil;
    static bool holding;
    static string kindNow = "";

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: true while a power attack is loading, and for QuietAfterMs after it went - MeleeActions' swing strikes wait then. Any thread.
    // RA01: Cost - CPU: extremely low (two reads); GPU: none.
    public static bool Busy => holding || Environment.TickCount64 < Volatile.Read(ref quietUntil);

    // RA01: true while RT is being held for a power attack (Visuals' cyberware arms come out as for your own held trigger). Cost - CPU: extremely low; GPU: none.
    public static bool Charging => holding;

    // RA01: one look: what V holds, the pose, the hold and the release. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        WeaponProbe.Kind kind = WeaponProbe.Held;
        bool usable = Tuning.PhysicalPowerAttacks && IsMelee(kind) && !SettingsMenu.Open && !MenuSense.ShowsMenu && !GestureMode.Posing && !VehicleState.Mounted;
        long now = Environment.TickCount64;
        if (!usable || !Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid || !f.LeftValid || !f.RightValid) { Release(now, "power attack off (no melee weapon, a menu, or tracking lost)", false); poseSince = 0; return; }

        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, Quaternion.Normalize(new Quaternion(f.Head.Orientation.X, f.Head.Orientation.Y, f.Head.Orientation.Z, f.Head.Orientation.W)));
        forward.Y = 0;
        if (forward.LengthSquared() < 1e-4f) return;
        forward = Vector3.Normalize(forward);
        Vector3 side = Vector3.Cross(forward, Vector3.UnitY);
        Vector3 head = new(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z);
        Vector3 left = new Vector3(f.Left.Position.X, f.Left.Position.Y, f.Left.Position.Z) - head;
        Vector3 right = new Vector3(f.Right.Position.X, f.Right.Position.Y, f.Right.Position.Z) - head;

        bool eitherHand = kind != WeaponProbe.Kind.Melee;                       // RA01: a melee weapon loads with the weapon hand; fists and cyberware with either
        bool weaponLeft = Tuning.LeftHanded;
        bool windUp = eitherHand ? WindUp(left, forward, side) || WindUp(right, forward, side) : WindUp(weaponLeft ? left : right, forward, side);
        bool gorilla = Overhead(left, forward) && Overhead(right, forward);
        bool pose = windUp || gorilla;

        if (holding)
        {
            if (!pose) Release(now, $"power attack - {kindNow} released", true);
            else if (now - heldSince > MaxHoldMs) Release(now, "power attack - held too long, let go", true);
            return;
        }
        if (!pose) { poseSince = 0; return; }
        if (poseSince == 0) { poseSince = now; return; }
        if (now - poseSince < HoldMs) return;
        holding = true; heldSince = now; kindNow = gorilla ? "gorilla pound" : "wind-up";
        ControllerMapping.HoldRightTrigger(true, $"power attack - {kindNow} held {HoldMs} ms ({kind})");
    }

    // RA01: a hand behind the head's line, out to the side, not hanging low - the loaded strike. Cost - CPU: extremely low (two dot products); GPU: none.
    static bool WindUp(Vector3 hand, Vector3 forward, Vector3 side) =>
        Vector3.Dot(hand, forward) < -BehindHead && MathF.Abs(Vector3.Dot(hand, side)) > OutToSide && hand.Y > LowestWindUp;

    // RA01: a hand above the head and not out in front of it. Cost - CPU: extremely low (a dot product); GPU: none.
    static bool Overhead(Vector3 hand, Vector3 forward) => hand.Y > AboveHead && Vector3.Dot(hand, forward) < NotInFront;

    // RA01: lets RT go (the strong attack goes if it was held long enough for the game) and starts the quiet time for normal swings. Cost - CPU: extremely low; GPU: none.
    static void Release(long now, string why, bool fired)
    {
        if (!holding) return;
        holding = false; poseSince = 0;
        if (fired) Volatile.Write(ref quietUntil, now + QuietAfterMs);
        ControllerMapping.HoldRightTrigger(false, why);
    }

    // RA01: the melee kinds a strong attack exists for. Cost - CPU: extremely low (compares); GPU: none.
    static bool IsMelee(WeaponProbe.Kind k) => k is WeaponProbe.Kind.Fists or WeaponProbe.Kind.Melee or WeaponProbe.Kind.MantisBlades or WeaponProbe.Kind.GorillaArms or WeaponProbe.Kind.Monowire;
}
