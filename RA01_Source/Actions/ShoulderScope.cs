using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M17-1 - SHOULDER TO AIM: raise a gun to your cheek like a soldier and V aims by herself; lower it and she stops. Your own LT still works as always.
// M35-2 (Rob 21:51): now that Core\GameData\AimZoomData.cs takes the zoom out of every gun and scope, shouldering simply HOLDS LT for you (ControllerMapping.HoldAim) -
// the game's own aim, so every scope opens exactly as with a real LT (the old part-flipping, zzz_Retired\ScopeSwitch.cs.txt, left sniper lenses solid and is gone).
// (M15-3 did this once before - removed in M16-E only because LT zoomed then.) Shouldered = all of these, measured from your eyes
// along where you look (a builder's tape from the nose):
//  - the gun hand (right, or left with Tuning.LeftHanded) is in the cheek box: FrontMin-FrontMax in front of your eyes, DownMax below to UpMax above them,
//    no more than SideMax to either side;
//  - the gun points where you look: its barrel (the muzzle on V's body, as VRIK placed it in your hand) within MaxAngleDeg of your view;
//  - for EngageMs (a gun swinging past your face doesn't count). It closes once the pose has been gone ReleaseMs (no flicker at the edges).
// Only with a gun in V's hands, Physical aiming on and the Weapons tab's "Shoulder to aim" on; never while a menu or the F10 window is open.
// M80 - never while V DRIVES (empty-handed or with a gun drawn - VehicleState IsDriving / IsDriverCombat; Rob 2026-10-14): the game still reports her
// pistol in hand at the wheel, and your hands on the wheel in front of your face read as "shouldered" - the held LT is the car's brake/reverse, so it backed
// up. Passenger seats are unchanged, and your own LT always works.
// Runs on MeleeActions' thread, every look (about 90 a second).
// Cost - CPU: extremely low (a few vector sums, dot products and one angle per look); GPU: none.
internal static class ShoulderScope
{
    const float FrontMin = 0.10f, FrontMax = 0.45f, DownMax = 0.30f, UpMax = 0.05f, SideMax = 0.25f, MaxAngleDeg = 20f;
    const long EngageMs = 120, ReleaseMs = 200;
    static long poseSince, poseGoneSince;
    static bool open;

    // RA01: one look: is the gun shouldered? Asks the scope switch to open or close as the class comment says. Logs each change.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    public static void Update(WeaponProbe.Kind kind)
    {
        long now = Environment.TickCount64;
        bool allowed = Tuning.ShoulderScope && Tuning.PhysicalAiming && kind == WeaponProbe.Kind.Gun && !SettingsMenu.Open && !MenuSense.ShowsMenu
                       && !VehicleState.IsDriving && !VehicleState.IsDriverCombat; // RA01: M80 - never while driving
        bool pose = allowed && Shouldered();
        if (pose) { poseGoneSince = 0; if (poseSince == 0) poseSince = now; }
        else { poseSince = 0; if (poseGoneSince == 0) poseGoneSince = now; }
        if (!open && pose && now - poseSince >= EngageMs) Set(true, "gun shouldered");
        else if (open && (!allowed || (!pose && now - poseGoneSince >= ReleaseMs))) Set(false, allowed ? "gun lowered" : "no gun, switch off or driving (M80)");
    }

    // RA01: the pose test (see the class comment). False if tracking or the muzzle isn't available.
    // Cost - CPU: extremely low (a few turns and dot products); GPU: none.
    static bool Shouldered()
    {
        if (!Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid || !(Tuning.LeftHanded ? f.LeftValid : f.RightValid)) return false;
        Quaternion head = Quaternion.Normalize(new Quaternion(f.Head.Orientation.X, f.Head.Orientation.Y, f.Head.Orientation.Z, f.Head.Orientation.W));
        Xr.Posef handPose = Tuning.LeftHanded ? f.Left : f.Right;
        Vector3 hand = new Vector3(handPose.Position.X, handPose.Position.Y, handPose.Position.Z) - new Vector3(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z);
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, head), up = Vector3.Transform(Vector3.UnitY, head), right = Vector3.Transform(Vector3.UnitX, head); // RA01: OpenXR looks down -Z
        float front = Vector3.Dot(hand, forward), lift = Vector3.Dot(hand, up), across = Vector3.Dot(hand, right);
        if (front < FrontMin || front > FrontMax || lift < -DownMax || lift > UpMax || MathF.Abs(across) > SideMax) return false;
        if (!WeaponProbe.TryReadMuzzleOnBody(out _, out Quaternion muzzleTurn) || !CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody view)) return false;
        Vector3 barrel = Vector3.Transform(Vector3.UnitY, muzzleTurn), look = Vector3.Transform(Vector3.UnitY, view.Turn); // RA01: both on V's body: +Y forward
        return Vector3.Dot(barrel, look) >= MathF.Cos(MaxAngleDeg * MathF.PI / 180f);
    }

    // RA01: holds LT for the game (on) or lets it go (off); ControllerMapping logs why.
    // Cost - CPU: extremely low (a store and a log line on a change); GPU: none.
    static void Set(bool on, string why)
    {
        open = on;
        ControllerMapping.HoldAim(on, $"shoulder to aim - {why}");
    }
}
