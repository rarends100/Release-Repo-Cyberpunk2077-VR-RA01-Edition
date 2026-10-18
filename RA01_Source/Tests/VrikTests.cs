using System.Numerics;
using RA01.Visuals;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - VRIK's arm solver (Visuals\VRIK.cs SolveArm) on a small fake arm, and its turn helpers.
// THE ARM SOLVER, the builder's way: the arm is two beams on hinges - the upper arm (shoulder to elbow) and the forearm (elbow to wrist). The beams can't
// stretch, so for a hand target the only question is where the elbow goes. Shoulder, elbow and wrist make a triangle whose three sides we know (both
// beams, and the shoulder-to-target distance); the "law of cosines" (a college-algebra formula) gives the shoulder's angle from those three sides. The
// "pole" is a string pulling the elbow one way (down and out), so it bends like an elbow and not like a knee. A target too far: the arm points straight
// at it. Too close: folded as far as it goes.
// These tests build that arm in memory (4 bones: collarbone, upper arm, forearm, hand; real VRIK bone slots, 48 bytes each) and check that the hand
// lands on the target, the beams kept their lengths, and the turns written into the bones really put the hand there when stacked back up.
// Cost - CPU: extremely low (a few solves of a 4-bone chain); GPU: none.
public sealed unsafe class VrikTests : IDisposable
{
    const int RightShoulder = 8, SlotBytes = 0x30;
    delegate string? SolveFn(byte* bones, int shoulder, float armSize, Vector3 target, Vector3 poleDir, out Vector3 elbow, out Vector3 wrist, out Quaternion fore);
    delegate void PlaceFn(byte* bones, int s, out Vector3 pos, out Quaternion turn);
    delegate void SplitFn(Quaternion d, Vector3 axis, out float rollDeg, out float bendDeg);
    static readonly Type Vrik = typeof(VRIK);
    readonly Action restore = Kit.Remember(typeof(VRIK));
    readonly byte[] bones = new byte[4 * SlotBytes];

    // RA01: VRIK's own state back as it was. Cost - CPU: extremely low; GPU: none.
    public void Dispose() => restore();

    // RA01: builds the fake arm: collarbone at the origin (turned 'shoulderTurn', size 'shoulderSize'), the upper arm bolted 15 cm out, the elbow 30 cm further,
    // RA01: the wrist 25 cm further - all in a straight line along x. Points VRIK's bone map (slot) and parent list at it. Cost - CPU: extremely low; GPU: none.
    void BuildArm(Quaternion shoulderTurn, float shoulderSize = 1f)
    {
        Array.Clear(bones);
        Vector3[] offsets = { Vector3.Zero, new(0.15f, 0, 0), new(0.30f, 0, 0), new(0.25f, 0, 0) };
        fixed (byte* b = bones)
            for (int i = 0; i < 4; i++)
            {
                float* p = (float*)(b + i * SlotBytes);
                p[0] = offsets[i].X; p[1] = offsets[i].Y; p[2] = offsets[i].Z;
                Quaternion q = i == 0 ? shoulderTurn : Quaternion.Identity;
                p[4] = q.X; p[5] = q.Y; p[6] = q.Z; p[7] = q.W;
                p[8] = p[9] = p[10] = i == 0 ? shoulderSize : 1f;
            }
        int[] slot = Kit.Get<int[]>(Vrik, "slot");
        for (int i = 0; i < 4; i++) slot[RightShoulder + i] = i;
        Kit.Set(Vrik, "parentOf", new short[] { -1, 0, 1, 2 });
    }

    // RA01: where bone 'i' sits once every bone above it is stacked up (VRIK's own BodyPlace). Cost - CPU: extremely low; GPU: none.
    Vector3 Place(int i)
    {
        fixed (byte* b = bones) { Kit.Method<PlaceFn>(Vrik, "BodyPlace")(b, i, out Vector3 pos, out _); return pos; }
    }

    // RA01: runs SolveArm on the fake arm. Cost - CPU: extremely low; GPU: none.
    (string? note, Vector3 elbow, Vector3 wrist) Solve(Vector3 target, Vector3 pole, float armSize = 1f)
    {
        fixed (byte* b = bones)
        {
            string? note = Kit.Method<SolveFn>(Vrik, "SolveArm")(b, RightShoulder, armSize, target, pole, out Vector3 e, out Vector3 w, out _);
            return (note, e, w);
        }
    }

    // RA01: a reachable target: the hand lands on it, both beams keep their lengths, the elbow bends toward the pole, and stacking the written turns back up
    // puts the elbow and hand bones exactly where the solver said. Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData(0.45f, 0.30f, 0.00f)]
    [InlineData(0.30f, 0.20f, 0.25f)]
    [InlineData(0.15f, 0.00f, -0.40f)]
    [InlineData(0.55f, 0.05f, 0.10f)]
    public void Reachable_target_is_reached(float x, float y, float z)
    {
        BuildArm(Quaternion.Identity);
        Vector3 shoulder = Place(1), target = new(x, y, z), pole = new(0, 0, -1);
        var (note, elbow, wrist) = Solve(target, pole);
        Assert.NotNull(note);
        Kit.Near(target, wrist, 1e-4f, "wrist");
        Kit.Near(0.30f, Vector3.Distance(shoulder, elbow), 1e-4f, "upper arm length");
        Kit.Near(0.25f, Vector3.Distance(elbow, wrist), 1e-4f, "forearm length");
        Kit.Near(elbow, Place(2), 1e-4f, "elbow bone after stacking the written turns");
        Kit.Near(wrist, Place(3), 1e-4f, "hand bone after stacking the written turns");
        Vector3 line = Vector3.Normalize(target - shoulder), side = elbow - shoulder - line * Vector3.Dot(elbow - shoulder, line);
        Assert.True(Vector3.Dot(side, pole - line * Vector3.Dot(pole, line)) >= -1e-5f, "the elbow bent away from the pole (it should bend toward it)");
    }

    // RA01: a target out of reach: the arm points straight at it, fully stretched (minus a hair, so the triangle never goes flat).
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Far_target_gives_a_straight_arm_pointing_at_it()
    {
        BuildArm(Quaternion.Identity);
        Vector3 shoulder = Place(1), target = new(2f, 1f, -0.5f);
        var (_, elbow, wrist) = Solve(target, new Vector3(0, 0, -1));
        Vector3 dir = Vector3.Normalize(target - shoulder);
        Kit.Near(shoulder + dir * (0.55f - 0.001f), wrist, 1e-4f, "wrist at full reach toward the target");
        Kit.Near(0.30f, Vector3.Distance(shoulder, elbow), 1e-4f, "upper arm length");
        Kit.Near(wrist, Place(3), 1e-4f, "hand bone after stacking");
    }

    // RA01: a target closer than the arm can fold: the hand stops at the closest it can reach, on the line to the target. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Close_target_folds_the_arm()
    {
        BuildArm(Quaternion.Identity);
        Vector3 shoulder = Place(1), target = shoulder + new Vector3(0.01f, 0.005f, 0);
        var (_, elbow, wrist) = Solve(target, new Vector3(0, 0, -1));
        Kit.Near(0.05f + 0.001f, Vector3.Distance(shoulder, wrist), 1e-4f, "folded reach (upper arm minus forearm)");
        Kit.Near(0.25f, Vector3.Distance(elbow, wrist), 1e-4f, "forearm length");
    }

    // RA01: a turned and bigger collarbone, and the arm-size setting (M9-6: written on the upper arm, so BOTH beams grow): the beams grow with the sizes, and the hand still lands exactly on the target.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Turned_and_sized_arm_still_reaches()
    {
        BuildArm(Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f)), 0.7f), shoulderSize: 1.1f);
        const float armSize = 1.2f;
        Vector3 shoulder = Place(1);
        Vector3 target = shoulder + Vector3.Normalize(new Vector3(0.2f, 0.7f, -0.3f)) * 0.5f;
        var (_, elbow, wrist) = Solve(target, new Vector3(0, 0, -1), armSize);
        Kit.Near(target, wrist, 1e-4f, "wrist");
        Kit.Near(0.30f * 1.1f * armSize, Vector3.Distance(shoulder, elbow), 1e-4f, "upper arm (collarbone size x arm size: the arm size sits on the upper arm, so both beams grow)");
        Kit.Near(0.25f * 1.1f * armSize, Vector3.Distance(elbow, wrist), 1e-4f, "forearm (collarbone size x arm size)");
        Kit.Near(wrist, Place(3), 1e-4f, "hand bone after stacking");
    }

    // RA01: no target at all (the shoulder itself) is refused, nothing written. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Target_on_the_shoulder_is_refused()
    {
        BuildArm(Quaternion.Identity);
        var (note, _, _) = Solve(Place(1), new Vector3(0, 0, -1));
        Assert.Null(note);
    }

    // RA01: TurnBetween swings one direction onto another, including the half-turn case (opposite directions). Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData(1f, 0f, 0f, 0f, 1f, 0f)]
    [InlineData(0f, 0f, 1f, 0.6f, 0f, 0.8f)]
    [InlineData(1f, 0f, 0f, -1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f, 0f, -1f, 0f)]
    public void Turn_between_two_directions(float ax, float ay, float az, float bx, float by, float bz)
    {
        Vector3 a = new(ax, ay, az), b = new(bx, by, bz);
        Quaternion q = (Quaternion)Kit.Call(Vrik, "TurnBetween", a, b)!;
        Kit.Near(b, Vector3.Transform(a, q), 1e-4f, "turned direction");
        Kit.Near(1f, q.Length(), 1e-4f, "a pure turn (unit length)");
    }

    // RA01: SplitTurn: a turn split into its screwdriver roll about the forearm and its hinge bend - each alone, and a bend after a roll.
    // Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData(40f, 0f)]
    [InlineData(-75f, 0f)]
    [InlineData(0f, 30f)]
    [InlineData(20f, 30f)]
    [InlineData(-60f, 90f)]
    public void Split_turn_into_roll_and_bend(float roll, float bend)
    {
        Vector3 axis = Vector3.UnitY, hinge = Vector3.UnitX;
        Quaternion d = Quaternion.CreateFromAxisAngle(hinge, bend * MathF.PI / 180f) * Quaternion.CreateFromAxisAngle(axis, roll * MathF.PI / 180f);
        Kit.Method<SplitFn>(Vrik, "SplitTurn")(d, axis, out float gotRoll, out float gotBend);
        Kit.Near(roll, gotRoll, 0.05f, "roll");
        Kit.Near(bend, gotBend, 0.05f, "bend");
    }

    // RA01: the small angle helpers: the angle between two directions, an angle folded into -180..180, and the left/right mirror of a turn.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Angle_helpers()
    {
        Kit.Near(90f, (float)Kit.Call(Vrik, "AngleDeg", Vector3.UnitX, Vector3.UnitY * 3)!, 1e-3f, "right angle");
        Kit.Near(180f, (float)Kit.Call(Vrik, "AngleDeg", Vector3.UnitX, -Vector3.UnitX)!, 1e-3f, "opposite");
        Assert.Equal(0f, (float)Kit.Call(Vrik, "AngleDeg", Vector3.Zero, Vector3.UnitX)!);
        Kit.Near(-170f, (float)Kit.Call(Vrik, "WrapDeg", 190f)!, 1e-3f, "190 folded");
        Kit.Near(10f, (float)Kit.Call(Vrik, "WrapDeg", 730f)!, 1e-3f, "730 folded");
        Kit.Near(-90f, (float)Kit.Call(Vrik, "WrapDeg", -90f)!, 1e-3f, "-90 stays");
        Assert.Equal(new Quaternion(0.1f, -0.2f, -0.3f, 0.9f), (Quaternion)Kit.Call(Vrik, "Mirror", new Quaternion(0.1f, 0.2f, 0.3f, 0.9f))!);
    }
}
