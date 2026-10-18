using System.Diagnostics;
using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M27-4 - WEAPON WEIGHT (Weapons tab "Weapon weight", "Weapon weight strength"; Rob 2026-10-08: realistic per type, katanas light). No more weightless guns:
// V's weapon hand follows your controller through a soft SPRING - a light weapon follows almost at once, a heavy one lags a little behind quick moves and
// settles in smoothly - and, held in one hand, its nose droops a few degrees under its own weight. Holding it with BOTH hands (the two-handed
// grip) halves the droop and stiffens the spring, as a second hand does in real life. Your shots and the laser follow the weighted gun (they use its barrel).
// THE WEIGHTS are real-world masses for each weapon type (WeightKg): a pistol about 1 kg, a katana 1.2 kg (light), a rifle 3.5 kg, a sniper
// 7 kg, a light machine gun 9 kg, a heavy machine gun torn off a turret 30 kg, a sledgehammer 6 kg. The spring's quickness is Stiffness / sqrt(mass) (a
// weight on a spring swings slower the heavier it is); the droop is DroopPerKg degrees per kg (capped). "Weapon weight strength" scales the mass (0-200%).
// Builder picture: carrying a beam on a bungee cord instead of welded to your hand - a broom handle follows your hand, a steel girder sways behind it.
// Not with fists or cyberware (no weapon), not seated in a vehicle (the steering wheel owns the hands there), and reset after any jump (a draw, a teleport).
// Cost - CPU: extremely low (a few small spring steps, a few vector sums and one quaternion blend per frame, on VRIK's animation thread); GPU: none.
internal static class WeaponWeight
{
    const float Stiffness = 80f;             // RA01: rad/s for a 1 kg weapon; heavier = Stiffness / sqrt(kg). 90% of a sudden move is followed in about: pistol 45 ms, rifle 80,
                                             // RA01: sniper 100, HMG 200 (checked in a small simulation before delivery)
    const float Damping = 0.8f;              // RA01: just under 1: a heavy weapon overshoots a touch as it settles, a light one not visibly
    const float DroopPerKg = 0.5f, MaxDroopDeg = 10f;  // RA01: one-handed: half a degree of nose-down per kg (a pistol 0.5, a rifle 1.8, an HMG 10)
    const float TwoHands = 0.5f;             // RA01: with the two-handed grip: half the droop, the spring twice as stiff
    const float JumpM = 0.5f;                // RA01: a target jump bigger than this (a draw, a teleport, a recentre) resets the spring
    const float MaxStep = 0.05f;             // RA01: seconds - a longer frame gap (a hitch) is taken as this, so the spring never explodes

    // RA01: real-world masses by the game's weapon type names (WeaponEventProbe's English names; Smart / Tech variants share their base type's name).
    static readonly (string Name, float Kg)[] WeightKg =
    {
        ("Handgun", 1.0f), ("Revolver", 1.4f), ("SMG", 2.8f), ("Precision rifle", 4.5f), ("Sniper rifle", 7.0f), ("Assault rifle", 3.5f), ("Rifle", 3.5f),
        ("Double-barrel shotgun", 3.2f), ("Shotgun", 3.6f), ("Light machine gun", 9.0f), ("Heavy machine gun", 30f), ("Grenade launcher", 4.0f),
        ("Katana", 1.2f), ("Long blade", 1.5f), ("Sword", 1.4f), ("Short blade", 0.6f), ("Knife", 0.25f), ("Machete", 0.8f), ("Chainsword", 4.0f), ("Axe", 2.0f),
        ("Hammer", 6.0f), ("Two-handed club", 2.0f), ("One-handed club", 1.0f), ("Melee weapon", 1.5f),
    };

    static readonly float[] spring = new float[6];   // RA01: the weighted hand's place (x, y, z) and speed (x, y, z) on V's body - arrays, not Vector3 fields (the ILC quirk)
    static Quaternion turnNow = Quaternion.Identity;
    static bool started;
    static long lastTicks;
    static string lastName = "";

    // RA01: HandTargets, once per frame, for the weapon hand ('head' = your head's centre on V's body): replaces 'hand' and 'turn' with the weighted ones.
    // Does nothing (and forgets the spring) when off, with no weapon, seated, or the hand untracked. Cost - CPU: extremely low (see the class comment); GPU: none.
    public static void Apply(Vector3 head, ref Vector3 hand, ref Quaternion turn, bool ok)
    {
        float kg = Mass();
        long now = Stopwatch.GetTimestamp();
        if (!ok || kg <= 0f || VehicleState.Mounted) { started = false; lastTicks = now; return; }
        float dt = lastTicks == 0 ? 0f : Math.Min((now - lastTicks) / (float)Stopwatch.Frequency, MaxStep);
        lastTicks = now;
        Vector3 at = new(spring[0], spring[1], spring[2]), speed = new(spring[3], spring[4], spring[5]);
        if (!started || Vector3.Distance(at, hand) > JumpM || WeaponProbe.HeldName != lastName)
        {
            started = true; lastName = WeaponProbe.HeldName;
            at = hand; speed = Vector3.Zero; turnNow = turn;
        }
        bool twoHands = TwoHandGrip.Holding;
        float w = Stiffness / MathF.Sqrt(kg) * (twoHands ? 1f / TwoHands : 1f);     // RA01: how quick the spring is (rad/s)
        if (dt > 0f)
        {
            int steps = Math.Clamp((int)MathF.Ceiling(w * dt / 0.4f), 1, 64);         // RA01: small steps, so a stiff spring or a long frame never overshoots wildly
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                Vector3 pull = (hand - at) * (w * w) - speed * (2f * Damping * w);   // RA01: the spring's pull toward your hand, minus its damping
                speed += pull * h;
                at += speed * h;
            }
            turnNow = Quaternion.Normalize(Quaternion.Slerp(turnNow, turn, 1f - MathF.Exp(-w * dt))); // RA01: the turn follows at the same quickness
        }
        spring[0] = at.X; spring[1] = at.Y; spring[2] = at.Z; spring[3] = speed.X; spring[4] = speed.Y; spring[5] = speed.Z;

        float droop = MathF.Min(kg * DroopPerKg, MaxDroopDeg) * (twoHands ? TwoHands : 1f) * (MathF.PI / 180f);
        Vector3 out_ = at - head; out_.Z = 0;                                         // RA01: the way the hand is held out from you, level (V's body: z up)
        Vector3 axis = out_.LengthSquared() > 1e-4f ? Vector3.Normalize(Vector3.Cross(out_, Vector3.UnitZ)) : Vector3.UnitX; // RA01: the level line across it
        hand = at;
        turn = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, -droop) * turnNow); // RA01: the nose dips about that line
    }

    // RA01: the weapon's mass in kg times the strength, or 0 (no weight: off, fists, cyberware, an unknown type). Cost - CPU: extremely low (a short search); GPU: none.
    static float Mass()
    {
        if (!Tuning.WeaponWeight || Tuning.WeaponWeightStrength <= 0f) return 0f;
        WeaponProbe.Kind kind = WeaponProbe.Held;
        if (kind != WeaponProbe.Kind.Gun && kind != WeaponProbe.Kind.Melee) return 0f;
        string name = WeaponProbe.HeldName;
        foreach (var (n, kg) in WeightKg) if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return kg * Tuning.WeaponWeightStrength;
        return kind == WeaponProbe.Kind.Gun ? 2.5f * Tuning.WeaponWeightStrength : 1.5f * Tuning.WeaponWeightStrength; // RA01: an unnamed gun or blade: a middling weight
    }
}
