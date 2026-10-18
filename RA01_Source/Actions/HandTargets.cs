using System.Numerics;
using RA01.Core;

namespace RA01.Actions;

// RA01: M9-1 - where each of V's hands should be, on her own body (her model space: X right, Y forward, Z up, metres from her feet).
// One ruler for the view and the hands: the headset shows the world through the finished game camera, so each hand is placed from that same camera.
// 1. Each controller is measured from the centre of your head, in your head's own directions ("30 cm below my eyes, 35 cm in front of my face") - the
//    room gap turned by your head's turn undone. Turning or tilting your head changes these numbers exactly as much as it changes the view.
// 2. Core (CameraBodyProbe) says where that same head centre is on V's body and how it is turned there. The hand's target is that spot plus the controller
//    offset turned by that turn - like a tape measure hung off the crane cab rather than stretched from a chalk mark on the floor.
// M9-2 - the target is your WRIST, not the controller's grip point (the middle of your palm): the grip is moved back along the controller's own +Z by
// PalmToWrist, so V's wrist sits where yours does instead of a hand-length further out. TryGet also hands out the camera's spot (the centre of your head on
// V's body) so VRIK can scale reach about it, and this class runs the T-pose calibration that measures your arm span (MeasureSpan).
// M9-3 - TryGet also hands out each controller's TURN on V's body (the same camera-based placement as the position: the controller's turn against your head,
// re-expressed in the camera's axes, then turned by the camera's turn on her body), which VRIK turns her hand bones to. WidestNow is true on the frames where the
// T-pose calibration finds a new widest span - the moment VRIK captures the wrist corrections - and Tuning.ArmCalibrationCount goes up when it succeeds.
// Replaces M8's recentre-frame offsets, head-bone anchor and hand-offset sliders: nothing here is guessed or calibrated for position.
// M19-2 - SEATED IN A VEHICLE, the camera-on-body reading is measured from something that isn't V's feet (M19-1's log: your head 0.72 m above it in a car,
// 0.32 m on a bike, 1.60 m on foot), while her bones - and the steering wheel found from them - still measure from her feet: two tape measures hooked on
// different nails. So while seated, your head is hung off a nail both agree on - her own HEAD bone (VRIK's mailbox, VehicleState.TryReadGameHead) - plus
// the gap from that bone to the game's camera, measured quietly on foot (LearnEyeGap), plus however far your head has moved from where the game's camera
// sits. The hands, the spine's head spot and the steering wheel then all share one ruler.
// Reads Core's tracking snapshot and camera-on-body reading; hands the targets to Visuals (VRIK), which only bends the arms onto them.
// Cost - CPU: extremely low (two struct copies and a few quaternion turns per call, called once per V body per frame); GPU: none.
internal static class HandTargets
{
    const float MaxReach = 1.5f; // RA01: a controller further than this from the head is a tracking glitch, not an arm - that hand is left to the game's animation
    const float PalmToWrist = 0.07f;     // RA01: M9-2 - how far behind the grip point (middle of the palm, OpenXR grip pose) the wrist is, along the controller's +Z, metres
    const long MeasureWindowMs = 2000;   // RA01: M9-2 - how long the T-pose is sampled once the countdown ends
    static float widestSpan;             // RA01: M9-2 - the widest grip-to-grip span seen in the current measuring window

    static float gapX, gapY = 0.05f, gapZ = 0.09f;  // RA01: M19-2 - head bone to the game's camera on V's body (learned on foot; a typical start) - plain
                                                     // RA01: numbers, not a stored Vector3 (the ILC 8.0.31 quirk - see RA01_Known_Quirks)
    static bool gapLogged;
    const float GapLearnRate = 0.02f, MaxEyeGap = 0.3f;

    // RA01: M9-3 - true while the T-pose window is open and this frame set a new widest span (read by VRIK on the same animation thread, same frame).
    public static bool WidestNow;

    // RA01: the two targets on V's body, and whether each is usable this frame.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    // M9-2 - 'head' is the centre of your head on V's body (the camera's spot), the point reach is scaled about.
    public static bool TryGet(out Vector3 head, out Vector3 left, out bool leftOk, out Vector3 right, out bool rightOk, out Quaternion leftTurn, out Quaternion rightTurn)
    {
        head = left = right = default;
        leftTurn = rightTurn = Quaternion.Identity;
        leftOk = rightOk = false;
        if (!Controllers.TryReadFrame(out Controllers.Frame f) || !f.HeadValid) return false;
        MeasureSpan(f);
        if (!CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody camera)) return false;
        Vector3 at = Anchor(camera.Position);  // RA01: M19-2 - your head's centre on V's body (re-hung on her head bone while seated)
        head = at;
        Quaternion headTurn = ToQuat(f.Head.Orientation);
        if (!(headTurn.LengthSquared() > 0.98f && headTurn.LengthSquared() < 1.02f)) return false;
        Quaternion undoHead = Quaternion.Conjugate(Quaternion.Normalize(headTurn));
        leftOk = f.LeftValid && Target(f.Head, f.Left, undoHead, at, camera.Turn, out left, out leftTurn);
        rightOk = f.RightValid && Target(f.Head, f.Right, undoHead, at, camera.Turn, out right, out rightTurn);
        if (Tuning.LeftHanded) WeaponWeight.Apply(at, ref left, ref leftTurn, leftOk);   // RA01: M27-4 - the weapon hand follows through the weapon's weight
        else WeaponWeight.Apply(at, ref right, ref rightTurn, rightOk);
        TwoHandGrip.Adjust(at, camera.Turn, ref left, ref leftTurn, leftOk, ref right, ref rightTurn, rightOk); // RA01: M18-0/1 - the off hand and the virtual stock steer the gun
        PhysicalSteering.Update(ref left, leftOk, ref right, rightOk); // RA01: M19-1/2 - driving: your hands turn the wheel; a holding hand sits on it
        return leftOk || rightOk;
    }

    // RA01: one hand: the room gap from head to controller, turned into the head's own directions, then from OpenXR's axes (x right, y up, -z forward) to the camera's
    // (x right, y forward, z up), then placed from the camera's spot on V's body along the camera's turn there.
    // Cost - CPU: extremely low (two vector turns and a few adds); GPU: none.
    static bool Target(in Xr.Posef head, in Xr.Posef hand, Quaternion undoHead, Vector3 cameraSpot, Quaternion cameraTurn, out Vector3 target, out Quaternion turn)
    {
        Quaternion inHeadTurn = undoHead * ToQuat(hand.Orientation);                               // RA01: M9-3 - the controller's turn against your head (OpenXR axes)
        Quaternion inCameraTurn = new(inHeadTurn.X, -inHeadTurn.Z, inHeadTurn.Y, inHeadTurn.W);     // RA01: the same turn with its pin re-expressed in the camera's axes (x, -z, y)
        turn = Quaternion.Normalize(cameraTurn * inCameraTurn);
        Vector3 wrist = Vector3.Transform(new Vector3(0, 0, PalmToWrist), ToQuat(hand.Orientation)); // RA01: M9-2 - from the grip point back to the wrist, in room axes
        Vector3 gap = new(hand.Position.X + wrist.X - head.Position.X, hand.Position.Y + wrist.Y - head.Position.Y, hand.Position.Z + wrist.Z - head.Position.Z);
        Vector3 inHead = Vector3.Transform(gap, undoHead);
        Vector3 inCamera = new(inHead.X, -inHead.Z, inHead.Y);
        target = cameraSpot + Vector3.Transform(inCamera, cameraTurn);
        return inCamera.LengthSquared() < MaxReach * MaxReach; // RA01: false for NaN too
    }

    // RA01: M27-6n - a point in the ROOM (OpenXR) placed on V's body with the same tape measure as the hand targets (no wrist shift: a grip point stays the grip
    // point), and the way back - so Actions\PhysicalReload.cs can measure your off hand against the gun AS V HOLDS IT (VRIK's muzzle and magazine well on her
    // body) and hang its small ball back in the room. False without a head or camera reading. Cost - CPU: extremely low (a few quaternion turns); GPU: none.
    public static bool TryRoomToBody(in Controllers.Frame f, Vector3 room, out Vector3 body)
    {
        body = default;
        if (!f.HeadValid || !CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody camera)) return false;
        Quaternion headTurn = Quaternion.Normalize(ToQuat(f.Head.Orientation));
        Vector3 inHead = Vector3.Transform(room - new Vector3(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z), Quaternion.Conjugate(headTurn));
        body = Anchor(camera.Position) + Vector3.Transform(new Vector3(inHead.X, -inHead.Z, inHead.Y), camera.Turn);
        return float.IsFinite(body.X + body.Y + body.Z);
    }
    public static bool TryBodyToRoom(in Controllers.Frame f, Vector3 body, out Vector3 room)
    {
        room = default;
        if (!f.HeadValid || !CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody camera)) return false;
        Vector3 c = Vector3.Transform(body - Anchor(camera.Position), Quaternion.Conjugate(camera.Turn));   // RA01: camera axes (x right, y ahead, z up)
        Quaternion headTurn = Quaternion.Normalize(ToQuat(f.Head.Orientation));
        room = new Vector3(f.Head.Position.X, f.Head.Position.Y, f.Head.Position.Z) + Vector3.Transform(new Vector3(c.X, c.Z, -c.Y), headTurn); // RA01: back to OpenXR (x, z, -y)
        return float.IsFinite(room.X + room.Y + room.Z);
    }

    // RA01: M19-2 - your head's centre on V's body: on foot, the camera reading as it is (and the head-bone gap learned from it); seated in a vehicle, her head
    // bone + that gap + how far your head has moved from the game's camera (see the class comment). Cost - CPU: extremely low (a mailbox read and a few adds); GPU: none.
    static Vector3 Anchor(Vector3 camera)
    {
        bool seated = VehicleState.Mounted;
        if (!VehicleState.TryReadGameHead(out Vector3 bone)) return camera;
        Vector3 game = CameraBodyProbe.GameSpot + new Vector3(0, Tuning.CameraForwardCm / 100f, Tuning.CameraUpCm / 100f); // RA01: the game's camera (with the VRIK tab's shift)
        if (!seated) { LearnEyeGap(CameraBodyProbe.GameSpot - bone); return camera; }
        return bone + new Vector3(gapX, gapY, gapZ) + new Vector3(0, Tuning.CameraForwardCm / 100f, Tuning.CameraUpCm / 100f) + (camera - game);
    }

    // RA01: M19-2 - on foot, standing (not crouched by physical crouch): the gap from V's head bone to the game's camera, slowly averaged; a gap longer than
    // MaxEyeGap (a jump, a glitch) is ignored. Logged once. Cost - CPU: extremely low (a length and three blends); GPU: none.
    static void LearnEyeGap(Vector3 gap)
    {
        if (PhysicalCrouch.Crouched || !(gap.Length() < MaxEyeGap)) return;
        gapX += (gap.X - gapX) * GapLearnRate; gapY += (gap.Y - gapY) * GapLearnRate; gapZ += (gap.Z - gapZ) * GapLearnRate;
        if (gapLogged) return;
        gapLogged = true;
        Log.Write($"RA01 hands: V's head bone to the game camera is about ({gap.X * 100:F0} right, {gap.Y * 100:F0} forward, {gap.Z * 100:F0} up) cm - used to place your head while seated in a vehicle");
    }

    // RA01: M9-8 - your neck angle: how far your head is turned from the way V's body faces, degrees, left positive (measured by Core's CameraHook), passed on to
    // VRIK so her chest can turn part of the way with your head. False while a menu shows or there is no head.
    // Cost - CPU: extremely low (two volatile reads); GPU: none.
    public static bool TryGetNeckTurn(out float degrees)
    {
        degrees = CameraHook.ReadNeckDegrees(out bool valid);
        return valid;
    }

    // RA01: M11-1 - how far your head has leaned from where it sits when you stand straight, on V's body: metres to her right (x) and forward (y). The finished camera
    // spot (Core) minus the game's own camera spot plus the VRIK tab's camera shift - the part that is only your head moving. False with no camera reading.
    // Cost - CPU: extremely low (one ring read and a few subtractions); GPU: none.
    public static bool TryGetLean(out float right, out float forward)
    {
        right = forward = 0;
        if (!CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody camera)) return false;
        Vector3 standing = CameraBodyProbe.GameSpot + new Vector3(0, Tuning.CameraForwardCm / 100f, Tuning.CameraUpCm / 100f);
        right = camera.Position.X - standing.X;
        forward = camera.Position.Y - standing.Y;
        return float.IsFinite(right) && float.IsFinite(forward);
    }

    // RA01: M11-1c - where your head (the camera, at V's eyes) is on her body, metres (x right, y forward, z up), for VRIK's spine aim. withLean false: where it
    // would be standing straight (the game's camera spot plus the VRIK tab's camera shift), so V's back stays upright whatever your head does. False with no reading.
    // Cost - CPU: extremely low (one ring read and a few additions); GPU: none.
    public static bool TryGetHeadSpot(bool withLean, out Vector3 spot)
    {
        spot = CameraBodyProbe.GameSpot + new Vector3(0, Tuning.CameraForwardCm / 100f, Tuning.CameraUpCm / 100f);
        if (VehicleState.Mounted && VehicleState.TryReadGameHead(out Vector3 bone))      // RA01: M19-2 - seated: on her head bone's ruler (see Anchor)
            spot = bone + new Vector3(gapX, gapY, gapZ) + new Vector3(0, Tuning.CameraForwardCm / 100f, Tuning.CameraUpCm / 100f);
        if (withLean)
        {
            if (!CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody camera)) return false;
            spot = Anchor(camera.Position);
        }
        return float.IsFinite(spot.X) && float.IsFinite(spot.Y) && float.IsFinite(spot.Z);
    }

    // RA01: M9-2 - the T-pose calibration. The VRIK tab's button sets Tuning.ArmCalibrationDueMs 3 s ahead; from then, for MeasureWindowMs, the widest distance between
    // the two grip points is kept (arms straight out to the sides = your widest span, like a builder running a tape from fingertip to fingertip). At the end it is
    // checked (a human span, hands roughly level, each hand well out to its own side of the head) and saved as Tuning.UserArmSpan; VRIK turns it into the fit.
    // Cost - CPU: extremely low (a clock read and a compare per frame; a distance per frame only during the 2-second window); GPU: none.
    static void MeasureSpan(in Controllers.Frame f)
    {
        long due = Tuning.ArmCalibrationDueMs;
        if (due == 0) return;
        long now = Environment.TickCount64;
        WidestNow = false;
        if (now < due) { widestSpan = 0; return; }
        if (now < due + MeasureWindowMs)
        {
            if (!f.LeftValid || !f.RightValid) return;
            Quaternion undoHead = Quaternion.Conjugate(Quaternion.Normalize(ToQuat(f.Head.Orientation)));
            Vector3 l = Vector3.Transform(new Vector3(f.Left.Position.X - f.Head.Position.X, f.Left.Position.Y - f.Head.Position.Y, f.Left.Position.Z - f.Head.Position.Z), undoHead);
            Vector3 r = Vector3.Transform(new Vector3(f.Right.Position.X - f.Head.Position.X, f.Right.Position.Y - f.Head.Position.Y, f.Right.Position.Z - f.Head.Position.Z), undoHead);
            bool tPose = l.X < -0.45f && r.X > 0.45f && MathF.Abs(l.Y - r.Y) < 0.3f; // RA01: each hand well out to its own side, roughly level
            float span = Vector3.Distance(l, r);
            if (tPose && span > widestSpan) { widestSpan = span; WidestNow = true; }
            return;
        }
        Tuning.ArmCalibrationDueMs = 0;
        if (widestSpan >= 1.0f && widestSpan <= 2.6f)
        {
            Tuning.UserArmSpan = widestSpan;
            Tuning.ArmCalibrationResult = $"done - your span {widestSpan:F2} m";
            Tuning.ArmCalibrationCount++; // RA01: M9-3 - tells VRIK to commit the arm size and wrist turns it captured at the widest moment
        }
        else Tuning.ArmCalibrationResult = widestSpan == 0 ? "failed - no T-pose seen (both arms straight out to the SIDES, level); press again" : $"failed - span {widestSpan:F2} m isn't a human arm span; press again";
        Log.Write($"RA01 T-pose calibration: {Tuning.ArmCalibrationResult}");
        Banner.Show(Tuning.ArmCalibrationResult.StartsWith("done", StringComparison.Ordinal) ? $"ARMS CALIBRATED: SPAN {widestSpan:F2} M" : "ARM CALIBRATION FAILED: T-POSE AND TRY AGAIN"); // RA01: M33-2 - the answer to your button
    }

    // RA01: Cost - CPU: extremely low (four copies); GPU: none.
    static Quaternion ToQuat(in Xr.Quaternionf q) => new(q.X, q.Y, q.Z, q.W);
}
