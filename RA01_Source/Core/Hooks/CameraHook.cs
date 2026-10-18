using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M4 - catches the moment the game builds its camera, so that the headset can steer it.
// M4b-2 - the head steers the camera: what the headset has done since the recentre (HeadPose) is put on top of the camera the game just built.
// M9-8 - it also measures your neck angle (head against V's body) for "body follows head" and keeps the view still while V's body catches up underneath your head
// (see TrackBodyFollow). ControllerMapping.cs feeds the catch-up turn in as a right-stick push. This file never touches the game's input itself, only publishes what it sees.
// how it works: the game's code is searched for a known run of bytes. The first instruction there is replaced by a 5-byte jump
// to a short piece of machine code (the trampoline). The trampoline calls OnCamera, runs the instruction that was replaced,
// and jumps back, so the game carries on exactly as before.
// Cost - CPU: extremely low (OnCamera runs on the game's own threads about 200 times a second, OnPresent 100 to 400 times a second, each a few dozen arithmetic operations and counters; the hook itself is installed once at start-up, see Install); GPU: none (nothing here touches the GPU).
internal static unsafe class CameraHook
{
    // RA01: the code around the place where the game fills in its camera setup block, found by the two public VR mods
    // RA01: (game version 2.31). The setup block is at [rbx]: position at +0x00, rotation at +0x10, field of view written at +0x20.
    // RA01: only LocateCamera is hooked. The other two are only counted, so we learn whether this build matches for later steps.
    static ReadOnlySpan<byte> LocateCamera => new byte[] { 0xF3, 0x0F, 0x11, 0x43, 0x20, 0x48, 0x8D, 0x54, 0x24, 0x20, 0x48, 0x8B, 0x06 };
    static ReadOnlySpan<byte> FinalCamera => new byte[] { 0xF3, 0x44, 0x0F, 0x5E, 0x4E, 0x40, 0x49, 0x81, 0xC0, 0x20, 0x0F, 0x00, 0x00 };
    static ReadOnlySpan<byte> PatchCamera => new byte[] { 0x0F, 0x11, 0x02, 0x80, 0xBE, 0xB1, 0x00, 0x00, 0x00, 0x00, 0x89, 0x45, 0x88 };

    const int JumpSize = 5;             // RA01: an E9 jump with a 32-bit distance
    const int ReplacedLength = 5;       // RA01: the jump replaces exactly one instruction: movss [rbx+20h], xmm0
    const int MaxThreads = 1024;
    const float PositionScale = 131072f; // RA01: the game stores positions as whole numbers of 1/131072 metre
    const int RotationAt = 0x10;        // RA01: the setup block holds x, y, z of the position (whole numbers), one more word, then the rotation x, y, z, w (floats)

    const uint AllocateNow = 0x3000;    // RA01: MEM_COMMIT | MEM_RESERVE
    const uint CanRunCode = 0x40;       // RA01: PAGE_EXECUTE_READWRITE
    const uint SnapshotThreads = 0x4;   // RA01: TH32CS_SNAPTHREAD
    const uint PauseAndRead = 0xA;      // RA01: THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT
    const int ContextSize = 1232;       // RA01: sizeof(CONTEXT)
    const int ContextFlagsAt = 48;      // RA01: offsetof(CONTEXT, ContextFlags)
    const uint ContextControl = 0x100001; // RA01: CONTEXT_CONTROL

    // RA01: the trampoline, in two halves. Between them go the 8 bytes of OnCamera's address; after the second half, the 8 bytes of
    // RA01: the address to jump back to. Windows passes the first argument in rcx and a second float argument in xmm1.
    static ReadOnlySpan<byte> TrampolineHead => new byte[]
    {
        0x9C,                                     // RA01: pushfq                 keep the flags
        0x50,                                     // RA01: push rax               keep every register the game may still need
        0x51,                                     // RA01: push rcx
        0x52,                                     // RA01: push rdx
        0x41, 0x50,                               // RA01: push r8
        0x41, 0x51,                               // RA01: push r9
        0x41, 0x52,                               // RA01: push r10
        0x41, 0x53,                               // RA01: push r11
        0x55,                                     // RA01: push rbp
        0x48, 0x89, 0xE5,                         // RA01: mov rbp, rsp           rbp remembers where the stack was
        0x48, 0x81, 0xEC, 0xA0, 0x00, 0x00, 0x00, // RA01: sub rsp, 0A0h          room for xmm0 to xmm5, and (M9-0) the 8-register block below them
        0x0F, 0x11, 0x45, 0xA0,                   // RA01: movups [rbp-60h], xmm0
        0x0F, 0x11, 0x4D, 0xB0,                   // RA01: movups [rbp-50h], xmm1
        0x0F, 0x11, 0x55, 0xC0,                   // RA01: movups [rbp-40h], xmm2
        0x0F, 0x11, 0x5D, 0xD0,                   // RA01: movups [rbp-30h], xmm3
        0x0F, 0x11, 0x65, 0xE0,                   // RA01: movups [rbp-20h], xmm4
        0x0F, 0x11, 0x6D, 0xF0,                   // RA01: movups [rbp-10h], xmm5
        0x48, 0x89, 0x9D, 0x60, 0xFF, 0xFF, 0xFF, // RA01: M9-0 - mov [rbp-0A0h], rbx   the registers the game keeps across calls, copied into a block for CameraBodyProbe
        0x48, 0x89, 0xB5, 0x68, 0xFF, 0xFF, 0xFF, // RA01: mov [rbp-98h], rsi           (read-only use: nothing here is ever written back into them)
        0x48, 0x89, 0xBD, 0x70, 0xFF, 0xFF, 0xFF, // RA01: mov [rbp-90h], rdi
        0x4C, 0x89, 0xA5, 0x78, 0xFF, 0xFF, 0xFF, // RA01: mov [rbp-88h], r12
        0x4C, 0x89, 0x6D, 0x80,                   // RA01: mov [rbp-80h], r13
        0x4C, 0x89, 0x75, 0x88,                   // RA01: mov [rbp-78h], r14
        0x4C, 0x89, 0x7D, 0x90,                   // RA01: mov [rbp-70h], r15
        0x48, 0x89, 0x6D, 0x98,                   // RA01: mov [rbp-68h], rbp     our own frame pointer: the registers pushed above sit just past it
        0x48, 0x83, 0xE4, 0xF0,                   // RA01: and rsp, -16           line the stack up the way a call needs
        0x48, 0x83, 0xEC, 0x20,                   // RA01: sub rsp, 20h           the 32 bytes Windows reserves for the callee
        0x48, 0x89, 0xD9,                         // RA01: mov rcx, rbx           first argument: the camera setup block
        0x0F, 0x28, 0xC8,                         // RA01: movaps xmm1, xmm0      second argument: the field of view
        0x4C, 0x8D, 0x85, 0x60, 0xFF, 0xFF, 0xFF, // RA01: M9-0 - lea r8, [rbp-0A0h]  third argument: the register block
        0x48, 0xB8,                               // RA01: mov rax, <8 bytes: OnCamera>
    };

    static ReadOnlySpan<byte> TrampolineTail => new byte[]
    {
        0xFF, 0xD0,                               // RA01: call rax                OnCamera answers with the field of view to store, in xmm0
        0xF3, 0x0F, 0x11, 0x45, 0xA0,             // RA01: movss [rbp-60h], xmm0   the answer goes into the low part of the saved xmm0 (its other parts stay as the game had them)
        0x0F, 0x10, 0x45, 0xA0,                   // RA01: movups xmm0, [rbp-60h]
        0x0F, 0x10, 0x4D, 0xB0,                   // RA01: movups xmm1, [rbp-50h]  put everything else back
        0x0F, 0x10, 0x55, 0xC0,                   // RA01: movups xmm2, [rbp-40h]
        0x0F, 0x10, 0x5D, 0xD0,                   // RA01: movups xmm3, [rbp-30h]
        0x0F, 0x10, 0x65, 0xE0,                   // RA01: movups xmm4, [rbp-20h]
        0x0F, 0x10, 0x6D, 0xF0,                   // RA01: movups xmm5, [rbp-10h]
        0x48, 0x89, 0xEC,                         // RA01: mov rsp, rbp
        0x5D,                                     // RA01: pop rbp
        0x41, 0x5B,                               // RA01: pop r11
        0x41, 0x5A,                               // RA01: pop r10
        0x41, 0x59,                               // RA01: pop r9
        0x41, 0x58,                               // RA01: pop r8
        0x5A,                                     // RA01: pop rdx
        0x59,                                     // RA01: pop rcx
        0x58,                                     // RA01: pop rax
        0x9D,                                     // RA01: popfq
        0xF3, 0x0F, 0x11, 0x43, 0x20,             // RA01: movss [rbx+20h], xmm0   the instruction the jump replaced
        0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,       // RA01: jmp [rip+0]             jump to the 8 bytes that follow
    };

    // RA01: what OnCamera saw, for the once-a-second report. This is diagnostics, so a torn read is acceptable and nothing here can reach the game.
    // RA01: the game builds cameras on about 20 threads at once, so the counters are changed with Interlocked. OnCamera never allocates:
    // RA01: everything below is created once, when the class is first used in Install.
    // RA01: Cost - CPU: extremely low (a 36-byte record, copied once per camera build); GPU: none.
    struct Seen
    {
        public int X, Y, Z;                   // RA01: position as the engine wrote it, in 1/131072 m
        public float Qx, Qy, Qz, Qw, Fov;     // RA01: rotation as the engine wrote it, and the field of view
    }

    static int installed;
    static bool hooked, silent;
    static long builds, presents, ageSum, ageMax, lastBuild, stackBuilds, oddBuilds, steered, unsteered;
    static long foldedBuilds;                 // RA01: M5a-8 - camera builds folded into the build before them since the last log line (they are not counted as builds)
    static int foldRun, foldLongest;          // RA01: M5a-8 - how many builds in a row have been folded into the current one, and the longest such run since the last log line (only for the log)
    [ThreadStatic] static HeadPose.Sample foldHead; // RA01: M5a-9 - the head pose the last build that was not folded was steered with; a folded build reuses it, so a pair of builds carries one pose and one eye (one copy per thread: no torn reads)
    [ThreadStatic] static bool foldHeadOk;          // RA01: M5a-9 - foldHead holds a pose that was really used (false after a build that was not steered, so a folded build reads its own)
    static long foldNewer;                    // RA01: M5a-9 - folded builds since the last log line that found a newer head pose already published (the case the reuse exists for; only for the log)
    static long lastMove;                     // RA01: M5a-7 - the time of the last camera build whose position or rotation (as the engine wrote it) differed from the build before
    static Seen latest;                       // RA01: the engine's most recent camera, before the head was added
    static (int X, int Y, int Z) latestSteered; // RA01: M6-10 - the same camera's position AFTER the head's offset was folded in (see Steer) - the one that actually moves when you crouch or lean, unlike 'latest' above
    static float latestFov;                   // RA01: M4b-3 - the field of view of the player's camera in degrees (0 = none seen yet), read by the headset thread to size the picture

    // RA01: the field of view the game draws with, for the headset thread to size the picture.
    // RA01: Cost - CPU: extremely low (one volatile read, once per headset frame); GPU: none.
    public static float Fov => Volatile.Read(ref latestFov);

    // RA01: M6-6 - the engine's own most recent camera position, in metres. BoneFinder compares a candidate bone's movement against THIS, not HeadPose's - the engine camera and a real bone
    // share the game's own world space, while HeadPose measures everything relative to the last recentre instead. False while nothing has been built yet (menu, loading, start-up).
    // Cost - CPU: extremely low (one Building check and three divides, called only while a bone hunt is running); GPU: none.
    public static bool TryReadEnginePosition(out float x, out float y, out float z)
    {
        if (!Building(Stopwatch.GetTimestamp())) { x = y = z = 0; return false; }
        var seen = latestSteered; // RA01: M6-10 - a torn read across the three ints is possible but harmless for a diagnostic tool that steers nothing - at most a stale frame's position
        x = seen.X / PositionScale; y = seen.Y / PositionScale; z = seen.Z / PositionScale;
        return true;
    }

    // RA01: M13-41 - the game's own latest camera position in metres, before any head steering (unlike TryReadEnginePosition, which needs the head to be
    // steering): flat or in the headset, this is where the game itself thinks the eye is. For AimProbe's bend test. A torn read across the three
    // numbers is possible but harmless here - at most a frame-old position. (0, 0, 0) before the first camera is built.
    // Cost - CPU: extremely low (a 36-byte copy and three divides); GPU: none.
    public static void ReadGameCamera(out float x, out float y, out float z)
    {
        var seen = latest;
        x = seen.X / PositionScale; y = seen.Y / PositionScale; z = seen.Z / PositionScale;
    }

    // RA01: M13-54 - the game's own latest camera TURN (before any head steering) - flat, it is exactly the view. With ReadGameCamera, for ShotProbe's test.
    // A torn read is possible but harmless here (normalised; at most a frame old).
    // Cost - CPU: extremely low (a 36-byte copy and a normalise); GPU: none.
    public static System.Numerics.Quaternion ReadGameCameraTurn()
    {
        var seen = latest;
        var q = new System.Numerics.Quaternion(seen.Qx, seen.Qy, seen.Qz, seen.Qw);
        return q.LengthSquared() > 0.5f ? System.Numerics.Quaternion.Normalize(q) : System.Numerics.Quaternion.Identity;
    }

    // RA01: M9-8 - "body follows head", by ANGLE with a dead zone (replaces M6's head-turn SPEED, which turned V on any brisk glance, and whose body turn then swung
    // RA01: the view a second time on top of the head's own turn). Two jobs here, both on the camera thread:
    // RA01:  1. the NECK angle: how far your head is turned away from the way V's body faces (degrees, left positive). The view is the engine's camera (always facing
    // RA01:     V's way) turned by your head's turn since the recentre, so the neck angle is that head turn minus followShift below. ControllerMapping reads it and only
    // RA01:     turns V once it passes the VR Controls tab's dead zone; VRIK reads it to turn her chest part of the way.
    // RA01:  2. the TURNTABLE: while ControllerMapping's catch-up push is on, every bit the engine's camera actually turns is added to followShift, and Steer takes
    // RA01:     followShift off your head's turn. Like a swivel chair on a turntable: the turntable turns V's body under you and the chair turns back by exactly the same
    // RA01:     amount, so the world stays still while her body catches up underneath your head. Measured from the engine's own camera, so it matches exactly however the
    // RA01:     game eases its turn. A physical stick turn is never counted (the catch-up waits while the stick is used), so stick turning still turns the view as before.
    static float followShift;                 // RA01: radians, left positive: how far the catch-up has turned V under your head since the last recentre
    static float neckDegrees;                 // RA01: published for ControllerMapping and VRIK
    static bool neckValid;                    // RA01: false while a menu shows or there is no head pose
    static float lastEngineYaw;               // RA01: the engine camera's facing at the previous build, radians, left positive
    static bool haveEngineYaw;
    static long followPushUntil;              // RA01: Stopwatch ticks: the catch-up push was on until about then (a short tail, since the engine eases out of a turn)
    static readonly long FollowTailTicks = Stopwatch.Frequency / 5; // RA01: 200 ms
    const float MaxYawStep = 0.5f;            // RA01: radians; a jump bigger than this between two builds is a camera cut or a load, never a turn, and is not counted

    // RA01: called from XrSession right after a recentre succeeds: the head's turn restarts at zero facing V's way, so the turntable restarts at zero too.
    // Cost - CPU: extremely low (two volatile writes, only when the player recentres); GPU: none.
    public static void NoteRecentre() { Volatile.Write(ref followShift, 0f); Volatile.Write(ref haveEngineYaw, false); }

    // RA01: M9-8 - the neck angle (see above), degrees, left positive. valid is false while a menu shows or there is no head.
    // Cost - CPU: extremely low (two volatile reads); GPU: none.
    public static float ReadNeckDegrees(out bool valid)
    {
        valid = Volatile.Read(ref neckValid);
        return Volatile.Read(ref neckDegrees);
    }

    // RA01: M9-8 - ControllerMapping says its catch-up push is on right now, so the engine's turn over the next moment is the turntable's (see above).
    // RA01: Cost - CPU: extremely low (a clock read and a volatile write, per controller read while catching up); GPU: none.
    public static float FollowShift => Volatile.Read(ref followShift); // RA01: M10-1 - how far body-follow has turned V under you since the recentre (radians, left positive), for the HUD panel's "V's body" follow

    public static void NoteFollowPush() => Volatile.Write(ref followPushUntil, Stopwatch.GetTimestamp() + FollowTailTicks);

    // RA01: M20-5 - the game's OWN camera tilt (before your head is added), degrees, up positive - for "Keep view level" (ControllerMapping). False in a menu
    // or with no camera built in the last 200 ms. Cost - CPU: extremely low (two volatile reads and a compare); GPU: none.
    public static bool TryEnginePitch(out float degrees)
    {
        degrees = Volatile.Read(ref enginePitch);
        return Stopwatch.GetTimestamp() - Volatile.Read(ref enginePitchAt) < Stopwatch.Frequency / 5;
    }
    static float enginePitch;
    static long enginePitchAt;

    // RA01: M9-8 - once per camera build, before Steer: adds the engine's turn to the turntable while the catch-up is pushing, then publishes the neck angle.
    // Cost - CPU: extremely low (two Atan2, a few multiplies and volatile reads/writes, once per camera build); GPU: none.
    static void TrackBodyFollow(bool menu, bool haveHead, in HeadPose.Sample head, float x, float y, float z, float w, long now)
    {
        if (!menu) { Volatile.Write(ref enginePitch, MathF.Asin(Math.Clamp(2 * (y * z + w * x), -1f, 1f)) * (180f / MathF.PI)); Volatile.Write(ref enginePitchAt, now); } // RA01: M20-5
        if (menu || !haveHead) { Volatile.Write(ref neckValid, false); Volatile.Write(ref haveEngineYaw, false); return; }
        float forwardX = 2 * (x * y - w * z), forwardY = 1 - 2 * (x * x + z * z);
        float shift = Volatile.Read(ref followShift);
        if (forwardX * forwardX + forwardY * forwardY > 1e-6f) // RA01: a camera looking straight up or down has no facing; skip this build
        {
            float yaw = MathF.Atan2(-forwardX, forwardY);
            bool dialogue = GameAnchor.SceneTier == DialogueTier;                           // RA01: M78 - a dialogue scene: the game's look-around is held off your view
            if (dialogue != dialogueWas) { dialogueWas = dialogue; Log.Write(dialogue ? "RA01 dialogue scene: the world held still - the game's look-around is taken off your view; your head (and stick) still look around (M78)" : "RA01 dialogue scene over: the game turns the view again (M78)"); }
            bool holdWorld = dialogue && MathF.Abs(ControllerMapping.TurnStick.X) <= StickTurning; // RA01: M78 - your own stick turn still turns the view
            if (Volatile.Read(ref haveEngineYaw) && (now < Volatile.Read(ref followPushUntil) || holdWorld))
            {
                float step = Wrap(yaw - lastEngineYaw);
                if (MathF.Abs(step) < MaxYawStep) { shift = Wrap(shift + step); Volatile.Write(ref followShift, shift); lookCounted += step; }
                else { lookSkipped++; lookSkippedRad += step; }                      // RA01: M77 look
                lookBuilds++;
                if (now >= lookNextAt) LogTurntable(now, 2f * MathF.Atan2(head.TurnSin, head.TurnCos), shift);
            }
            lastEngineYaw = yaw;
            Volatile.Write(ref haveEngineYaw, true);
        }
        float headTurn = 2f * MathF.Atan2(head.TurnSin, head.TurnCos); // RA01: the half-angle sin/cos Steer already carries, turned back into the full angle
        Volatile.Write(ref neckDegrees, Wrap(headTurn - shift) * (180f / MathF.PI));
        Volatile.Write(ref neckValid, true);
    }

    // RA01: M82 - CINEMATIC CLOSE-UPS GO TO THE FLAT PANEL (Rob 2026-10-15, Nocturne's rooftop ending 02:15-02:17): for its close-ups of V and Johnny the game
    // films with a long zoom lens (11-18 deg, its normal scene lens is 43-45); widened to the headset's 94 deg and steered by the head, a shot built for a narrow
    // lens felt glued to the face. So while a scene runs (scene tier CloseUpTier or more - dialogue and cutscenes, never gameplay, so a scope never counts) and
    // the game's own lens is under CloseUpLensDeg, the build is treated like a menu's: left as the game made it (no head, no wider view), and XrSession shows
    // it on the flat panel like the menus and end credits. Back to 3D once the lens has been wide again for CloseUpHoldSeconds (no flicker at the game's
    // cuts between wide and zoomed shots), or at once when the scene ends. Logs each change.
    // Like a cinema switching a 3D film to a flat screen for the shots that were only ever shot flat.
    // Cost - CPU: extremely low (a tier read and two compares per camera build; a log line per change); GPU: none.
    static bool CloseUp(float fov, long now)
    {
        bool scene = GameAnchor.SceneTier >= CloseUpTier;
        if (scene && fov > 1f && fov < CloseUpLensDeg)
        {
            closeUpWideSince = 0;
            if (!closeUp) { closeUp = true; Log.Write($"RA01 cinematic close-up (the game's lens {fov:F1} deg) - shown on the flat panel (M82)"); }
        }
        else if (closeUp)
        {
            if (closeUpWideSince == 0) closeUpWideSince = now;
            if (!scene || now - closeUpWideSince >= (long)(CloseUpHoldSeconds * Stopwatch.Frequency))
            { closeUp = false; closeUpWideSince = 0; Log.Write($"RA01 cinematic close-up over ({(scene ? $"the lens is wide again, {fov:F1} deg" : "the scene ended")}) - back to 3D (M82)"); }
        }
        return closeUp;
    }
    const int CloseUpTier = 3;
    const float CloseUpLensDeg = 30f, CloseUpHoldSeconds = 1f;
    static volatile bool closeUp;
    static long closeUpWideSince;

    // RA01: M82 - true while a cinematic close-up is on the flat panel (see CloseUp). Read by XrSession each headset frame. Cost - CPU: extremely low (one read); GPU: none.
    public static bool CinematicCloseUp => closeUp;

    // RA01: M78 - DIALOGUE HOLDS THE WORLD STILL (Rob 2026-10-14: in dialogue scenes - Johnny in the bar, V with Takemura on the way to Vik's - the game swings the
    // RA01: camera 40-60 deg at a time to look at whoever speaks; in VR the whole world swung round a still head, which read as a shake). While the scene tier is
    // RA01: DialogueTier (3, "limited" - the game's dialogue scenes; never 4-5, the cutscenes, which stay exactly as the game plays them), every turn the game's camera
    // RA01: makes is added to the turntable (followShift), exactly as body-follow's own turns are - so it is taken back off your view and the world stays put; you look
    // RA01: at the speaker by turning your head. Big steps (MaxYawStep, camera cuts) still go through, the camera's position is untouched, and your own stick turn
    // RA01: (past StickTurning) is never held. When the scene ends the neck angle already knows the difference, so body-follow lines V up as usual.
    // RA01: Like a camera on a gimbal: the cart turns under it, the lens stays pointed where the operator points it.
    const int DialogueTier = 3;
    const float StickTurning = 0.1f;
    static bool dialogueWas;

    // RA01: M77 LOOK (log only) - while body-follow pushes, once a second: how far the engine's camera turned and how much of it the turntable took off your view
    // (all of it, unless a step was too big - MaxYawStep - and skipped as a cut). In a body where the two don't match, the view would jolt: that's the shake
    // hunted (Rob, Never Fade Away as Johnny). Cost - CPU: extremely low (a few adds per camera build while pushing; a log line a second); GPU: none.
    static void LogTurntable(long now, float headTurn, float shift)
    {
        lookNextAt = now + Stopwatch.Frequency;
        if (lookLines++ < MaxLookLines)
            Log.Write($"RA01 turntable (M77 look): {lookBuilds} builds - counted {lookCounted * (180f / MathF.PI):F1} deg, skipped {lookSkipped} big step(s) ({lookSkippedRad * (180f / MathF.PI):F1} deg); " +
                      $"neck now {Wrap(headTurn - shift) * (180f / MathF.PI):F0} deg, {(GameAnchor.InJohnnysBody ? "Johnny's body" : GameAnchor.OnStandIn ? "a stand-in body" : "V's body")}");
        lookBuilds = lookSkipped = 0; lookCounted = lookSkippedRad = 0f;
    }
    static float lookCounted, lookSkippedRad;
    static int lookBuilds, lookSkipped, lookLines;
    static long lookNextAt;
    const int MaxLookLines = 600;

    // RA01: an angle wrapped into -180..+180 degrees (radians).
    // Cost - CPU: extremely low (a divide, a round and a multiply); GPU: none.
    static float Wrap(float a) => a - 2 * MathF.PI * MathF.Round(a / (2 * MathF.PI));

    // RA01: M5a-2 - which head pose (or eye's pose) each of the last camera builds was steered with, as the Stopwatch time that pose was published (0 = that build was not steered).
    // RA01: FrameCopy asks at every Present for the build 'FrameLag' builds back, so each picture is stamped with the exact pose it was built from, not a timing guess.
    // RA01: 64 slots (M5a-8: it was 16, M5a-7: 8), the newest at buildCount & 63, so even a long camera-cut burst cannot wipe the poses of the pictures still on their way to the screen. Camera threads only ever write a long,
    // RA01: so a torn read is not possible and no lock is needed.
    const int StampSlots = 64;
    static readonly long MaxBuildAge = Stopwatch.Frequency / 10;
    static readonly long[] stamps = new long[StampSlots];
    static int buildCount;

    // RA01: M5a-3 - which camera build each Present belongs to is found by COUNTING, not by asking which build is the newest at the moment of the Present (that was a race between the game's
    // RA01: camera thread and its render thread: the newest build was sometimes the next frame's, and the picture got the wrong pose, and sometimes the wrong eye).
    // RA01: The game draws every frame it builds a camera for, in order, so Present number p belongs to build number p + c for one fixed c (numbers counted from the first Present after a gap, which is taken to belong to
    // RA01: the newest build). c is not known, but a Present can only use a build that already exists, so at every Present the newest build number minus p (called the offset) is c + k, where k >= 0 is how many
    // RA01: builds the game's camera thread is ahead of that picture: 0 when the game thread is the slow one, 1 or more when the GPU is, so k changes with the load. No offset is ever below c.
    // RA01: M5a-5 - so the reference offset is the LOWEST offset seen in the last AlignMemory Presents: one sighting is proof, and it goes up only when that offset has not been seen for that long. The game builds and
    // RA01: presents one for one (the counts are equal over every second of play), so c does not move while the game is played. The rule before (the lowest offset that a tenth of the last 64 Presents had) hopped between two
    // RA01: values whenever k = 0 came near a tenth of the Presents, and in stereo a hop of one build swaps the eyes of the pictures: the jitter. The picture is paired with build p + reference - FrameLag.
    // RA01: The counting only runs on the Present thread. After 100 ms without a camera build (a menu, a loading screen) it starts again, since the counts mean nothing across such a gap.
    const int AlignMemory = 2048, AlignSpan = 32, AlignHalf = 16, RebaseAt = 8;
    const int StepAbove = 3, StepAfter = 32;                  // RA01: M5a-7 - the offsets have stepped up for good when 32 Presents in a row were all at least 3 above the reference
    const int JumpAbove = 8, JumpAfter = 3;                   // RA01: M5a-8 - or, for a jump this big (no game queue is ever 8 builds deep), when 3 Presents in a row were all at least 8 above it
    static long foldGap = Stopwatch.Frequency / 1000;    // RA01: M5a-8 - a camera build less than 1 ms after the one before is folded into it (a real frame takes at least 2 ms, the fastest seen was 478 a second)
    const int DoubledAfter = 3, WarningEvery = 60;            // RA01: M5a-6 - frame generation is reported after 3 seconds in a row, then at most once a minute
    static readonly int[] lastSeen = new int[AlignSpan];      // RA01: for each offset (slot = offset + 16, offsets far outside are clipped to the ends): the number of the Present (seenCount) that had it last, 0 = none since the counting started
    static int stepRun, stepLow;                              // RA01: M5a-7 - how many Presents in a row were at least StepAbove above the reference, and the lowest offset among them
    static int seenCount, alignRef;                           // RA01: how many Presents have been counted since the counting started, and the reference offset
    static int presentSeq, presentBuilds;                     // RA01: this Present's number (counted from the first one after a gap, which starts at the build count), and how many camera builds existed at that Present
    static int pairingSeconds;                                 // RA01: seconds since the last pairing line in the log
    static int doubledSeconds, secondsSinceWarning = WarningEvery; // RA01: M5a-6 - seconds in a row with 1.7 or more Presents per camera build, and seconds since the frame generation warning was last logged
    static readonly int[] pairingSeen = new int[3];           // RA01: for the log: Presents whose offset was the reference, one above it, two or more above it
    static int referenceChanges, windowLow = int.MaxValue, windowHigh = int.MinValue; // RA01: for the log: how often the reference changed, and the lowest and highest offset seen since the last pairing line

    // RA01: the pose stamp for the picture grabbed at the Present just counted by OnPresent, 'lag' builds behind the reference. 0 = that build was not steered, or there is none, or no camera
    // was built in the last 100 ms before 'now' (a menu, a loading screen: what the game draws then is not from the head's camera, however old the last stamp is).
    // Cost - CPU: extremely low (a few integer operations and one array read, once per Present, 100 to 400 a second); GPU: none.
    public static long StampOfPresent(int lag, long now)
    {
        if (lag < 0 || lag >= StampSlots || seenCount == 0 || now - Volatile.Read(ref lastBuild) > MaxBuildAge) return 0;
        int wanted = Math.Min(presentSeq + alignRef - lag, presentBuilds); // RA01: never a build that did not exist yet when the game presented (only while the reference is still being learned)
        if (wanted < 1 || Volatile.Read(ref buildCount) - wanted >= StampSlots) return 0; // RA01: build numbers start at 1; an older one than the ring keeps is gone
        return Volatile.Read(ref stamps[wanted & (StampSlots - 1)]);
    }

    // RA01: starts the counting again (a gap in the camera builds, or the first Present).
    // Cost - CPU: extremely low (clears 32 numbers; runs only after a gap of 100 ms in the camera builds, so a few times per menu or loading screen); GPU: none.
    static void ForgetAlignment()
    {
        Array.Clear(lastSeen);
        seenCount = alignRef = presentSeq = presentBuilds = stepRun = 0;
    }

    // RA01: M5a-4 - true while the game is playing: a camera build was made in the last third of a second before 'now'. False in a menu, a loading screen or a pause (and before the first build): the game then draws
    // RA01: no camera picture at all. A third of a second, not the 100 ms of the stamps, so a shader hitch or a quicksave does not shrink the picture to the menu size. With no camera hook there is nothing
    // RA01: to tell a menu from play by, and the picture is sized from the game's field of view as before.
    static readonly long MenuGap = Stopwatch.Frequency / 3;
    // RA01: 
    // Cost - CPU: extremely low (two volatile reads, a few times per headset frame); GPU: none.
    public static bool Building(long now)
    {
        if (!hooked) return true;
        long last = Volatile.Read(ref lastBuild);
        return last != 0 && now - last <= MenuGap;
    }

    // RA01: M5a-7 - true if the engine's own camera has moved (the head's addition does not count, and neither does a change of the field of view alone) at 'time' or later. A game that is paused or sitting in a menu
    // keeps the camera exactly still; a game being played moves it. MenuSense uses this to tell a menu that is still open from play.
    // Cost - CPU: extremely low (one volatile read, called once per headset frame while MenuSense is deciding); GPU: none.
    public static bool MovedSince(long time) => Volatile.Read(ref lastMove) >= time;

    // RA01: the lowest offset seen in the last AlignMemory Presents (the reference stays as it is while nothing has been seen yet).
    // Cost - CPU: extremely low (up to 32 comparisons, once per Present); GPU: none.
    static int LowestSeen()
    {
        for (int i = 0; i < AlignSpan; i++)
            if (lastSeen[i] != 0 && seenCount - lastSeen[i] < AlignMemory) return i - AlignHalf;
        return alignRef;
    }

    // RA01: notes one Present's offset and finds the reference again.
    // Cost - CPU: extremely low (a few dozen integer operations, once per Present); GPU: none.
    static void Align(int offset)
    {
        seenCount++;
        if (seenCount > 1) lastSeen[Math.Clamp(offset, -AlignHalf, AlignHalf - 1) + AlignHalf] = seenCount; // RA01: the first Present after a gap is only taken to be at offset 0 (see OnPresent), which proves nothing
        int reference = LowestSeen();
        if (reference != alignRef) { alignRef = reference; referenceChanges++; }
        pairingSeen[Math.Min(Math.Max(offset - alignRef, 0), 2)]++;
        windowLow = Math.Min(windowLow, offset);
        windowHigh = Math.Max(windowHigh, offset);
        if (offset - alignRef < StepAbove) stepRun = 0;
        else if (stepRun++ == 0 || offset < stepLow) stepLow = offset;
        if (stepRun >= StepAfter || (stepRun >= JumpAfter && stepLow - alignRef >= JumpAbove)) StepUp();
        else if (Math.Abs(alignRef) >= RebaseAt) Rebase();
    }

    // RA01: M5a-7 - a burst of camera builds that never reached the screen (a camera cut in a cutscene) raises every later offset for good. The lowest offset of the last 32 Presents is then the new reference: the
    // counting moves by that much at once (presentSeq + alignRef is what pairs a Present with a build) and the old sightings are forgotten, or the sticky minimum would pull the reference straight back down.
    // Cost - CPU: extremely low (clears 32 numbers; runs only when the offsets jump, a few times a minute in cutscenes); GPU: none.
    static void StepUp()
    {
        presentSeq += stepLow;
        Array.Clear(lastSeen);
        alignRef = 0;
        stepRun = 0;
        referenceChanges++;
        lastSeen[AlignHalf] = seenCount; // RA01: this Present is at offset 0 in the new counting
    }


    // RA01: over a long game the offsets can drift (a build without a Present, or a Present without a build, moves them by one, and only a gap of 100 ms starts the counting again). Before the reference
    // reaches the end of its range (where offsets are clipped and stop being told apart) the Present count and every remembered offset are moved by the reference, so it is 0 again. The pairing itself
    // does not change: StampOfPresent uses presentSeq + alignRef, and that sum stays the same.
    // Cost - CPU: extremely low (about 32 loop steps; runs only when the reference reaches the end of its range, rarely); GPU: none.
    static void Rebase()
    {
        int by = alignRef;
        presentSeq += by;
        stepRun = 0; // RA01: the offsets have a new zero
        Span<int> old = stackalloc int[AlignSpan];
        lastSeen.CopyTo(old);
        Array.Clear(lastSeen);
        for (int i = 0; i < AlignSpan; i++)
        {
            int to = Math.Clamp(i - by, 0, AlignSpan - 1);
            lastSeen[to] = Math.Max(lastSeen[to], old[i]);
        }
        alignRef = LowestSeen();
    }

    // RA01: M5a-8 - a folded build (see OnCamera) has no build number of its own: it rewrites the stamp of the build it was folded into, so the newest pose of the burst is the one the picture is given.
    // Cost - CPU: extremely low (one atomic add and one write, once per camera build, about 200 a second on the game's threads); GPU: none.
    // M26-0 - also keeps the build's finished position (as written into the setup block, 1/131072 m) for RenderCameraProbe to recognise it again.
    static void NoteBuild(long stamp, bool folded, int* position)
    {
        int count = folded ? Volatile.Read(ref buildCount) : Interlocked.Increment(ref buildCount);
        int slot = (count & (StampSlots - 1)) * 3;
        buildPos[slot] = position[0]; buildPos[slot + 1] = position[1]; buildPos[slot + 2] = position[2];
        Volatile.Write(ref stamps[count & (StampSlots - 1)], stamp);
    }
    static readonly int[] buildPos = new int[StampSlots * 3];

    // RA01: M26-1 - the pose stamp a build was steered with (0 = not steered, or too old for the ring). Cost - CPU: extremely low (a compare and a read); GPU: none.
    public static long StampOfBuild(int build) => build > 0 && Volatile.Read(ref buildCount) - build < StampSlots ? Volatile.Read(ref stamps[build & (StampSlots - 1)]) : 0;

    // RA01: M26-0 - the build number the counting pairs this Present with at frame lag 'lag' (the same sum StampOfPresent uses; 0 = none). Present thread only.
    // Cost - CPU: extremely low (a few integer operations); GPU: none.
    public static int WantedBuild(int lag) => seenCount == 0 ? 0 : Math.Min(presentSeq + alignRef - lag, presentBuilds);

    // RA01: M26-0 - which of the last StampSlots builds had this exact finished position (0 = none); 'nearest' = the closest one's distance in cm either way.
    // Cost - CPU: extremely low (up to 64 three-number compares); GPU: none.
    public static int FindBuild(int x, int y, int z, out float nearestCm)
    {
        int newest = Volatile.Read(ref buildCount);
        long best = long.MaxValue;
        for (int b = newest; b > 0 && b > newest - StampSlots; b--)
        {
            int slot = (b & (StampSlots - 1)) * 3;
            long dx = buildPos[slot] - x, dy = buildPos[slot + 1] - y, dz = buildPos[slot + 2] - z;
            long d = dx * dx + dy * dy + dz * dz;
            if (d == 0) { nearestCm = 0; return b; }
            if (d < best) best = d;
        }
        nearestCm = best == long.MaxValue ? -1 : MathF.Sqrt(best) / PositionScale * 100f;
        return 0;
    }

    // RA01: 
    // Cost - CPU: low, once (three scans of the game's 42 MB code section take about 10 ms each, plus a very short pause of the game's other threads while five bytes are changed; never repeated); GPU: none.
    public static void Install()
    {
        // RA01: running the launcher twice must not hook the same place twice, so only ever install once per game run.
        if (Interlocked.Exchange(ref installed, 1) == 1)
        {
            Log.Write("RA01 camera hook already installed, skipping");
            return;
        }

        try
        {
            // RA01: the first call of a Windows function has to look it up. Do that now, not inside the game's camera code.
            nuint low, high;
            GetCurrentThreadStackLimits(&low, &high);
            RuntimeHelpers.RunClassConstructor(typeof(HeadPose).TypeHandle); // RA01: HeadPose creates its slots now, never inside the game's camera code

            if (!ExeScan.Load(GetModuleHandleW(null!)))
            {
                Log.Write("RA01 camera hook NOT installed: the game's EXE header could not be read");
                return;
            }
            ExeScan.LogIdentity();

            nint site = Scan(HookRegistry.CameraLocate, "LocateCamera", LocateCamera);   // RA01: M43 - each one reports to the Rehooker
            Scan(HookRegistry.CameraFinal, "FinalCamera", FinalCamera);
            Scan(HookRegistry.CameraPatch, "PatchCamera", PatchCamera);
            if (site == 0)
            {
                Log.Write("RA01 camera hook NOT installed: LocateCamera was not found exactly once, so this game build may differ");
                return;
            }

            nint trampoline = AllocateNear(site);
            if (trampoline == 0)
            {
                Log.Write("RA01 camera hook NOT installed: no free memory within reach of the camera code");
                HookRegistry.Off(HookRegistry.CameraLocate, "no free memory within reach");
                return;
            }
            BuildTrampoline((byte*)trampoline, (nint)(delegate* unmanaged<nint, float, nint, float>)&OnCamera, site + ReplacedLength);

            if (!WriteJump(site, trampoline))
            {
                Log.Write("RA01 camera hook NOT installed: the jump could not be written (nothing was changed)");
                HookRegistry.Off(HookRegistry.CameraLocate, "the jump could not be written");
                return;
            }
            hooked = true;
            Log.Write($"RA01 camera hook installed at RVA 0x{site - ExeScan.Base:X}, trampoline at 0x{trampoline:X}");
            CrosshairProbe.Hook(site + ZoomWrittenAt, ZoomWritten, (nint)(delegate* unmanaged<void>)&OnZoomWritten, "aim zoom trim (M13-64)");
        }
        catch (Exception e)
        {
            Log.Write($"RA01 camera hook failed: {e.Message}");
        }
    }

    // RA01: looks for one run of code bytes and says how many times it was found. Only "exactly once" is good enough to hook.
    // M43 - through the Rehooker (HookRegistry.FindAll): its result is reported there, and if the pattern isn't found once, the row's own function is
    // checked by its bytes before giving up.
    // Cost - CPU: low, three times at start-up (one pass over the game's code section, about 10 ms); GPU: none.
    static nint Scan(int id, string name, ReadOnlySpan<byte> pattern)
    {
        List<nint> found = HookRegistry.FindAll(id, pattern);
        if (found.Count == 1)
        {
            Log.Write($"RA01 camera code {name}: found once, at RVA 0x{found[0] - ExeScan.Base:X}");
            return found[0];
        }

        Log.Write($"RA01 camera code {name}: found {found.Count} times (needs exactly 1)");
        // RA01: if the whole run is missing, see whether its first or last 8 bytes exist, which shows what changed in this build.
        LogPart(name, "first 8 bytes", pattern.Slice(0, 8), 0, pattern.Length);
        LogPart(name, "last 8 bytes", pattern.Slice(pattern.Length - 8), pattern.Length - 8, pattern.Length);
        return 0;
    }

    // RA01: one more search, only when a pattern was not found exactly once: shows where its first or last 8 bytes occur, to see what changed in this game build.
    // Cost - CPU: low, only on a failed start-up (one more pass over the code section, about 10 ms); GPU: none.
    static void LogPart(string name, string what, ReadOnlySpan<byte> part, int offsetInPattern, int patternLength)
    {
        List<nint> found = ExeScan.FindAll(part);
        Log.Write($"RA01 camera code {name}: its {what} occur {found.Count} time(s)");
        for (int i = 0; i < found.Count && i < 3; i++)
        {
            nint start = found[i] - offsetInPattern;
            Log.Write($"RA01   at RVA 0x{start - ExeScan.Base:X}: {ExeScan.Bytes(start - 8, patternLength + 16)}");
        }
    }

    // RA01: builds the trampoline (the machine code the jump leads to) at 'code'.
    // Cost - CPU: extremely low, once (copies about 150 bytes); GPU: none.
    internal static int BuildTrampoline(byte* code, nint callback, nint returnTo)
    {
        int at = 0;
        TrampolineHead.CopyTo(new Span<byte>(code + at, TrampolineHead.Length)); at += TrampolineHead.Length;
        *(long*)(code + at) = callback; at += 8;
        TrampolineTail.CopyTo(new Span<byte>(code + at, TrampolineTail.Length)); at += TrampolineTail.Length;
        *(long*)(code + at) = returnTo; at += 8;
        return at;
    }

    // RA01: the game calls this (through the trampoline) each time it fills in a camera setup block, just before the field of view is stored. What it returns is the field of view that is stored
    // (M5a-3): the game's own, or a wider one when Tuning.FovScale asks for it. Every path returns the game's own value unless the camera is the player's and a wider view is wanted.
    // Cost - CPU: extremely low (about 200 calls a second on the game's camera threads, each a few dozen arithmetic operations, a few atomic counters, no allocation and no lock, well under 10 microseconds); GPU: none.
    [UnmanagedCallersOnly]
    static float OnCamera(nint setup, float fov, nint regs)
    {
        float used = fov;
        try
        {
            if (setup < 0x10000) return fov;

            // RA01: the block is the engine's own: it has just written its position and rotation, and the very next instruction stores the field of view at +0x20.
            int* position = (int*)setup;
            float* rotation = (float*)(setup + RotationAt);

            if (OnThisThreadsStack(setup))
            {
                // RA01: a temporary camera the game builds on its own stack (the 3D map, for one). Only counted, never changed.
                Interlocked.Increment(ref stackBuilds);
                return fov;
            }

            float x = rotation[0], y = rotation[1], z = rotation[2], w = rotation[3];
            float lengthSquared = x * x + y * y + z * z + w * w;
            if (!(lengthSquared > 0.98f && lengthSquared < 1.02f)) // RA01: a real rotation has length 1; this also rejects NaN
            {
                Interlocked.Increment(ref oddBuilds);
                return fov;
            }
            long now = Stopwatch.GetTimestamp();
            long before = Volatile.Read(ref lastBuild);
            bool folded = before != 0 && now - before < foldGap; // RA01: M5a-8 - in a camera cut the game builds 30 to 65 cameras within a few milliseconds and draws none of them; they must not count as frames
            Volatile.Write(ref lastBuild, now);
            if (folded) { Interlocked.Increment(ref foldedBuilds); if (++foldRun > foldLongest) foldLongest = foldRun; }
            else { Interlocked.Increment(ref builds); foldRun = 0; }
            Seen was = latest;
            latest = new Seen { X = position[0], Y = position[1], Z = position[2], Qx = x, Qy = y, Qz = z, Qw = w, Fov = fov };
            if (latest.X != was.X || latest.Y != was.Y || latest.Z != was.Z || latest.Qx != was.Qx || latest.Qy != was.Qy || latest.Qz != was.Qz || latest.Qw != was.Qw) Volatile.Write(ref lastMove, now);
            bool menu = MenuSense.ShowsMenu || CloseUp(fov, now); // RA01: M5a-8 - in a game menu the game's own camera is left alone: no head steering, no wider view (M82: and a cinematic close-up)
            CameraBodyProbe.Root body = default;
            if (!menu) CameraBodyProbe.Observe(position, rotation, regs, out body); // RA01: M9-0 - read-only; must run BEFORE Steer below, while the block still holds the game's own camera
            if (!menu) CameraBodyProbe.ShiftCamera(position, in body); // RA01: M9-3 - the VRIK tab's camera forward/up on V's body, before the head goes on
            zoomSetup = menu ? 0 : setup; // RA01: M13-64 - the zoom goes into this same block a few instructions on (OnZoomWritten trims it)
            nint camera = ((nint*)regs)[1];                     // RA01: M13-68 - saved register 1 is rsi, the camera object here (CameraBodyProbe's log confirms it)
            if (!menu && camera >= 0x10000) cameraObject = camera; // RA01: M14-0 - kept for GameAnchor (M16-E: the aim lens writes that also lived here are gone)
            float fit = XrSession.FitFov;                       // RA01: M5b-2 - Projection mode: the view fitted to the headset (0 = not in use: Flat screen mode, or the headset not measured yet)
            used = menu ? fov : fit > 0 && fov > 20f && fov < 150f ? fit : WiderFov(fov, Tuning.FovScale);
            if (fov > 20f && fov < 150f) Volatile.Write(ref latestFov, used); // RA01: a number outside this is not a field of view (this also rejects NaN). The picture is sized for the field of view the game DRAWS with, the widened one

            // RA01: every camera that is not on a stack is the player's (found in M4b-1: exactly one per Present in normal play). No fresh head pose = the game's own camera stays.
            // RA01: M5a-9 - a folded build (less than 1 ms after the one before) reuses that build's head pose instead of reading a new one. The headset may publish the other eye's pose between the two, and
            // RA01: the game may draw either build of the pair; with one pose for both, the picture is stamped with the right pose and eye whichever one it is.
            bool reuse = folded && foldHeadOk && !menu;
            if (reuse)
            {
                if (HeadPose.TryRead(now, out HeadPose.Sample newer) && newer.Time != foldHead.Time) Interlocked.Increment(ref foldNewer);
            }
            else foldHeadOk = !menu && HeadPose.TryRead(now, out foldHead);
            TrackBodyFollow(menu, foldHeadOk, in foldHead, x, y, z, w, now); // RA01: M9-8 - the neck angle and the turntable, whether or not this build ends up steered
            if (foldHeadOk && Steer(position, rotation, x, y, z, w, in foldHead))
            {
                latestSteered = (position[0], position[1], position[2]); // RA01: M6-10 - captured AFTER Steer wrote the head's offset in, unlike 'latest' above
                CameraBodyProbe.Publish(position, rotation, in foldHead, in body); // RA01: M9-1 - the finished camera, measured on V's body, for HandTargets
                Interlocked.Increment(ref steered);
                NoteBuild(foldHead.Time, folded, position);
            }
            else
            {
                foldHeadOk = false;
                Interlocked.Increment(ref unsteered);
                NoteBuild(0, folded, position);
            }
        }
        catch
        {
            // RA01: an exception must never escape into the game
            return fov;
        }
        return used;
    }

    // RA01: M13-64 - AIM ZOOM TRIM. Aiming down sights (left trigger) does not narrow the field of view: the engine blends the camera object's ZOOM in (+0x27C, weight
    // RA01: +0x278 - M13-67; script SetZoom is not used for this, M13-66 found), and each frame the same function our camera hook sits in copies the zoom into the picture's setup block at +0x3C (component
    // RA01: vtable +0x270, written just before ZoomWrittenAt). (M13-35's SoftAimZoom trimmed the field of view instead, so it never did anything - removed.)
    // RA01: Here, right after that copy, while the left trigger reaches the game (and AimSettleMs after, while it zooms back out), the picture's copy is pulled most
    // RA01: of the way back to 1 (no zoom): kept = 1 + (zoom - 1) * AimZoomShare - x2.00 becomes x1.02. Like a magnifier dial with a stop on it: the game still turns
    // RA01: the dial (so it still knows V is aiming: tighter spread), but the picture only moves a hair. The camera's own setting is untouched; only the drawing copy
    // RA01: is trimmed. Only a zoom between 1 and MaxZoom is touched; at most one log line a second while trimming.
    // RA01: Cost - CPU: extremely low (one thread-local read and, while aiming, a multiply and a store per camera build); GPU: none.
    static ReadOnlySpan<byte> ZoomWritten => new byte[] { 0x48, 0x8B, 0x06, 0x4C, 0x8D, 0x43, 0x50 }; // RA01: mov rax,[rsi]; lea r8,[rbx+50h] - right after the zoom copy
    const int ZoomWrittenAt = 0x3B;     // RA01: from LocateCamera (2.31: 0x127F97 -> 0x127FD2)
    const int ZoomAt = 0x3C;            // RA01: the zoom in the setup block
    const float MaxZoom = 20f;
    const long AimSettleMs = 1000;
    [ThreadStatic] static nint zoomSetup;  // RA01: the player's setup block being filled on this thread (OnCamera notes it), 0 for any other
    static long aimSentAt = long.MinValue / 2, lastZoomLog;
    static float askedZoom = 1f;
    static long askedZoomAt;

    // RA01: M16-0 - the zoom the game asked for in the last half second (1 = none) - how long the fitted scope is (the scope magnifier's strength).
    // RA01: Cost - CPU: extremely low (two reads); GPU: none.
    public static float ScopeZoomAsked => Environment.TickCount64 - askedZoomAt < 500 ? askedZoom : 1f;

    [UnmanagedCallersOnly]
    static void OnZoomWritten()
    {
        nint setup = zoomSetup;
        zoomSetup = 0;
        if (setup == 0) return;
        try
        {
            long now = Environment.TickCount64;
            if (ControllerMapping.LeftTriggerSent) aimSentAt = now;
            if (now - aimSentAt > AimSettleMs) return;
            float* zoom = (float*)(setup + ZoomAt);
            float asked = *zoom;
            if (!(asked > 1.0001f && asked <= MaxZoom)) return;
            askedZoom = asked; askedZoomAt = now;                                 // RA01: M16-0 - what the game asks: the fitted scope's magnification (read only)
            float kept = 1f + (asked - 1f) * Tuning.AimZoomShare;
            *zoom = kept;
            if (now - lastZoomLog < 1000) return;
            lastZoomLog = now;
            Log.Write($"RA01 aim zoom: the game asked x{asked:F2}, the picture gets x{kept:F2}");
        }
        catch { } // RA01: an exception must never escape into the game
    }

    // RA01: M21-6 - REMOVED: a near plane test on the setup block's +0x4C (vtable +0x278). The log showed +0x40, +0x4C and +0x50 all 0 there - an "override,
    // RA01: 0 = default" slot, not the near plane - so it never wrote anything. The real near plane is on the render camera (+0x40): see CullingLab M21-7.

    // RA01: M16-E - REMOVED: the aim lens holds (M13-68/69 trimmed the gun zoom's weight, M16-C held its target, both kept the blur focus at rest). The game
    // RA01: rewrites its aim lens on the camera object every frame (23-44 times a second), so any write there raced it - the gun shrank and popped while the sights
    // RA01: opened. With the writes off the transition is smooth; the game's own small gun-zoom drift and blur are left as they are. Findings:
    // RA01: claude/RA01_M16_Scope_Lens_and_Aim_Lens_2026-10-05.md. The world zoom trim above (M13-64) stays: it changes the copy, so it never races.

    // RA01: M14-0 - the player's camera object, as last seen in a camera build (GameAnchor compares its owner with V). 0 = none seen yet.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static nint CameraObject => cameraObject;
    static nint cameraObject;

    // RA01: M17-2 - the game's gun zoom right now, READ ONLY from the camera object: weight (+0x280, how far the aim slide has gone) and target (+0x284), and the
    // zoom they make, 1 + (target - 1) x weight. False if there is no camera object or the values aren't sensible.
    // Cost - CPU: extremely low (two 4-byte safe reads); GPU: none.
    public static bool TryGunZoom(out float zoom, out float weight, out float target)
    {
        zoom = 1f; weight = target = 0f;
        nint camera = cameraObject;
        nuint done = 0;
        float w = 0, t = 0;
        if (camera < 0x10000 || !ReadProcessMemory(GetCurrentProcess(), camera + 0x280, &w, 4, &done) || !ReadProcessMemory(GetCurrentProcess(), camera + 0x284, &t, 4, &done)) return false;
        if (!(w >= 0f && w <= 1f && t > 0f && t < 20f)) return false;
        weight = w; target = t; zoom = 1f + (t - 1f) * w;
        return zoom > 0f;
    }

    // RA01: M5a-3 - the game's field of view made wider (or narrower) by 'scale' in the way a camera lens does it: the size of the view at a distance is multiplied by the scale (so aiming, which
    // zooms the game's own value in, still zooms in). A value that is not a field of view (or no change asked) comes back unchanged, and the result never passes 140 degrees.
    // Cost - CPU: extremely low (one tan and one atan, once per camera build); GPU: none.
    internal static float WiderFov(float fov, float scale)
    {
        if (!(fov > 20f && fov < 150f) || !(scale > 0f) || scale == 1f) return fov;
        float wider = 2f * MathF.Atan(MathF.Tan(fov * (MathF.PI / 360f)) * scale) * (180f / MathF.PI);
        return Math.Min(wider, 140f);
    }

    // RA01: M5a-11 - the opposite of WiderFov: given a widened field of view and the scale that made it, returns the game's own field of view before the widening.
    // This is exact algebra run backwards (undo the multiply, undo the lens curve), not a guess, so feeding a WiderFov result back in here returns the original fov.
    // Cost - CPU: extremely low (one tan and one atan, called once at start-up when AutoFov picks a scale); GPU: none.
    internal static float UnwidenFov(float widened, float scale)
    {
        if (!(widened > 20f && widened < 150f) || !(scale > 0f) || scale == 1f) return widened;
        float baseFov = 2f * MathF.Atan(MathF.Tan(widened * (MathF.PI / 360f)) / scale) * (180f / MathF.PI);
        return baseFov;
    }

    // RA01: puts the head on top of the camera the engine just built. Both parts start from the values the engine wrote for THIS build, never from what we wrote
    // before (M4b-1 showed the engine writes them fresh every build), so nothing can build up. x, y, z, w is the engine's rotation.
    // Cost - CPU: extremely low (about seventy multiplications, two square roots and four sin/cos (M9-8), once per steered camera build); GPU: none.
    static bool Steer(int* position, float* rotation, float x, float y, float z, float w, in HeadPose.Sample head)
    {
        // RA01: rotation, in three parts: the head's turn about the vertical (T) is applied to the world side of the engine's rotation E, and the rest of the head (S: nod and tilt)
        // RA01: to the camera side, so the result is T * E * S. The mouse's pitch then stays a pitch about the camera's yawed right axis, whatever way the head is turned,
        // RA01: and the horizon only rolls when the head itself tilts. (A plain E * head would turn the head about the PITCHED camera's up axis and roll the horizon.)
        // RA01: T is a turn about the game's z axis (0, 0, sin, cos), so T * E and S = T^-1 * head are short products. S has no z part (that is what taking the turn out means).
        // RA01: M9-8 - T is the head's turn MINUS the turntable (followShift), so the world stays still while body-follow turns V under you; S still takes off the full turn.
        float ts = head.TurnSin, tc = head.TurnCos;
        float shift = Volatile.Read(ref followShift), hs = MathF.Sin(shift / 2), hc = MathF.Cos(shift / 2);
        float ts2 = ts * hc - tc * hs, tc2 = tc * hc + ts * hs; // RA01: half-angle of (turn - shift)
        float ex = tc2 * x - ts2 * y, ey = tc2 * y + ts2 * x, ez = tc2 * z + ts2 * w, ew = tc2 * w - ts2 * z; // RA01: T * E
        float sx = tc * head.Qx + ts * head.Qy, sy = tc * head.Qy - ts * head.Qx, sw = tc * head.Qw + ts * head.Qz; // RA01: S = T^-1 * head, without its (zero) z part
        float nx = ew * sx + ex * sw - ez * sy;
        float ny = ew * sy + ey * sw + ez * sx;
        float nz = ex * sy - ey * sx + ez * sw;
        float nw = ew * sw - ex * sx - ey * sy;
        float lengthSquared = nx * nx + ny * ny + nz * nz + nw * nw;
        if (!(lengthSquared > 0.98f && lengthSquared < 1.02f)) return false; // RA01: two real rotations cannot give this, only NaN or a broken input can
        float unit = 1f / MathF.Sqrt(lengthSquared);

        // RA01: position: the head's offset is in the recentre's frame (forward = the way the head faced then), so it is turned to the way the engine's camera
        // RA01: faces on the ground plane. That is the game's forward (+Y) turned to the left by 'yaw', so forward is (-sin, cos) and right is (cos, sin).
        float forwardX = 2 * (x * y - w * z), forwardY = 1 - 2 * (x * x + z * z);
        float horizontal = MathF.Sqrt(forwardX * forwardX + forwardY * forwardY);
        float sin = 0, cos = 1; // RA01: a camera looking straight up or down has no ground direction, then the offset is used as it is
        if (horizontal > 0.001f) { sin = -forwardX / horizontal; cos = forwardY / horizontal; }
        // RA01: M9-8 - the head's offset is turned back by the turntable too, so your real spot in the room doesn't swing around V while she turns under you
        float fs = MathF.Sin(shift), fc = MathF.Cos(shift);
        float right = head.Right * fc + head.Forward * fs, forward = head.Forward * fc - head.Right * fs + SeatedForward(); // RA01: M46 - plus the Car view forward shift
        float east = right * cos - forward * sin, north = right * sin + forward * cos;

        rotation[0] = nx * unit; rotation[1] = ny * unit; rotation[2] = nz * unit; rotation[3] = nw * unit;
        position[0] += (int)MathF.Round(east * PositionScale);
        position[1] += (int)MathF.Round(north * PositionScale);
        position[2] += (int)MathF.Round(SeatedUp(head.Up) * PositionScale);
        return true;
    }

    // RA01: M20-1 - CAR VIEW HEIGHT: while V sits in anything but a motorcycle (car, truck, tank...), your head's height change is capped at 0 - it can go down
    // (duck, lean) but never above the game's own driver eye point - and the Vehicle tab's "Car view height" is added. On foot, getting in or out, and on
    // a bike: your head's height exactly as before (it goes back by itself the moment V is no longer seated).
    // Cost - CPU: extremely low (a cached state read, a min and an add, per camera build); GPU: none.
    static float SeatedUp(float up)
    {
        if (!VehicleState.IsSeated || VehicleState.Current is VehicleState.Kind.Bike or VehicleState.Kind.None) return up;
        return MathF.Min(up, 0f) + Tuning.CarViewHeightCm / 100f;
    }

    // RA01: M46 - CAR VIEW FORWARD: while V sits in anything but a motorcycle (car, truck, tank...), the Vehicle tab's "Car view forward" in metres, added to your
    // head's forward offset - the view slides that far along the car's own forward, like moving the driver's seat on its rails. 0 anywhere else (on foot,
    // getting in or out, on a bike) and with the slider at 0, so those take exactly the old path.
    // Cost - CPU: extremely low (a cached state read and a divide, per camera build); GPU: none.
    static float SeatedForward() =>
        !VehicleState.IsSeated || VehicleState.Current is VehicleState.Kind.Bike or VehicleState.Kind.None ? 0f : Tuning.CarViewForwardCm / 100f;

    // RA01: is this address inside the stack of the thread that is running right now?
    // Cost - CPU: extremely low (one quick Windows call that reads two numbers from the thread's own header, once per camera build); GPU: none.
    static bool OnThisThreadsStack(nint address)
    {
        nuint low, high;
        GetCurrentThreadStackLimits(&low, &high);
        return (nuint)address >= low && (nuint)address < high;
    }

    // RA01: the direction a rotation looks (the game's forward is +Y) as two angles in degrees: yaw, turning left from +Y, and pitch, up from level.
    // Cost - CPU: extremely low (an atan2 and an asin, twice a second for the log); GPU: none.
    static void Heading(float x, float y, float z, float w, out float yaw, out float pitch)
    {
        float forwardX = 2 * (x * y - w * z), forwardY = 1 - 2 * (x * x + z * z), forwardZ = 2 * (y * z + w * x);
        yaw = MathF.Atan2(-forwardX, forwardY) * (180f / MathF.PI);
        pitch = MathF.Asin(Math.Clamp(forwardZ, -1f, 1f)) * (180f / MathF.PI);
    }

    // RA01: how far a rotation tilts the top of the head to the right, in degrees (right = +X, up = +Z of the rotated camera).
    // Cost - CPU: extremely low (one atan2, twice a second for the log); GPU: none.
    static float Roll(float x, float y, float z, float w)
    {
        float rightZ = 2 * (x * z - w * y), upZ = 1 - 2 * (x * x + y * y);
        return MathF.Atan2(-rightZ, upZ) * (180f / MathF.PI);
    }

    // RA01: called on every Present: notes how long ago the latest camera build was.
    // Cost - CPU: extremely low (a few atomic counters and Align, on the game's render thread once per Present, 100 to 400 a second); GPU: none.
    public static void OnPresent()
    {
        if (!hooked) return;

        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref lastBuild);
        if (last == 0) return;

        long age = now - last;
        Interlocked.Increment(ref presents);
        Interlocked.Add(ref ageSum, age);
        long max = Volatile.Read(ref ageMax);
        while (age > max)
        {
            long seen = Interlocked.CompareExchange(ref ageMax, age, max);
            if (seen == max) break;
            max = seen;
        }

        if (age > MaxBuildAge) { ForgetAlignment(); return; } // RA01: M5a-3 - a gap in the builds: nothing to pair, and the counts start again afterwards
        int builds = Volatile.Read(ref buildCount);
        presentSeq = seenCount == 0 ? builds : presentSeq + 1; // RA01: the first Present after a gap is taken to belong to the newest build (the offsets that follow correct this if it was one off)
        presentBuilds = builds;
        Align(builds - presentSeq);
    }

    // RA01: once per second: how many camera builds happened, how many of them the head steered, and what the engine's camera and the head were doing.
    // Cost - CPU: low (formats and writes up to four log lines once a second, about 100 microseconds, on the headset thread); GPU: none.
    public static void LogSecond()
    {
        if (!hooked) return;
        long built = Interlocked.Exchange(ref builds, 0);
        long shown = Interlocked.Exchange(ref presents, 0);
        long sum = Interlocked.Exchange(ref ageSum, 0);
        long max = Interlocked.Exchange(ref ageMax, 0);
        long ignored = Interlocked.Exchange(ref stackBuilds, 0);
        long odd = Interlocked.Exchange(ref oddBuilds, 0);
        long foldedNow = Interlocked.Exchange(ref foldedBuilds, 0);
        int longest = foldLongest; foldLongest = 0;
        long newerNow = Interlocked.Exchange(ref foldNewer, 0);
        long moved = Interlocked.Exchange(ref steered, 0);
        long left = Interlocked.Exchange(ref unsteered, 0);
        if (built == 0 && ignored == 0 && odd == 0 && foldedNow == 0)
        {
            if (!silent) Log.Write("RA01 camera hook: no camera builds (a loading screen or a pause?)"); // RA01: said once, not every second
            silent = true;
            return;
        }
        silent = false;

        double toMs = 1000.0 / Stopwatch.Frequency;
        double avg = shown > 0 ? sum * toMs / shown : 0;
        Log.Write($"RA01 camera hook: {built} camera builds ({moved} steered by the head, {left} left as the game made them) and {shown} Presents; the latest build was {avg:F2} ms (max {max * toMs:F2} ms) old at each Present. "
            + $"Ignored: {ignored} built on a stack, {odd} with no valid rotation; {foldedNow} folded into the build before (camera-cut bursts, longest {longest}; {newerNow} of them found a newer head pose and kept the first build's)");

        // RA01: M5a-6 - frame generation makes up an extra Present after every real one. Counting Presents against camera builds then slips by one on every frame, and a made-up picture blends the two eyes
        // RA01: (neighbouring frames are the left and right eye), so the 3D shakes at every Frame lag setting. Rob's log showed exactly 2.0 Presents per build. Menus go back to 1.0, hence "3 in a row" and the reminder pause.
        if (secondsSinceWarning < WarningEvery) secondsSinceWarning++;
        doubledSeconds = built >= 30 && shown * 10 >= built * 17 ? doubledSeconds + 1 : 0;
        if (doubledSeconds >= DoubledAfter && secondsSinceWarning >= WarningEvery)
        {
            secondsSinceWarning = 0;
            Log.Write($"RA01 WARNING: the game shows {(double)shown / built:F1} Presents for every camera build. That is frame generation making extra pictures; in 3D each one is a blend of the two eyes and the pairing loses count, so the picture shakes. "
                + "Turn Frame Generation off in the game's Settings > Graphics (DLSS Super Resolution and ray tracing can stay on).");
        }

        // RA01: M5a-3 - every 10 seconds, how steady the game's build-to-Present pairing is (the offset spread; a race between the game's threads would show as a large share above the reference)
        if (++pairingSeconds >= 10)
        {
            pairingSeconds = 0;
            int atReference = Interlocked.Exchange(ref pairingSeen[0], 0), oneAbove = Interlocked.Exchange(ref pairingSeen[1], 0), moreAbove = Interlocked.Exchange(ref pairingSeen[2], 0);
            int total = atReference + oneAbove + moreAbove;
            if (total > 0)
                Log.Write($"RA01 pairing: of {total} Presents in 10 s the game's newest camera build was at the reference offset ({alignRef}) on {100 * atReference / total}%, one build above it on {100 * oneAbove / total}%, "
                    + $"two or more above on {100 * moreAbove / total}%; offsets seen {windowLow} to {windowHigh}, the reference changed {referenceChanges} times; frame lag {Tuning.FrameLag} pairs each picture with the build {Tuning.FrameLag} behind the reference");
            referenceChanges = 0; windowLow = int.MaxValue; windowHigh = int.MinValue;
        }

        Seen engine = latest;
        Heading(engine.Qx, engine.Qy, engine.Qz, engine.Qw, out float yaw, out float pitch);
        string head = "no usable head pose (steering off, no recentre yet, or the headset is not rendering)";
        if (HeadPose.TryRead(Stopwatch.GetTimestamp(), out HeadPose.Sample sample))
        {
            Heading(sample.Qx, sample.Qy, sample.Qz, sample.Qw, out float headYaw, out float headPitch);
            head = $"head turned {headYaw:F1} degrees left, {headPitch:F1} up, tilted {Roll(sample.Qx, sample.Qy, sample.Qz, sample.Qw):F1} right; "
                + $"offset {sample.Right:F3} m right, {sample.Forward:F3} forward, {sample.Up:F3} up";
        }
        Log.Write($"RA01   engine camera: position ({engine.X / PositionScale:F3}, {engine.Y / PositionScale:F3}, {engine.Z / PositionScale:F3}) m, "
            + $"direction yaw {yaw:F1}, pitch {pitch:F1}, field of view {engine.Fov:F2}; {head}");
    }

    // RA01: finds free memory within 2 GB of 'site' (a 5-byte jump can only reach that far) and makes it able to run code.
    // internal so PoseHook can reuse it - M7-0.
    // Cost - CPU: low, once (asks Windows for free memory near the hook site, normally found within a few tries); GPU: none.
    internal static nint AllocateNear(nint site)
    {
        const long Step = 0x10000;   // RA01: Windows hands out memory in 64 KB steps
        const long Reach = 0x70000000;
        long start = (long)site & ~(Step - 1);
        for (long distance = Step; distance < Reach; distance += Step)
        {
            nint block = VirtualAlloc((nint)(start + distance), 0x1000, AllocateNow, CanRunCode);
            if (block == 0 && start - distance > Step)
                block = VirtualAlloc((nint)(start - distance), 0x1000, AllocateNow, CanRunCode);
            if (block != 0) return block;
        }
        return 0;
    }

    // RA01: replaces the instruction at 'site' with a jump to 'trampoline'. All the game's other threads are paused while the bytes
    // change, so none of them can be running that instruction at that moment. False means nothing was changed.
    // internal so PoseHook can reuse it - M7-0.
    // Cost - CPU: low, once (lists the game's threads, pauses them all for well under a millisecond while five bytes are written, then resumes them); GPU: none.
    internal static bool WriteJump(nint site, nint trampoline)
    {
        long distance = (long)trampoline - ((long)site + JumpSize);
        if (distance > int.MaxValue || distance < int.MinValue) return false;

        // RA01: everything that allocates or waits happens BEFORE any thread is paused, including unlocking the code for writing.
        nint* threads = stackalloc nint[MaxThreads];
        int count = OpenOtherThreads(threads);
        if (count <= 0) return false; // RA01: without a list of threads to pause, live code is never touched
        byte* contextMemory = stackalloc byte[ContextSize + 16];
        byte* context = (byte*)(((long)contextMemory + 15) & ~15L); // RA01: Windows wants this block 16-byte aligned

        bool written = false;
        bool noGc = false;
        if (VirtualProtect(site, JumpSize, CanRunCode, out uint oldProtect))
        {
            noGc = StopGarbageCollection();

            // RA01: from here until every thread runs again there must be no allocation, no logging and no waiting: a paused thread
            // RA01: might be holding a lock that such a call would need. Only short direct Windows calls are used.
            for (int i = 0; i < count; i++)
                SuspendThread(threads[i]);
            for (int i = 0; i < count; i++)
            {
                *(uint*)(context + ContextFlagsAt) = ContextControl;
                GetThreadContext(threads[i], context); // RA01: SuspendThread can return early. This call waits until the thread has really stopped.
            }

            byte* code = (byte*)site;
            code[0] = 0xE9;
            *(int*)(code + 1) = (int)distance;

            for (int i = 0; i < count; i++)
                ResumeThread(threads[i]);
            // RA01: ---- the game's threads are running again ----

            AllowGarbageCollection(noGc);
            VirtualProtect(site, JumpSize, oldProtect, out _);
            FlushInstructionCache(GetCurrentProcess(), site, JumpSize);
            written = true;
            if (!noGc) Log.Write("RA01 camera hook: note - garbage collection could not be held off while the code was written");
        }

        for (int i = 0; i < count; i++)
            CloseHandle(threads[i]);
        return written;
    }

    // RA01: while the game's threads are paused, a garbage collection could never finish (it needs those threads), so none may start.
    // Cost - CPU: extremely low, once (asks the .NET runtime to reserve 1 MB so no collection starts); GPU: none.
    static bool StopGarbageCollection()
    {
        try { return GC.TryStartNoGCRegion(1 << 20); } catch { return false; }
    }

    // RA01: lets .NET garbage collection run again after WriteJump.
    // Cost - CPU: extremely low, once; GPU: none.
    static void AllowGarbageCollection(bool wasStopped)
    {
        if (!wasStopped) return;
        try { GC.EndNoGCRegion(); } catch { } // RA01: it has already ended by itself if the budget ran out, nothing to do then
    }

    // RA01: opens every thread of the game except ours, so they can be paused. Returns how many, or -1 if that could not be done safely.
    // Cost - CPU: low, once (a snapshot of all Windows threads and one OpenThread call for each of the game's roughly one hundred threads, a few milliseconds); GPU: none.
    static int OpenOtherThreads(nint* handles)
    {
        uint self = GetCurrentThreadId();
        uint process = (uint)Environment.ProcessId;
        nint snapshot = CreateToolhelp32Snapshot(SnapshotThreads, 0);
        if (snapshot == -1) return -1; // RA01: -1 is INVALID_HANDLE_VALUE

        int count = 0;
        bool tooMany = false;
        ThreadEntry entry = default;
        entry.Size = (uint)sizeof(ThreadEntry);
        for (bool more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry))
        {
            if (entry.OwnerProcessId != process || entry.ThreadId == self) continue;
            nint thread = OpenThread(PauseAndRead, false, entry.ThreadId);
            if (thread == 0) continue; // RA01: a thread that already ended
            if (count == MaxThreads) { CloseHandle(thread); tooMany = true; break; }
            handles[count++] = thread;
        }
        CloseHandle(snapshot);

        if (tooMany)
        {
            for (int i = 0; i < count; i++) CloseHandle(handles[i]);
            return -1;
        }
        return count;
    }
}
