using System.Numerics;
using System.Reflection;
using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - the head maths (Core\Video\HeadPose.cs), the scope lens panel's place (Core\Video\ScopeLens.cs Placed), and the game's name codes.
// The head maths are checked against .NET's own System.Numerics turns, an independent "second tape measure": both must agree.
// Builder picture: measuring the same wall with two different tapes - if they disagree, one of them is wrong.
// Cost - CPU: extremely low (a few dozen small calculations); GPU: none.
public sealed class HeadPoseAndNameCodeTests : IDisposable
{
    readonly Action restore = Kit.Remember(typeof(HeadPose));

    // RA01: HeadPose's state back as it was. Cost - CPU: extremely low; GPU: none.
    public void Dispose() => restore();

    // RA01: an OpenXR pose from a System.Numerics position and turn. Cost - CPU: extremely low; GPU: none.
    static Xr.Posef Pose(Vector3 p, Quaternion q) => new() { Position = { X = p.X, Y = p.Y, Z = p.Z }, Orientation = { X = q.X, Y = q.Y, Z = q.Z, W = q.W } };
    static Vector3 V(Xr.Vector3f v) => new(v.X, v.Y, v.Z);
    static Quaternion Q(Xr.Quaternionf q) => new(q.X, q.Y, q.Z, q.W);
    static Quaternion Yaw(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg * MathF.PI / 180f);
    static Quaternion Rot(Vector3 axis, float deg) => Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), deg * MathF.PI / 180f);

    // RA01: the head's facing on the floor: a turn of N degrees to the left about "up" reads as N; looking straight up has no facing.
    // Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData(0f)] [InlineData(30f)] [InlineData(-100f)] [InlineData(179f)]
    public void Yaw_of_a_head(float deg)
    {
        Xr.Posef head = Pose(Vector3.Zero, Yaw(deg) * Rot(Vector3.UnitX, -25f));   // RA01: the head nodded down 25 degrees too: that must not change its facing
        Assert.True(HeadPose.YawOf(head.Orientation, out float yaw));
        Kit.Near(deg, yaw * 180f / MathF.PI, 1e-3f, "yaw");
        Assert.False(HeadPose.YawOf(Pose(Vector3.Zero, Rot(Vector3.UnitX, 90f)).Orientation, out _), "a head looking straight up has no facing");
    }

    // RA01: recentre, then step and turn: forward / right / height / turned-left all read as walked. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Recentre_then_walk_and_turn()
    {
        HeadPose.FloorAssumed = false; HeadPose.FloorY = 0; HeadPose.Ready = false;
        Assert.True(HeadPose.Recentre(Pose(new Vector3(1f, 1.7f, 2f), Yaw(90f))));    // RA01: standing at (1, 2), facing -x (90 degrees left of -z)
        Kit.Near(1.7f, HeadPose.EyeHeight, 1e-5f, "eye height");
        HeadPose.Relative(Pose(new Vector3(0.5f, 1.6f, 1.7f), Yaw(110f)), out float forward, out float right, out float height, out float turned);
        Kit.Near(0.5f, forward, 1e-5f, "forward (half a metre along -x)");
        Kit.Near(0.3f, right, 1e-5f, "right (0.3 m along -z is to the right when facing -x)");
        Kit.Near(1.6f, height, 1e-5f, "height");
        Kit.Near(20f, turned, 1e-3f, "turned left");
        HeadPose.Yaw = 170f * MathF.PI / 180f;
        HeadPose.Relative(Pose(Vector3.Zero, Yaw(-170f)), out _, out _, out _, out turned);
        Kit.Near(20f, turned, 1e-3f, "turning past the back wraps to a small angle");
    }

    // RA01: the flat screen stands 'distance' ahead of the recentre point, half its height above the floor, facing back at the player.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Flat_screen_stands_ahead()
    {
        HeadPose.OriginX = 1; HeadPose.OriginZ = 2; HeadPose.FloorY = 0.1f; HeadPose.Yaw = 90f * MathF.PI / 180f;
        Xr.Posef screen = HeadPose.ScreenPose(3f, 1.5f);
        Kit.Near(new Vector3(1 - 3, 0.1f + 0.75f, 2), V(screen.Position), 1e-5f, "screen centre");
        Kit.SameTurn(Yaw(90f), Q(screen.Orientation), 1e-5f, "screen turned to face the recentre direction");
    }

    // RA01: the eye's pose, the tagged picture's place and the vector turn agree with System.Numerics for an awkward head turn. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Head_maths_agree_with_numerics()
    {
        Quaternion q = Quaternion.Normalize(Rot(new Vector3(0.3f, 1f, -0.2f), 67f) * Rot(Vector3.UnitX, -15f));
        Vector3 at = new(0.4f, 1.65f, -0.8f), eye = new(-0.032f, 0.01f, -0.005f);
        Xr.Posef head = Pose(at, q);
        Xr.Vector3f local = new() { X = eye.X, Y = eye.Y, Z = eye.Z };
        Kit.Near(at + Vector3.Transform(eye, q), V(HeadPose.EyePose(head, local).Position), 1e-5f, "EyePose");
        Kit.Near(at + Vector3.Transform(new Vector3(0, 0, -2.5f), q), V(HeadPose.TaggedScreenPose(head, 2.5f).Position), 1e-5f, "TaggedScreenPose");
        Kit.Near(Vector3.Transform(eye, q), V(HeadPose.Turn(head.Orientation, local)), 1e-6f, "Turn");
    }

    // RA01: Orbited slides a picture across the eye's view: no slide leaves it where it was; any slide keeps it the same distance from the eye and facing it.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Orbited_keeps_facing_the_eye()
    {
        Quaternion q = Rot(new Vector3(0.2f, 1f, 0.1f), 40f);
        Xr.Posef picture = Pose(new Vector3(0.3f, 1.4f, -1.2f), q);
        Xr.Posef same = HeadPose.Orbited(picture, 1.5f, 0f, 0f);
        Kit.Near(V(picture.Position), V(same.Position), 1e-5f, "no slide");
        Vector3 eye = V(picture.Position) + Vector3.Transform(new Vector3(0, 0, 1.5f), q);
        Xr.Posef moved = HeadPose.Orbited(picture, 1.5f, 0.2f, -0.1f);
        Kit.Near(eye, V(moved.Position) + Vector3.Transform(new Vector3(0, 0, 1.5f), Q(moved.Orientation)), 1e-4f, "the eye stays straight in front of the moved picture");
        Assert.True(Vector3.Distance(V(picture.Position), V(moved.Position)) > 0.1f, "the picture moved");
    }

    // RA01: the pose history: a picture's pose is found by its exact stamp, an unknown stamp is not, a recentre forgets them all, and only the last 64 are kept.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Pose_history_finds_by_stamp()
    {
        HeadPose.ForgetHistory();
        for (int i = 1; i <= 3; i++) Kit.Call(typeof(HeadPose), "Remember", 1000L + i, Pose(new Vector3(i, 0, 0), Quaternion.Identity), i % 3);
        Assert.True(HeadPose.PoseOf(1002L, out Xr.Posef p, out int eye));
        Assert.Equal((2f, 2), (p.Position.X, eye));
        Assert.False(HeadPose.PoseOf(999L, out _, out _));
        for (int i = 0; i < 64; i++) Kit.Call(typeof(HeadPose), "Remember", 5000L + i, Pose(Vector3.Zero, Quaternion.Identity), 0);
        Assert.False(HeadPose.PoseOf(1003L, out _, out _), "older than 64 poses must be gone");
        Assert.True(HeadPose.PoseOf(5063L, out _, out _));
        HeadPose.ForgetHistory();
        Assert.False(HeadPose.PoseOf(5063L, out _, out _));
    }

    // RA01: the scope lens panel stands on the line from the eye to the eyepiece, at the eyepiece's distance. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Scope_lens_panel_stands_on_the_eye_line()
    {
        Xr.Posef picture = Pose(new Vector3(0, 1.5f, -1f), Quaternion.Identity);
        Xr.Posef straight = ScopeLens.Placed(picture, 1f, new ScopeLens.Cutout { LensDepth = 0.3f });
        Kit.Near(new Vector3(0, 1.5f, -1f + 0.7f), V(straight.Position), 1e-5f, "eyepiece straight ahead, 30 cm from the eye");
        Xr.Posef side = ScopeLens.Placed(picture, 1f, new ScopeLens.Cutout { LensRight = 0.1f, LensDepth = 0.5f });
        Vector3 eyeAt = new(0, 1.5f, 0), dir = Vector3.Normalize(new Vector3(0.1f, 0, -1f));
        Kit.Near(eyeAt + dir * 0.5f, V(side.Position), 1e-5f, "eyepiece to the right");
    }

    // RA01: every copy of NameCode in the mod (one per file that needs it) gives the same codes, and they are the game's (FNV-1a 64), checked against values
    // worked out separately. A slip in one copy would make that file stop finding its bones or classes. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Every_name_code_copy_agrees_with_the_game()
    {
        var copies = typeof(Kit).Assembly.GetTypes().Where(t => t.Namespace != "RA01.Tests")
            .Select(t => t.GetMethod("NameCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string) }))
            .OfType<MethodInfo>().Where(m => m.ReturnType == typeof(ulong)).ToArray();   // RA01: OfType drops the empty look-ups (and tells the compiler so)
        Assert.True(copies.Length >= 6, $"only {copies.Length} NameCode copies found");
        (string name, ulong code)[] known =
        {
            ("Root", 0xca7cfe2bef51b2a5), ("RightHand", 0x72c5e9c494e11400), ("fx_muzzle", 0x9cca7fbebf31f119),
            ("PlayerPuppet", 0xcebecae898e55b86), ("inkWidget", 0x2f7dae78acd4a767), ("r_nanoWire01_jnt", 0x2c91dea7b724e5fd),
        };
        foreach (MethodInfo m in copies)
            foreach ((string name, ulong code) in known)
                Assert.True((ulong)m.Invoke(null, new object[] { name })! == code, $"{m.DeclaringType!.Name}.NameCode(\"{name}\") is not the game's code 0x{code:x}");
    }

    // RA01: the game's database ids (TweakDbId): CRC-32 of the name with its length on top, checked against values worked out separately.
    // Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData("WeaponEvolution.Smart", 0x157fb82636UL)]
    [InlineData("Items.Preset_Yukimura_Default", 0x1d4d62e81dUL)]
    [InlineData("Ammo.RifleAmmo", 0xe5bec7bb0UL)]
    public void Database_ids_are_the_games(string name, ulong id)
    {
        Assert.Equal(id, (ulong)Kit.Call(Kit.Type("WeaponEventProbe"), "TweakDbId", name)!);
    }
}
