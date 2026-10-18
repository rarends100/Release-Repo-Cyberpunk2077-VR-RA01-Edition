using System.Numerics;
using RA01.Core;

namespace RA01.Visuals;

// RA01: M24-4 - THE LASER SIGHT (Weapons tab "Laser sight", off by default; colour, depth, thickness, brightness). Drawn by the mod, not the game: no hook,
// nothing in the game's world - Core\HandStrip.cs hangs a glowing strip on your real right controller every headset frame, and this class only says where on
// the controller it starts and which way it points: at the gun's muzzle, along its barrel.
// HOW IT FINDS THE MUZZLE ON YOUR HAND: the game says where the muzzle is and which way the barrel points (AimProbe.TryMuzzleInWorld - the same reading the
// shots follow), seen from the game's camera; your headset says where your head and right controller are. Both are measured from the head, so the muzzle
// can be written down in the controller's own frame: "8 cm forward, 3 cm up, tilted 20 degrees down". That note is refreshed about 60 times a second,
// eased (Blend) so the gun's sway and the game's small timing differences don't shake it, and HandStrip draws from the LIVE controller - so the beam moves with
// your hand at once, and it follows the gun when two-handing or the stock bends the gun in your grip. While you fire the note is held, so recoil kicks
// don't throw the beam around. A note further than MaxReach from the hand (a draw or holster animation) is not taken.
// M24-5 - THE LASER ONLY SHOWS, IT NEVER AIMS (Rob 2026-10-07 18:44): it follows the line the shots already take. A gun aimed physically (crosshair hidden -
// Actions\FireArmAiming.cs steers its shots down the barrel) gets the barrel line above. A camera-aimed gun (a Smart gun, or Physical aiming off - the crosshair
// shows and the shots go where it points) gets a beam from the muzzle to the crosshair's point 'Laser depth' ahead (XrSession.ReticleLook, from your head),
// so the dot sits on the camera's aim line and a shot at the dot hits it.
// Builder picture: marking where a drill bit sits in the chuck - measure once against a ruler you both share (your head), then the mark rides on the drill.
// Cost - CPU: extremely low (per controller read with a gun out: two mailbox reads, a few vector turns and a lock); GPU: none here (HandStrip's two panels).
internal static class LaserSight
{
    const float Blend = 0.25f;               // RA01: how much of each new reading goes into the note (the rest is the old note) - eases sway and timing jitter
    const float MaxReach = 1.0f;             // RA01: metres - a muzzle further than this from the controller isn't in your hand yet (M24-5: was 0.6 - long rifles)
    const float FirePress = 0.15f;           // RA01: the trigger past this: firing - the note is held through the recoil

    static readonly float[] note = new float[6];   // RA01: the muzzle in the controller's frame: start (x, y, z), barrel direction (x, y, z) - arrays, not Vector3 fields (the ILC quirk)
    static bool noted, shown, wasLefty;     // RA01: M28-1 - wasLefty: the gun hand last frame
    static long muzzleSeenAt;                      // RA01: M26-1 - when the game last reported a muzzle (Environment.TickCount64)
    const long MuzzleFreshMs = 250;                // RA01: M26-1 - no muzzle for this long = no gun in V's hand (holstered, or none equipped): no laser
    static long lastFrameTime;
    static string lastGun = "";

    // RA01: runs Update just before every controller read is answered. Call once at start-up, after XInputHook.
    // Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static unsafe void Install() => ControllerMapping.AddBeforeBuild(&Update);

    // RA01: one look: a gun out and the laser on? refresh the note from this frame's readings, then hold the strip on the controller. Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Update()
    {
        bool hmg = WeaponProbe.HeldName.StartsWith("Heavy machine gun", StringComparison.Ordinal);   // RA01: M27-6q - Rob 06:26: the Mk.31 HMG has no other way to aim (no sights,
        if (!(Tuning.LaserSight || hmg) || WeaponProbe.Held != WeaponProbe.Kind.Gun || MenuSense.ShowsMenu) // RA01: its crosshair off) - its laser is always on, whatever the setting
        {
            noted = false;
            if (shown) { shown = false; HandStrip.Drop(); Log.Write("RA01 laser: off (no gun, a menu, or switched off)"); }
            return;
        }
        string gun = WeaponProbe.HeldName;
        if (gun != lastGun) { lastGun = gun; noted = false; }                 // RA01: a new gun: its muzzle sits somewhere else
        long now = Environment.TickCount64;
        bool haveMuzzle = AimProbe.TryMuzzleInWorld(out Vector3 muzzle, out Quaternion turn, out CameraBodyProbe.OnBody view);
        if (haveMuzzle) muzzleSeenAt = now;
        if (now - muzzleSeenAt > MuzzleFreshMs)                                // RA01: M26-1 - Rob 2026-10-08: no laser without a weapon in V's hand - the "what V holds"
        {                                                                      // RA01: answer can trail a holster by seconds; the muzzle reading stops at once
            noted = false;
            if (shown) { shown = false; HandStrip.Drop(); Log.Write("RA01 laser: off (no gun in V's hand)"); }
            return;
        }
        bool lefty = Tuning.LeftHanded;                                        // RA01: M28-1/3 - the gun hand: 0 left, 1 right
        int gunHand = lefty ? 0 : 1;
        if (lefty != wasLefty) { wasLefty = lefty; noted = false; }            // RA01: M28-1 - the gun changed hands: learn it again
        if (haveMuzzle && ControllerMapping.FireTrigger <= FirePress && Controllers.TryReadFrame(out Controllers.Frame f) && f.HeadValid && (lefty ? f.LeftValid : f.RightValid) && f.Time != lastFrameTime)
        {
            lastFrameTime = f.Time;
            Note(f, lefty ? f.Left : f.Right, muzzle, turn, view);
        }
        if (!noted) return;
        Vector3 start = new(note[0], note[1], note[2]);
        if (HudLayer.HideCrosshair) HandStrip.Hold(gunHand, start, new Vector3(note[3], note[4], note[5]));  // RA01: physically aimed: along the barrel
        else
        {
            Vector3 look = XrSession.ReticleLook;                                // RA01: M24-5 - camera-aimed: to the crosshair's point (view axes: x right, y ahead, z up)
            HandStrip.HoldToward(gunHand, start, new Vector3(look.X, look.Z, -look.Y) * Tuning.LaserLengthM); // RA01: to OpenXR's head axes (x right, y up, -z ahead)
        }
        if (!shown) { shown = true; Log.Write($"RA01 laser: on - {gun}, muzzle {new Vector3(note[0], note[1], note[2]).Length() * 100f:F0} cm from the controller"); }
    }

    // RA01: the muzzle and barrel from the game's camera (x right, y ahead, z up) -> from your head (OpenXR: x right, y up, -z ahead) -> into the room by your
    // head's pose -> into the gun hand's controller frame (M28-1: left or right); eased into the note.
    // Cost - CPU: extremely low (six vector turns, a few adds); GPU: none.
    static void Note(in Controllers.Frame f, in Xr.Posef gunPose, Vector3 muzzle, Quaternion turn, in CameraBodyProbe.OnBody view)
    {
        Quaternion back = Quaternion.Conjugate(view.WorldTurn);
        Vector3 m = Vector3.Transform(muzzle - view.WorldHead, back), b = Vector3.Transform(Vector3.Transform(Vector3.UnitY, turn), back);
        Quaternion head = Quaternion.Normalize(new Quaternion(f.Head.Orientation.X, f.Head.Orientation.Y, f.Head.Orientation.Z, f.Head.Orientation.W));
        Quaternion hand = Quaternion.Normalize(new Quaternion(gunPose.Orientation.X, gunPose.Orientation.Y, gunPose.Orientation.Z, gunPose.Orientation.W));
        Vector3 headAt = new(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z), handAt = new(gunPose.Position.X, gunPose.Position.Y, gunPose.Position.Z);
        Vector3 room = headAt + Vector3.Transform(new Vector3(m.X, m.Z, -m.Y), head), roomAlong = Vector3.Transform(new Vector3(b.X, b.Z, -b.Y), head);
        Quaternion fromRoom = Quaternion.Conjugate(hand);
        Vector3 start = Vector3.Transform(room - handAt, fromRoom), along = Vector3.Transform(roomAlong, fromRoom);
        if (!(start.Length() < MaxReach) || !(along.LengthSquared() > 0.5f)) return;  // RA01: not in the hand yet (drawing), or a bad reading
        float k = noted ? Blend : 1f;
        for (int i = 0; i < 3; i++)
        {
            note[i] += (start[i] - note[i]) * k;
            note[i + 3] += (along[i] - note[i + 3]) * k;
        }
        Vector3 dir = Vector3.Normalize(new Vector3(note[3], note[4], note[5]));
        note[3] = dir.X; note[4] = dir.Y; note[5] = dir.Z;
        noted = true;
    }
}
