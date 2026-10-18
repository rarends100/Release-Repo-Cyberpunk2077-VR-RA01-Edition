using System.Numerics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M9-0 - READ-ONLY probe: finds, at the same instant, the game camera's world position/turn and V's body root's world position/turn, and from the pair works out
// where the camera sits ON V's body (her own model space). That spot is the fixed point VRIK needs; until now it was guessed, which is why the hands never lined up.
// How, without guessing:
// 1. CameraHook calls Observe at the moment the game has just copied the camera object's world position into its setup block. The camera object's address is in
//    one of the CPU registers right then, but we don't know which, so the trampoline hands over a block of them (see CameraHook's TrampolineHead). Each candidate
//    (and each candidate minus a few small header sizes) is tested: does the object hold, at +0xE0, EXACTLY the position numbers the game just copied? Only the real
//    camera object passes - like finding the right key by trying each one in the lock.
// 2. From the camera object, a background search follows its pointers (one or two steps) looking for an object whose world position is 1.0-2.2 m straight below the
//    camera (within 0.6 m sideways) and whose turn is upright (a person standing: only left/right turn, no tilt). That is V's body root, at her feet.
// 3. From then on, every camera build: camera on V's body = her turn undone, applied to (camera - root). Logged once a second. If the pairing is right, that number
//    stays nearly constant while you walk, turn and jump. Nothing is ever written to the game; every read goes through ReadProcessMemory (a bad pointer fails one read).
// M9-1 - now permanent: Observe also hands back V's root for this build, and after the head has been added on, Publish measures the FINISHED camera (the one the
// headset shows) on V's body - with the eye offset taken back out, so it is always the centre of your head - and publishes it for Actions (HandTargets).
// M9-3 - also moves the game's camera on V's body by Tuning.CameraForwardCm / CameraUpCm (ShiftCamera, before the head is added), and keeps the game's own
// camera spot on her body (GameSpot) so VRIK can work out how far it is from her eyes.
// World positions are whole numbers of 1/131072 m (x, y, z as three 4-byte ints at +0xE0), turns are quaternions (x, y, z, w floats at +0xF0), game axes (Z up).
// Cost - CPU: low while searching (a few dozen tiny safe reads per camera build for a few builds, plus one background search of a few thousand reads, a few ms,
// once per load), then low (about four safe reads per camera build, ~200 builds a second, well under a millisecond a second in total); GPU: none.
internal static unsafe class CameraBodyProbe
{
    const int PosAt = 0xE0, QuatAt = 0xF0;       // RA01: where a placed object keeps its world position and turn
    const float Scale = 131072f;
    static readonly string[] RegisterNames = { "rbx", "rsi", "rdi", "r12", "r13", "r14", "r15", "rbp", "r11", "r10", "r9", "r8", "rdx", "rcx", "rax" };
    static readonly int[] HeaderSizes = { 0, 0x100, 0x110, 0x120 }; // RA01: a register may point at the camera object itself or at an interface inside it
    const int OwnerScanBytes = 0x200;            // RA01: how much of each object the owner search looks through for pointers
    const long ReportEveryMs = 1000;

    static readonly nint Self = GetCurrentProcess();
    static int cameraRegister = -1, cameraHeader;      // RA01: which register (index into RegisterNames) and header size gave the camera object
    static int ownerFirst = -1, ownerSecond = -1;       // RA01: path from the camera object to the root: [camera + ownerFirst], then (if >= 0) [that + ownerSecond]
    static int ownerSearchRunning;
    static int findAttempts;                           // RA01: camera builds tried without a register matching - logged once at 300 so a miss is visible
    static int misses;                                  // RA01: builds in a row where the known path stopped matching - past a few, search again
    static long nextReportMs;
    static float gameSpotX, gameSpotY, gameSpotZ;
    public static volatile bool HoldStandingHeight; // RA01: M11-2 - set while V is crouched by physical crouch: the game's crouch drop is added back (ShiftCamera)
    static float standingSpotZ;              // RA01: M11-2 - the highest the game's camera has sat on V's body while not held - her standing camera height

    // RA01: M9-3 - where the game's OWN camera sits on V's body (before the camera shift and before your head is added), metres, her axes.
    // RA01: Cost - CPU: extremely low (three reads); GPU: none.
    public static Vector3 GameSpot => new(gameSpotX, gameSpotY, gameSpotZ);

    // RA01: M9-1 - V's root for one camera build, handed from Observe to Publish (a plain value, so two camera threads can never mix theirs).
    // Cost - CPU: extremely low (a 28-byte struct); GPU: none.
    public struct Root { public int X, Y, Z; public Quaternion Turn; public bool Valid; }

    // RA01: M9-1 - the finished camera measured on V's body: its spot (metres, V's own axes: X right, Y forward, Z up, from her feet) and its turn relative to her body.
    // The camera's own axes are X right, Y forward (the way it looks), Z up.
    // M13-47 - also the same camera in the game's WORLD: your head's centre (metres) and the turn of the view you see, for AimProbe's aim line.
    // Cost - CPU: extremely low (a 64-byte struct); GPU: none.
    public struct OnBody { public Vector3 Position; public Quaternion Turn; public Vector3 WorldHead; public Quaternion WorldTurn; public long Time; }

    // RA01: M9-1 - the same lock-free 4-slot ring HeadPose and Controllers use: the camera threads fill the next slot and only then count it published.
    static readonly OnBody[] ring = new OnBody[4];
    static int published;
    static readonly object writeGate = new();
    static readonly long MaxAge = System.Diagnostics.Stopwatch.Frequency / 10; // RA01: older than 100 ms = no camera is being built (menu, loading)

    // RA01: called by CameraHook.OnCamera for the player's camera, BEFORE the head is added on (so the setup block still holds the game's own camera).
    // 'regs' is the register block the trampoline saved (see the class comment).
    // Cost - CPU: low (see the class comment); GPU: none.
    public static void Observe(int* setupPos, float* setupQuat, nint regs, out Root body)
    {
        body = default;
        try
        {
            if (regs == 0) return;
            if (cameraRegister < 0) { FindCamera(setupPos, setupQuat, regs); return; }

            nint camera = Register(regs, cameraRegister) - cameraHeader;
            if (!ReadPlaced(camera, out int cx, out int cy, out int cz, out _) || cx != setupPos[0] || cy != setupPos[1] || cz != setupPos[2])
            {
                if (++misses > 30) { Log.Write("RA01 camera-body probe: the camera register stopped matching - searching again"); cameraRegister = -1; ownerFirst = -1; misses = 0; }
                return;
            }
            misses = 0;

            if (ownerFirst < 0)
            {
                if (Interlocked.CompareExchange(ref ownerSearchRunning, 1, 0) == 0)
                {
                    int ix = setupPos[0], iy = setupPos[1], iz = setupPos[2];
                    Task.Run(() => FindOwner(camera, ix, iy, iz));
                }
                return;
            }

            nint root = FollowOwner(camera);
            if (root == 0 || !ReadPlaced(root, out int rx, out int ry, out int rz, out Quaternion rootTurn)) return;
            float lengthSquared = rootTurn.LengthSquared();
            if (!(lengthSquared > 0.98f && lengthSquared < 1.02f)) return;
            body = new Root { X = rx, Y = ry, Z = rz, Turn = Quaternion.Normalize(rootTurn), Valid = true };
            Vector3 spot = Vector3.Transform(new Vector3((setupPos[0] - rx) / Scale, (setupPos[1] - ry) / Scale, (setupPos[2] - rz) / Scale), Quaternion.Conjugate(body.Turn));
            gameSpotX = spot.X; gameSpotY = spot.Y; gameSpotZ = spot.Z; // RA01: M9-3 - three plain floats; a torn read only ever mixes two nearly identical readings
            Report(setupPos, setupQuat, rx, ry, rz, rootTurn);
        }
        catch { } // RA01: must never reach the game
    }

    // RA01: step 1 - tries every saved register (and a few header sizes below it) until one points at an object holding exactly the position the game just copied.
    // Cost - CPU: low (15 registers x 4 header sizes, one 12-byte safe read each, until found); GPU: none.
    static void FindCamera(int* setupPos, float* setupQuat, nint regs)
    {
        for (int r = 0; r < RegisterNames.Length; r++)
        {
            nint value = Register(regs, r);
            foreach (int header in HeaderSizes)
            {
                nint candidate = value - header;
                if (!LooksLikePointer(candidate)) continue;
                if (!ReadPlaced(candidate, out int x, out int y, out int z, out Quaternion q)) continue;
                if (x != setupPos[0] || y != setupPos[1] || z != setupPos[2]) continue;
                if (MathF.Abs(q.X - setupQuat[0]) > 1e-3f || MathF.Abs(q.W - setupQuat[3]) > 1e-3f) continue;
                cameraRegister = r;
                cameraHeader = header;
                Log.Write($"RA01 camera-body probe: camera object found - register {RegisterNames[r]} minus 0x{header:X} = 0x{candidate:X} holds exactly the position the game just copied");
                return;
            }
        }
        if (++findAttempts == 300) Log.Write("RA01 camera-body probe: after 300 camera builds no saved register points at an object holding the copied position - the camera object isn't in the block this hook sees");
    }

    // RA01: step 2 (background thread) - looks through the camera object's pointers, and the pointers inside those, for V's body root: an upright object 1.0-2.2 m below
    // the camera and within 0.6 m of it sideways. The first one found wins; the path to it is remembered as two offsets.
    // Cost - CPU: low, once per load (up to 64 x 64 small safe reads, a few ms, off the game's threads); GPU: none.
    static void FindOwner(nint camera, int camX, int camY, int camZ)
    {
        try
        {
            byte* first = stackalloc byte[OwnerScanBytes];
            byte* second = stackalloc byte[OwnerScanBytes];
            if (!Read(camera, first, OwnerScanBytes)) return;
            for (int a = 0; a < OwnerScanBytes; a += 8)
            {
                nint p = *(nint*)(first + a);
                if (!LooksLikePointer(p) || p == camera) continue;
                if (IsRoot(p, camX, camY, camZ, out string why)) { Found(a, -1, p, why); return; }
                if (!Read(p, second, OwnerScanBytes)) continue;
                for (int b = 0; b < OwnerScanBytes; b += 8)
                {
                    nint q = *(nint*)(second + b);
                    if (!LooksLikePointer(q) || q == camera || q == p) continue;
                    if (IsRoot(q, camX, camY, camZ, out why)) { Found(a, b, q, why); return; }
                }
            }
            Log.Write("RA01 camera-body probe: NO body root found within two pointer steps of the camera object - will try again on the next load");
            ownerFirst = int.MaxValue; // RA01: stop searching this load (Observe treats any value >= 0 as "done"); FollowOwner returns 0 for it
        }
        catch (Exception e) { Log.Write($"RA01 camera-body probe: owner search failed - {e.Message}"); }
        finally { Interlocked.Exchange(ref ownerSearchRunning, 0); }
    }

    // RA01: Cost - CPU: extremely low (one log line, once); GPU: none.
    static void Found(int a, int b, nint root, string why)
    {
        ownerSecond = b;
        ownerFirst = a;
        Log.Write($"RA01 camera-body probe: V's body root found - camera object +0x{a:X}{(b >= 0 ? $" -> +0x{b:X}" : "")} = 0x{root:X}, {why}");
    }

    // RA01: the body-root test: a sane upright turn, 1.0-2.2 m below the camera, within 0.6 m sideways.
    // Cost - CPU: extremely low (one 28-byte safe read and a few compares); GPU: none.
    static bool IsRoot(nint candidate, int camX, int camY, int camZ, out string why)
    {
        why = "";
        if (!ReadPlaced(candidate, out int x, out int y, out int z, out Quaternion q)) return false;
        float lengthSquared = q.LengthSquared();
        if (!(lengthSquared > 0.98f && lengthSquared < 1.02f) || MathF.Abs(q.X) > 0.05f || MathF.Abs(q.Y) > 0.05f) return false; // RA01: upright = no tilt parts
        float below = (camZ - z) / Scale, dx = (camX - x) / Scale, dy = (camY - y) / Scale;
        float sideways = MathF.Sqrt(dx * dx + dy * dy);
        if (below < 1.0f || below > 2.2f || sideways > 0.6f) return false;
        why = $"{below:F2} m below the camera, {sideways:F2} m to the side, facing {Yaw(q):F1} deg";
        return true;
    }

    // RA01: Cost - CPU: extremely low (one or two pointer-sized safe reads); GPU: none.
    static nint FollowOwner(nint camera)
    {
        if (ownerFirst == int.MaxValue) return 0;
        nint p;
        if (!Read(camera + ownerFirst, &p, 8) || !LooksLikePointer(p)) return 0;
        if (ownerSecond < 0) return p;
        nint q;
        return Read(p + ownerSecond, &q, 8) && LooksLikePointer(q) ? q : 0;
    }

    // RA01: step 3 - the camera measured on V's body: her turn undone (the conjugate), applied to the gap from her root to the camera; and the camera's turn relative to hers.
    // Logged once a second: the spot (right, forward, up in metres), which way her body faces in the world, and how the camera looks relative to her.
    // Cost - CPU: extremely low (a quaternion multiply and a vector turn; the log line once a second); GPU: none.
    // M15-1 - also, every camera build, the game's OWN camera (before your head) on V's body - spot and turn - for VRIK's gun calibration (GameLook).
    static void Report(int* camPos, float* camQuat, int rx, int ry, int rz, Quaternion rootTurn)
    {
        Quaternion undo = Quaternion.Conjugate(Quaternion.Normalize(rootTurn));
        Vector3 gap = new((camPos[0] - rx) / Scale, (camPos[1] - ry) / Scale, (camPos[2] - rz) / Scale);
        Vector3 onBody = Vector3.Transform(gap, undo);
        Quaternion lookOnBody = Quaternion.Normalize(undo * new Quaternion(camQuat[0], camQuat[1], camQuat[2], camQuat[3]));
        lock (lookGate) { gameLookSpot = onBody; gameLookTurn = lookOnBody; gameLookAt = System.Diagnostics.Stopwatch.GetTimestamp(); } // RA01: M15-1
        long now = Environment.TickCount64;
        if (now < Volatile.Read(ref nextReportMs)) return;
        Volatile.Write(ref nextReportMs, now + ReportEveryMs);
        Vector3 look = Vector3.Transform(Vector3.UnitY, lookOnBody); // RA01: the game camera looks along its own +Y
        float left = MathF.Atan2(-look.X, look.Y) * (180f / MathF.PI), up = MathF.Asin(Math.Clamp(look.Z, -1f, 1f)) * (180f / MathF.PI);
        Log.Write($"RA01 camera-body probe: camera on V's body at ({onBody.X:F3} right, {onBody.Y:F3} forward, {onBody.Z:F3} up) m; body faces {Yaw(rootTurn):F1} deg in the world; camera looks {left:F1} deg left, {up:F1} deg up of her body");
    }

    // RA01: M15-1 - the game's own camera (before your head) on V's body this frame: its spot (metres, her axes) and turn. False if none in the last 100 ms.
    // Cost - CPU: extremely low (a short lock and a few copies); GPU: none.
    public static bool GameLook(out Vector3 spot, out Quaternion turn)
    {
        lock (lookGate) { spot = gameLookSpot; turn = gameLookTurn; return gameLookAt != 0 && System.Diagnostics.Stopwatch.GetTimestamp() - gameLookAt < MaxAge; }
    }
    static readonly object lookGate = new();
    static Vector3 gameLookSpot;
    static Quaternion gameLookTurn;
    static long gameLookAt;

    // RA01: M9-3 - moves the game's camera on V's body by the VRIK tab's Camera forward / Camera up, turned by her body's turn into the world, before your head is added.
    // Everything after (the head, the published camera, the hands) builds on the moved camera, so view and hands stay together.
    // M11-2 - while HoldStandingHeight is set (physical crouch), the game's own crouch drop - how far its camera now sits below her standing camera height - is
    // added back up, so the view drops exactly as far as your real head does and no further. Not held, the highest camera spot is remembered as her standing one.
    // Cost - CPU: extremely low (one vector turn and a few adds and compares, per camera build); GPU: none.
    // M24-1 - not in a braindance or a taken-over camera (BlackboardProbe.RemoteView): that camera isn't V's eyes, so it stays where the game puts it.
    public static void ShiftCamera(int* camPos, in Root body)
    {
        if (!body.Valid || BlackboardProbe.RemoteView) return;
        float up = Tuning.CameraUpCm / 100f, z = gameSpotZ;
        if (!HoldStandingHeight) { if (z > 1.2f && z < 2.2f && z > standingSpotZ) standingSpotZ = z; } // RA01: a person's standing camera height; anything else (a car seat, a glitch) is ignored
        else if (standingSpotZ > 0 && standingSpotZ - z > 0.02f && standingSpotZ - z < 1.0f) up += standingSpotZ - z;
        if (Tuning.CameraForwardCm == 0 && up == 0) return;
        Vector3 shift = Vector3.Transform(new Vector3(0, Tuning.CameraForwardCm / 100f, up), body.Turn);
        camPos[0] += (int)MathF.Round(shift.X * Scale);
        camPos[1] += (int)MathF.Round(shift.Y * Scale);
        camPos[2] += (int)MathF.Round(shift.Z * Scale);
    }

    // RA01: M9-1 - called by CameraHook right after the head has been added onto the camera (Steer), with the finished position and turn in the setup block and the
    // head sample that was used. The eye offset (the camera was moved to one eye) is taken back out along the camera's own axes, so the published spot is the
    // head's centre - the same point the controllers are measured from. Then: V's turn undone, applied to the gap from her feet (the conjugate, as in Report).
    // M13-47 - the head's centre and the view's turn in the world are kept too (AimProbe aims the game's aim line from them).
    // Cost - CPU: extremely low (two quaternion products, two vector turns and an uncontended lock, per steered camera build, about 200 a second); GPU: none.
    public static void Publish(int* camPos, float* camQuat, in HeadPose.Sample head, in Root body)
    {
        if (!body.Valid) return;
        Quaternion camTurn = Quaternion.Normalize(new Quaternion(camQuat[0], camQuat[1], camQuat[2], camQuat[3]));
        Vector3 eyeInGame = new(head.EyeX, -head.EyeZ, head.EyeY); // RA01: OpenXR (x right, y up, -z forward) to the camera's own axes (x right, y forward, z up)
        Vector3 gap = new((camPos[0] - body.X) / Scale, (camPos[1] - body.Y) / Scale, (camPos[2] - body.Z) / Scale);
        Vector3 eyeOffset = Vector3.Transform(eyeInGame, camTurn);
        gap -= eyeOffset;
        Quaternion undo = Quaternion.Conjugate(body.Turn);
        Vector3 worldHead = new Vector3(camPos[0] / Scale, camPos[1] / Scale, camPos[2] / Scale) - eyeOffset; // RA01: M13-47 - the head's centre in the world
        OnBody value = new() { Position = Vector3.Transform(gap, undo), Turn = Quaternion.Normalize(undo * camTurn), WorldHead = worldHead, WorldTurn = camTurn, Time = System.Diagnostics.Stopwatch.GetTimestamp() };
        lock (writeGate) // RA01: several camera threads may publish; the slot is filled first and only then counted, so a reader never sees a half-written one
        {
            int next = published + 1;
            ring[next & 3] = value;
            Volatile.Write(ref published, next);
        }
    }

    // RA01: M9-1 - any thread (HandTargets, on the game's animation threads). True = 'value' is a whole, fresh reading.
    // Cost - CPU: extremely low (one struct copy, a memory barrier and two volatile reads; a retry is rare); GPU: none.
    public static bool TryRead(out OnBody value)
    {
        for (int tries = 0; tries < 4; tries++)
        {
            int newest = Volatile.Read(ref published);
            value = ring[newest & 3];
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref published) - newest < 3)
                return newest > 0 && System.Diagnostics.Stopwatch.GetTimestamp() - value.Time < MaxAge;
        }
        value = default;
        return false;
    }

    // RA01: which way an upright turn faces, in degrees (left of the world's +Y is positive).
    // Cost - CPU: extremely low (one atan2); GPU: none.
    static float Yaw(Quaternion q) => 2f * MathF.Atan2(q.Z, q.W) * (180f / MathF.PI);

    // RA01: the saved register block: 8 values written by the trampoline (rbx, rsi, rdi, r12-r15, and its own frame pointer), then the registers the trampoline pushed
    // (r11, r10, r9, r8, rdx, rcx, rax), which sit just above that frame pointer. The game's own rbp is the first thing at the frame pointer.
    // Cost - CPU: extremely low (one or two memory reads); GPU: none.
    static nint Register(nint regs, int index)
    {
        nint* block = (nint*)regs;
        if (index < 7) return block[index];
        nint* frame = (nint*)block[7];
        return index == 7 ? frame[0] : frame[index - 7]; // RA01: frame[1] = r11 ... frame[7] = rax, in the order the trampoline pushed them (reversed)
    }

    // RA01: a placed object's world position and turn, read safely.
    // Cost - CPU: extremely low (one 32-byte ReadProcessMemory); GPU: none.
    static bool ReadPlaced(nint obj, out int x, out int y, out int z, out Quaternion q)
    {
        byte* buf = stackalloc byte[32];
        x = y = z = 0; q = default;
        if (!Read(obj + PosAt, buf, 32)) return false;
        x = *(int*)buf; y = *(int*)(buf + 4); z = *(int*)(buf + 8);
        float* f = (float*)(buf + QuatAt - PosAt);
        q = new Quaternion(f[0], f[1], f[2], f[3]);
        return true;
    }

    // RA01: Cost - CPU: extremely low (three compares); GPU: none.
    static bool LooksLikePointer(nint p) => p > 0x10000 && p < 0x7FFF_FFFF_FFFF && (p & 7) == 0;

    // RA01: Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
