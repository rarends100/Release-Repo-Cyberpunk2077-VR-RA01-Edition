using System.Diagnostics;

namespace RA01.Core;

// RA01: M4b-2a - the player in the room. Remembers the recentre point (where the head was and which way it faced at the last recentre)
// and the floor, both measured in the headset's floor-based tracking space, and holds the maths that goes with them.
// M4b-2 - it also hands the head's movement since the recentre, in the game's own axes, from the OpenXR thread to the game's camera threads.
// M4b-3 - it also remembers the head poses it handed over, so that a picture can be put where the head was when the game built it.
// M5a - the pose handed over can be one eye's (left or right in turn), and the eye is remembered with it.
// Cost - CPU: extremely low (static fields and small arrays, no allocation and no lock; the methods below are a few multiplies each, run per headset frame or per camera build); GPU: none (no GPU work is recorded here).
internal static class HeadPose
{
    public const float AssumedEyeHeight = 1.6f; // RA01: metres, only used when the runtime has no floor of its own (see FloorAssumed)
    const float LevelLimit = 0.2f;       // RA01: the head must look at least this "flat" (about 78 degrees up or down at most) for the facing direction to mean anything
    const float MaxOffset = 10f;         // RA01: metres, a head further than this from the recentre point is a tracking glitch, not the player
    const float LeashRadius = 0.6f;      // RA01: M6-4 - how far the camera may wander from the body (see Steer) before it is held at that distance instead of following further

    // RA01: everything from here to the hand-over section is used by the OpenXR thread only.
    public static bool Ready;         // RA01: true once the first recentre has happened
    public static bool FloorAssumed;  // RA01: set by XrSession when the runtime has no STAGE space, so the floor is estimated at the first recentre
    public static bool Steering = true; // RA01: whether the head steers the game's camera. The End key switches it (see XrSession).
    public static float FloorY;       // RA01: the floor's height in the tracking space, 0 when the runtime knows the floor
    public static float OriginX, OriginZ, Yaw; // RA01: the recentre point on the floor plane and the way it faced, in radians, counter-clockwise seen from above (left is positive)
    public static float EyeHeight;    // RA01: head height above the floor at the last recentre, so a seated player and a standing player both get their own height

    // RA01: which way a head points on the floor plane. False when it looks nearly straight up or down, where that direction is not defined.
    // OpenXR is right-handed with y up and -z forward, so forward on the floor is (-sin yaw, -cos yaw) and a yaw of 0 looks down -z.
    // Cost - CPU: extremely low (a dozen multiplies and one Atan2, a fraction of a microsecond, run per headset frame and on a recentre); GPU: none (pure maths).
    public static bool YawOf(in Xr.Quaternionf q, out float yaw)
    {
        float sin = 2 * (q.X * q.Z + q.W * q.Y);
        float cos = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        yaw = MathF.Atan2(sin, cos);
        return sin * sin + cos * cos > LevelLimit * LevelLimit; // RA01: also false for NaN
    }

    // RA01: makes the head's current spot and facing the new centre. False = the head looks too far up or down, nothing was changed.
    // Cost - CPU: extremely low (one YawOf and a few assignments, run only when the player recentres, plus once at the start); GPU: none (pure maths).
    public static bool Recentre(in Xr.Posef head)
    {
        if (!YawOf(head.Orientation, out float yaw)) return false;
        if (FloorAssumed && !Ready) FloorY = head.Position.Y - AssumedEyeHeight; // RA01: fixed at the first recentre, later recentres keep the same floor
        OriginX = head.Position.X;
        OriginZ = head.Position.Z;
        Yaw = yaw;
        EyeHeight = head.Position.Y - FloorY;
        Ready = true;
        return true;
    }

    // RA01: where the head is compared with the recentre point: metres forward, metres to the right, metres above the floor,
    // and degrees turned to the left (negative = right). turnedLeft is NaN while the head looks straight up or down.
    // Cost - CPU: extremely low (a Sin, a Cos, one YawOf and a few multiplies, well under a microsecond, run once per headset frame from Steer and for the status display); GPU: none (pure maths).
    public static void Relative(in Xr.Posef head, out float forward, out float right, out float height, out float turnedLeft)
    {
        float dx = head.Position.X - OriginX, dz = head.Position.Z - OriginZ;
        float sin = MathF.Sin(Yaw), cos = MathF.Cos(Yaw);
        forward = -dx * sin - dz * cos;
        right = dx * cos - dz * sin;
        height = head.Position.Y - FloorY;
        if (YawOf(head.Orientation, out float yaw))
        {
            float turn = yaw - Yaw;
            turn -= 2 * MathF.PI * MathF.Round(turn / (2 * MathF.PI)); // RA01: wrap into -180..+180 degrees
            turnedLeft = turn * (180f / MathF.PI);
        }
        else turnedLeft = float.NaN;
    }

    // RA01: a flat screen standing on the floor, 'distance' metres in front of the recentre point and turned to face it.
    // the pose is the screen's centre, so it sits half its height above the floor. A quad faces its own +z, which here points back at the player.
    // Cost - CPU: extremely low (two Sin and two Cos and a few adds, run per headset frame while the flat screen is drawn); GPU: none (pure maths, the pose is only handed to OpenXR).
    public static Xr.Posef ScreenPose(float distance, float height)
    {
        return new Xr.Posef
        {
            Orientation = { Y = MathF.Sin(Yaw / 2), W = MathF.Cos(Yaw / 2) },
            Position = { X = OriginX - distance * MathF.Sin(Yaw), Y = FloorY + height / 2, Z = OriginZ - distance * MathF.Cos(Yaw) }
        };
    }

    // RA01: ---- the hand-over to the game's camera threads (M4b-2) ----

    // RA01: how the head has moved since the recentre, in the GAME's axes: x right, y forward, z up (OpenXR has x right, y up, -z forward).
    // the rotation is the head's turn away from "looking straight ahead as at the recentre" (a level head facing the recentre direction is no turn at all).
    // the offset is in metres, measured in the recentre's own frame (forward means the way the head faced at the recentre).
    // Cost - CPU: extremely low (a 48-byte plain struct with no methods, copied by value once per headset frame by the writer and once per camera build by each reader); GPU: none (plain data).
    public struct Sample
    {
        public float Qx, Qy, Qz, Qw;      // RA01: the head's rotation as a quaternion, game axes
        public float TurnSin, TurnCos;    // RA01: sine and cosine of half the head's turn about the vertical (its "twist" about game Z), see Steer
        public float Right, Forward, Up;  // RA01: the head's offset from the recentre point, game axes, metres
        public long Time;                 // RA01: Stopwatch ticks when it was published
        public float EyeX, EyeY, EyeZ;    // RA01: M9-1 - where the eye this pose was moved to sits in the head's own axes (OpenXR: x right, y up, -z forward), metres; 0 = the head's centre (stereo off)
        public bool Valid;                // RA01: false = no usable head right now (steering off, no recentre yet, or the head pose was not sane)
    }

    // RA01: a ring of 4 slots. The OpenXR thread fills the next slot and only then counts it as published, so a slot being written is never
    // RA01: the one readers look at. A reader copies the newest slot and checks that the writer has not come round to that slot again in the
    // RA01: meantime (it would need to publish 3 more samples, about 33 ms, while the reader copies one sample). No locks, no waiting, no allocation.
    static readonly Sample[] ring = new Sample[4];
    static int published;
    static readonly long MaxAge = Stopwatch.Frequency / 10; // RA01: a sample older than 100 ms is not used: the headset is off or the OpenXR thread has stopped

    // RA01: OpenXR thread, once per rendered frame. 'usable' says the head was located. Without a recentre or with steering off it publishes "no head" at once.
    // a head that could not be located while steering is running is a tracking hiccup: nothing is published, the last sample stays and goes out of date
    // by itself after 100 ms, so a dropout of a frame or two does not snap the view to the game's own camera and back.
    // 'eye' says whose pose 'head' is: 0 = the head itself, 1 = the left eye, 2 = the right eye (the same numbers as the OpenXR eye visibility).
    // 'placed' (M5a-2) is where a picture built from this pose will STAND in the headset: the same as 'head', except when the game's camera is moved to an eye spacing (IPD) that is not
    // the headset's own: then 'head' is the moved eye the game gets, and 'placed' is the eye where the headset really has it, so the world shows at the right distance.
    // Cost - CPU: extremely low (one Steer, one Stopwatch read, one 48-byte and one 28-byte copy and a volatile write, a few hundred nanoseconds, no lock, run once per headset frame); GPU: none (pure maths and memory).
    // M9-1 - 'eyeLocal' is the eye offset 'head' was moved by (see Sample.EyeX), so a reader can find the head's centre again.
    public static void Publish(bool usable, in Xr.Posef head, int eye, in Xr.Posef placed, in Xr.Vector3f eyeLocal)
    {
        if (!usable && Ready && Steering) return;
        Sample s = default;
        if (Ready && Steering) s = Steer(head); // RA01: 'usable' is true here, the hiccup case has left above
        s.Time = Stopwatch.GetTimestamp();
        s.EyeX = eyeLocal.X; s.EyeY = eyeLocal.Y; s.EyeZ = eyeLocal.Z;
        if (s.Valid) Remember(s.Time, placed, eye);
        int next = published + 1;
        ring[next & 3] = s;
        Volatile.Write(ref published, next);
    }

    // RA01: the short form of Publish for when the picture stands exactly where the head pose given is (no eye spacing change), so 'placed' is the same pose as 'head'.
    // Cost - CPU: extremely low (it only calls the full Publish, so the cost is that one, run once per headset frame); GPU: none (pure maths and memory).
    public static void Publish(bool usable, in Xr.Posef head, int eye = 0) => Publish(usable, head, eye, head, default);

    // RA01: ---- the picture's pose tag (M4b-3), OpenXR thread only ----

    // RA01: the poses pictures are placed at (Publish's 'placed'), newest last, with the Stopwatch time of each. The game builds a camera from the newest one at that moment, and CameraHook notes that pose's
    // RA01: time (see CameraHook.StampOfPresent), so a picture's pose is found by its stamp (PoseOf). 64 poses are about 0.7 s at 90 Hz, far more than the pipeline needs.
    const int HistorySize = 64;
    static readonly long[] historyTime = new long[HistorySize];
    static readonly Xr.Posef[] historyPose = new Xr.Posef[HistorySize];
    static readonly int[] historyEye = new int[HistorySize];
    static int historyNext, historyCount;   // RA01: the slot written next, and how many slots hold a pose (up to HistorySize)

    // RA01: writes one pose, its time and its eye into the next slot of the history ring and moves on to the following slot, forgetting the oldest pose once the ring is full.
    // Cost - CPU: extremely low (three array stores and a modulo, tens of nanoseconds, run once per headset frame from Publish); GPU: none (memory only).
    static void Remember(long time, in Xr.Posef head, int eye)
    {
        historyTime[historyNext] = time;
        historyPose[historyNext] = head;
        historyEye[historyNext] = eye;
        historyNext = (historyNext + 1) % HistorySize;
        if (historyCount < HistorySize) historyCount++;
    }

    // RA01: the remembered poses are in the floor space; when the runtime moves that space they mean nothing any more.
    // Cost - CPU: extremely low (one integer store, run only when the player recentres or the runtime moves the space); GPU: none (memory only).
    public static void ForgetHistory() => historyCount = 0;

    // RA01: M5a-2 - the pose that was published at exactly 'stamp' (the Stopwatch time CameraHook noted when the game built a camera from it), in the floor space, and whose pose it was
    // (0 head, 1 left eye, 2 right eye). False when it is not remembered: the stamp is 0 (no pose; the clock never reads 0), the history was cleared by a recentre, or the pose is
    // more than 64 poses (0.7 s) old. There is no "nearest pose": a picture is shown at the pose it was really built from, or (when that is gone) not tied to the room at all.
    // Cost - CPU: extremely low (a search over at most 64 stored times, newest first, so usually a hit within a few steps and never more than about a microsecond, run once per picture shown); GPU: none (memory only).
    public static bool PoseOf(long stamp, out Xr.Posef pose, out int eye)
    {
        for (int back = 1; back <= historyCount; back++)
        {
            int slot = (historyNext - back + HistorySize) % HistorySize;
            if (historyTime[slot] != stamp) continue;
            pose = historyPose[slot];
            eye = historyEye[slot];
            return true;
        }
        pose = default;
        eye = 0;
        return false;
    }

    // RA01: the pose of one eye: the head's orientation, and the head's position moved by the eye's offset ('local', measured in the head's own axes) turned by that orientation.
    // the turn is v + 2w(u x v) + 2u x (u x v) for the quaternion (u, w). The tilt of the headset's screens (their cant) is not part of it yet.
    // Cost - CPU: extremely low (about thirty multiplies and adds, run once or twice per headset frame); GPU: none (pure maths).
    public static Xr.Posef EyePose(in Xr.Posef head, in Xr.Vector3f local)
    {
        Xr.Quaternionf q = head.Orientation;
        float cx = q.Y * local.Z - q.Z * local.Y, cy = q.Z * local.X - q.X * local.Z, cz = q.X * local.Y - q.Y * local.X; // RA01: u x v
        float dx = q.Y * cz - q.Z * cy, dy = q.Z * cx - q.X * cz, dz = q.X * cy - q.Y * cx; // RA01: u x (u x v)
        return new Xr.Posef
        {
            Orientation = q,
            Position = { X = head.Position.X + local.X + 2 * (q.W * cx + dx), Y = head.Position.Y + local.Y + 2 * (q.W * cy + dy), Z = head.Position.Z + local.Z + 2 * (q.W * cz + dz) }
        };
    }

    // RA01: where the flat picture stands for a remembered head pose: 'distance' metres straight ahead of that head, turned the same way as it, so the picture
    // faces the eyes it was built for. The forward vector (0, 0, -distance) is turned by the head's quaternion (x, y, z, w) with the usual formula.
    // Cost - CPU: extremely low (about twenty multiplies and adds, run per headset frame for each picture shown); GPU: none (pure maths).
    public static Xr.Posef TaggedScreenPose(in Xr.Posef tag, float distance)
    {
        Xr.Quaternionf q = tag.Orientation;
        return new Xr.Posef
        {
            Orientation = q,
            Position =
            {
                X = tag.Position.X - 2 * distance * (q.X * q.Z + q.W * q.Y),
                Y = tag.Position.Y + 2 * distance * (q.W * q.X - q.Y * q.Z),
                Z = tag.Position.Z - distance * (1 - 2 * (q.X * q.X + q.Y * q.Y))
            }
        };
    }

    // RA01: M5a-5 - a picture moved in the view of the eye that looks at it: 'right' and 'up' are angles in radians. The picture is turned about the point 'distance' straight in front of its face (where the eye stands),
    // so it slides across the view and still faces the eye (a plain slide in its own plane would leave it turned away from the eye, which looks tilted). The turn is q = yaw (about the picture's up axis) after
    // pitch (about its right axis), so the horizon does not roll. The picture's centre goes from the origin of its own axes to C - q*C, where C = (0, 0, distance) is the eye, and is then carried into the room by the pose.
    // Cost - CPU: extremely low (four Sin and Cos, one Turn and one Multiply, well under a microsecond, run per headset frame for each picture shown); GPU: none (pure maths).
    public static Xr.Posef Orbited(in Xr.Posef pose, float distance, float right, float up)
    {
        float sy = MathF.Sin(-right / 2), cy = MathF.Cos(-right / 2), sx = MathF.Sin(up / 2), cx = MathF.Cos(up / 2);
        Xr.Quaternionf q = new() { X = cy * sx, Y = sy * cx, Z = -sy * sx, W = cy * cx };
        Xr.Vector3f local = new() { X = -2 * distance * (q.X * q.Z + q.W * q.Y), Y = -2 * distance * (q.Y * q.Z - q.W * q.X), Z = 2 * distance * (q.X * q.X + q.Y * q.Y) };
        Xr.Vector3f world = Turn(pose.Orientation, local);
        Xr.Posef moved = pose;
        moved.Orientation = Multiply(pose.Orientation, q);
        moved.Position.X += world.X;
        moved.Position.Y += world.Y;
        moved.Position.Z += world.Z;
        return moved;
    }

    // RA01: a vector turned by a quaternion: v + w*t + o x t, with o the quaternion's x, y, z and t = 2 * (o x v).
    // Cost - CPU: extremely low (about thirty multiplies and adds, run per headset frame from Orbited); GPU: none (pure maths).
    internal static Xr.Vector3f Turn(in Xr.Quaternionf q, in Xr.Vector3f v)
    {
        float tx = 2 * (q.Y * v.Z - q.Z * v.Y), ty = 2 * (q.Z * v.X - q.X * v.Z), tz = 2 * (q.X * v.Y - q.Y * v.X);
        return new Xr.Vector3f { X = v.X + q.W * tx + (q.Y * tz - q.Z * ty), Y = v.Y + q.W * ty + (q.Z * tx - q.X * tz), Z = v.Z + q.W * tz + (q.X * ty - q.Y * tx) };
    }

    // RA01: the turn 'b' followed by the turn 'a' (the usual quaternion product a * b, b applied first in a's own axes).
    // Cost - CPU: extremely low (16 multiplies and 12 adds, run per headset frame from Orbited); GPU: none (pure maths).
    static Xr.Quaternionf Multiply(in Xr.Quaternionf a, in Xr.Quaternionf b) => new()
    {
        W = a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        X = a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        Y = a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        Z = a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W
    };

    // RA01: head rotation relative to the recentre: the recentre's yaw is taken off with a quaternion (0, sin, 0, cos) of half the yaw, then the axes are renamed.
    // the head's turn about the vertical is also split out (its "twist" about game Z: the z and w parts of the quaternion, made unit length). CameraHook applies that
    // turn about the world's vertical and the rest (nod and tilt) in the camera's own axes, so the horizon never rolls when the mouse has the camera pitched.
    // the twist stays smooth when the head looks straight up or down, and is only undefined for a head turned upside down (then it is no turn).
    // Cost - CPU: extremely low (a Sin, a Cos, a Sqrt, one Relative and some thirty multiplies, about a microsecond, run once per headset frame from Publish); GPU: none (pure maths).
    static Sample Steer(in Xr.Posef head)
    {
        float s = MathF.Sin(Yaw / 2), c = MathF.Cos(Yaw / 2);
        Xr.Quaternionf b = head.Orientation;
        float x = c * b.X - s * b.Z, y = c * b.Y - s * b.W, z = c * b.Z + s * b.X, w = c * b.W + s * b.Y; // RA01: conjugate of the yaw turn, times the head's rotation
        Relative(head, out float forward, out float right, out float height, out _);

        // RA01: M6-4 - the camera is tied to the body by a leash: within LeashRadius metres of the recentre point (where you last stood), it follows the head exactly - full lean,
        // RA01: crouch, sidestep comfort. Past that it is held at the leash's own length in whatever direction you're pulling, instead of drifting further from where the game's
        // RA01: character actually stands (nothing but a stick press ever moves that). Comparing offset*offset against radius*radius is the same check as "is this point inside
        // RA01: this circle" - it skips a Sqrt for the common case (already inside the leash), only paying for one on the rarer step of pulling past it.
        // RA01: M6-11 - Dev Only Tools' "Deactivate leash" row skips this clamp outright, so a bone hunt's forward lean is free to travel as far as the head really goes.
        float leashedForward = forward, leashedRight = right;
        float offsetSquared = forward * forward + right * right;
        if (!Tuning.LeashDisabled && offsetSquared > LeashRadius * LeashRadius)
        {
            float scale = LeashRadius / MathF.Sqrt(offsetSquared);
            leashedForward *= scale;
            leashedRight *= scale;
        }

        Sample sample = new() { Qx = x, Qy = -z, Qz = y, Qw = w, Right = leashedRight, Forward = leashedForward, Up = height - EyeHeight, Valid = true };
        float twist = MathF.Sqrt(y * y + w * w); // RA01: game z is OpenXR y
        if (twist > 0.001f) { sample.TurnSin = y / twist; sample.TurnCos = w / twist; }
        else sample.TurnCos = 1;

        float lengthSquared = x * x + y * y + z * z + w * w;
        bool sane = lengthSquared > 0.98f && lengthSquared < 1.02f && MathF.Abs(right) < MaxOffset && MathF.Abs(forward) < MaxOffset && MathF.Abs(sample.Up) < MaxOffset; // RA01: false for NaN too - checked against the RAW offset, before the leash, so a real tracking glitch past MaxOffset is still caught even though the leash would otherwise have shrunk it down to a small, "sane-looking" number
        return sane ? sample : default;
    }

    // RA01: game camera threads. True = 'sample' is a fresh, usable head pose ('now' is a Stopwatch timestamp).
    // Cost - CPU: extremely low (one 48-byte copy, a memory barrier, two volatile reads, no lock, a retry is rare; run per camera build, about 200 a second, on the game's camera thread); GPU: none (memory only).
    public static bool TryRead(long now, out Sample sample)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            int newest = Volatile.Read(ref published);
            sample = ring[newest & 3];
            Interlocked.MemoryBarrier(); // RA01: the copy above must be finished before the check below
            if (Volatile.Read(ref published) - newest < 3) // RA01: the writer has not come round to this slot again, so the copy is whole
                return sample.Valid && now - sample.Time < MaxAge;
        }
        sample = default;
        return false;
    }
}
