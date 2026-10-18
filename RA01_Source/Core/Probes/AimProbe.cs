using System.Numerics;
using System.Runtime.InteropServices;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M13-43 - THE LAST DOOR: the physics engine's own scene queries, inside PhysX3_x64.dll (PhysX 3.4, shipped with the game). Every ray the game checks
// against the world ends in PxScene::raycast, and every thick ray in PxScene::sweep - whichever part of the game sent it. M13-42 read the scene's
// function table in the running game: raycast is slot 0x2F0 = PhysX3_x64.dll +0x60890, sweep is slot 0x2F8 = +0x60960. Both are hooked here, checked
// first against the first bytes M13-42 logged.
// Why here: M13-38/39 (the game's ray builder) only caught the gunshot's echo (sound code); M13-40/41 (the game's ray runner) bent every camera ray
// 90 degrees and the bullets still went straight. So the bullet reaches PhysX some other way - but it must reach PhysX. Like the last gate of a city:
// whatever road a traveller took, they all pass under this one arch.
// What each asks for: raycast - start point (rdx), direction (r8), length (xmm3); sweep - a shape (rdx), its pose (r8: rotation, then position at +16),
// direction (r9), length. Positions are in the physics world's frame, which may be shifted from the game's (the game subtracts its world's offset,
// world +0x134, before asking - see RefreshShift).
// M13-43 RESULT: with every camera raycast AND sweep bent 90 degrees, the gun shot blanks - the bend reached the bullet for the first time. Bending
// camera raycasts alone (M13-41) never did, so the bullet is a SWEEP (a ray with thickness - a ball rolled along the line, so thin gaps can't swallow it).
// M13-44 RESULT: camera sweeps alone bent 20 degrees - bullets on centre. So it is not a sweep after all. What M13-43 bent that M13-41 didn't was
// the camera raycasts sent by the game's MULTI-HIT ray runner (exe+0x2397F8, which collects every surface along the line - what a bullet needs for
// penetration and ricochets); it hands PhysX its rays from exe+0x23992F, so they return to MultiHitReturn (0x239935).
// M13-45 (this build): the bend test turns ONLY camera raycasts from MultiHitReturn, and the multi-hit runner itself is hooked to count which of its
// three callers (MultiHitCallers) sends each ray - the one that rises by one per trigger pull is the bullet.
// (M13-44's wording, still describing the tally:) TEST ONLY: an open tally of up to 64 callers (by where they return to; sweeps marked SweepMark), how many of their queries started at the
// game's camera, and - the bend test - every SWEEP that starts at the camera is turned sideways first (raycasts are left alone now). Actions\FireArmAiming.cs switches it and writes the log; this class
// only carries it out (Core never decides gameplay).
// The game's ray runner (exe+0x2389CC) stays hooked only to learn which physics world is in use (for its offset).
// M13-46 RESULT: caller 1 (0x6F1FAF, about 135 rays a second) is the AIM LINE: the game traces it from the camera every frame and the shot goes where
// it hits. Bending only that one moved every non-smart gun's holes; callers 2 and 3 didn't.
// M13-47 - THE AIM LINE IS OURS. Actions (FireArmAiming) picks an AimMode; here it is only carried out, on the aim line alone, just before PhysX checks it:
//  - Game: left exactly as the game asked (melee, empty hands, ...).
//  - Reticle: from your head's centre, through the crosshair you SEE (XrSession.ReticleLook, turned by the view's world turn) - so a head-aimed weapon
//    hits what its crosshair covers, wherever the HUD panel stands.
//  - Barrel (M13-48): from the gun's muzzle, along its +Y axis (the M13-47 axis test: +Y stayed within a few degrees of your view while aiming down
//    the sights; X and Z near 90). The muzzle comes from VRIK's mailbox (WeaponProbe.TryReadMuzzleOnBody), measured on V's body, and is carried into the world.
// M13-48 - each stage is counted (aim lines seen in a mode, those starting at the camera, those re-laid), so the log shows where a mode stops.
// M13-49 RESULT: re-laying only the aim line moved the bullet HOLES but hurt nobody - the hit that does damage asks its own question from the eyes.
// M13-50 RESULT: re-laying EVERY camera raycast and sweep down the barrel while firing (thousands a second) still hurt nobody - damage is not decided
// by a PhysX query from the eyes (likely the game's own hit-shape test, fed from the shot's saved start and direction). M13-51: back to the aim line
// alone (the holes).
// M13-59/60: the shot itself (hit, damage, pellets, projectiles) is steered at its source - Core\Probes\ShotProbe.cs - using this class's mode and TryAimLine;
// the aim line here keeps the holes and the look-at check in step with it. M13-60 dropped the M13-48/50 stage counters (their logs are gone).
// Like re-laying the string a bricklayer stretches between two pegs: the game still lays its bricks along the string, we only move the pegs.
// Cost - CPU: low (a few thousand queries a second; each adds a short table search, one atomic count and a distance check; the aim line, about 135 a
// second, also a mailbox read, one safe read and a vector turn); GPU: none.
internal static unsafe class AimProbe
{
    // RA01: the game's ray runner's first 40 bytes in 2.31 (found once) - hooked only to note the physics world
    static ReadOnlySpan<byte> RayRunner => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x70, 0x10, 0x48, 0x89, 0x78, 0x18, 0x55, 0x41, 0x56, 0x41, 0x57,
        0x48, 0x8D, 0x68, 0x98, 0x48, 0x81, 0xEC, 0x50, 0x01, 0x00, 0x00, 0x4C, 0x8B, 0xF9, 0x49, 0x8B, 0xF0, 0x48, 0x8B, 0xCA };
    static ReadOnlySpan<byte> MovRaxRsp => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08 }; // RA01: mov rax,rsp; mov [rax+8],rbx

    // RA01: PhysX3_x64.dll (the game's copy): where its raycast and sweep start, and their first bytes (24 checked, the first whole instructions replaced)
    const int PxRaycastAt = 0x60890, PxSweepAt = 0x60960;
    static ReadOnlySpan<byte> PxRaycast => new byte[] { 0xF3, 0x0F, 0x11, 0x5C, 0x24, 0x20, 0x4C, 0x8B, 0xDC, 0x53, 0x48, 0x83, 0xEC, 0x70, 0x0F, 0xAE, 0x9C, 0x24, 0x88, 0x00, 0x00, 0x00, 0x8B, 0x9C };
    static ReadOnlySpan<byte> PxSweep => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x20, 0x55, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x80, 0x00, 0x00, 0x00, 0x48, 0x8B, 0xBC };
    static ReadOnlySpan<byte> MovssRsp20 => new byte[] { 0xF3, 0x0F, 0x11, 0x5C, 0x24, 0x20 };        // RA01: movss [rsp+20h],xmm3 - one whole instruction, 6 bytes
    static ReadOnlySpan<byte> MovRaxRspRbx20 => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x20 }; // RA01: mov rax,rsp; mov [rax+20h],rbx - 7 bytes

    public const int Slots = 64;
    static readonly long[] callerRva = new long[Slots];   // RA01: 0 = free slot; an exe RVA, or (PhysX inside its own DLL) the DLL offset + PhysXMark
    static readonly long[] counts = new long[Slots];
    static readonly long[] nearCounts = new long[Slots];   // RA01: rays that started within NearCamera of the game's camera
    static readonly float[] samples = new float[Slots * 6]; // RA01: per caller: start x, y, z, then direction x, y, z (its last ray, before any bend)
    public const long PhysXMark = 1L << 40;                // RA01: added to a return address that lies inside PhysX3_x64.dll (its own batch queries)
    public const long SweepMark = 1L << 41;                // RA01: M13-44 - added to a sweep caller's key, so raycasts and sweeps from one place are told apart
    static long overflow, sweeps, nearSweeps;
    static nint physX, lastWorld;
    static float shiftX, shiftY, shiftZ;                   // RA01: the physics world's offset from the game's world (world +0x134)
    static int installed;

    static volatile bool bendNear;                         // RA01: the bend test is on
    static float turnCos = 1f, turnSin;
    const float NearCamera = 0.5f;                         // RA01: metres - "starts at the camera"
    static long multiHitReturn = -1;                       // RA01: M13-45 - where PhysX returns to inside the game's multi-hit ray runner (2.31: 0x239935). M43 - found at
                                                           // RA01: start-up by LinkAimLine (was a fixed number that silently stopped matching on a new build); -1 = unknown, never matches
    static ReadOnlySpan<byte> CallRaycastSlot => new byte[] { 0xFF, 0x90, 0xF0, 0x02, 0x00, 0x00 }; // RA01: M43 - call qword [rax+2F0h]: PhysX's raycast, by its table slot
    const int RunnerSearchBytes = 0x800;                   // RA01: M43 - how far into the multi-hit runner that call is looked for (2.31: +0x137)

    // RA01: M13-45 - the game's multi-hit ray runner (2.31 exe+0x2397F8), its first 40 bytes (found once), and the three places that call it (return addresses)
    static ReadOnlySpan<byte> MultiHitRunner => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x70, 0x10, 0x48, 0x89, 0x78, 0x18, 0x55, 0x41, 0x56, 0x41, 0x57,
        0x48, 0x8D, 0xA8, 0x88, 0xEF, 0xFF, 0xFF, 0xB8, 0x60, 0x11, 0x00, 0x00, 0xE8, 0xF3, 0xC3, 0x7F, 0x01, 0x48, 0x2B, 0xE0 };
    public static readonly long[] MultiHitCallers = { 0x6F1FAF, 0xB8DF57, 0xC3521D };  // RA01: the byte after each call (inside exe+0x6F1CE4, +0xB8DEC4, +0xC3519C) - 2.31.
                                                                                       // RA01: M43 - checked at start-up by LinkAimLine; on a new build re-found, or -1 (never matches)
    static readonly long[] multiHitCounts = new long[4];   // RA01: per caller above, then "other"
    [ThreadStatic] static int multiHitCallerHere;          // RA01: M13-46 - which caller the multi-hit runner is working for on THIS thread right now (its PhysX
                                                           // RA01: call comes a moment later on the same thread, so the PhysX hook can read it)
    static volatile int bendOnlyCaller = -1;               // RA01: M13-46 - -1 = bend the rays of all three callers; 0..2 = only that one (MultiHitCallers order)
    const int AimLineCaller = 0;                           // RA01: M13-46 result - MultiHitCallers[0] (0x6F1FAF) traces the aim line

    // RA01: M13-47 - what the aim line does (see the class comment). Set by Actions\FireArmAiming.cs.
    public enum AimMode { Game, Reticle, Barrel }
    static volatile AimMode aimMode;

    // RA01: call once at start-up, after CameraHook (which loads ExeScan). PhysX3_x64.dll is loaded with the game, so it is already there. One log line per hook.
    // Cost - CPU: low, once (one scan of the code section, about 10 ms, and three 5-byte jumps written); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = HookRegistry.FindAll(HookRegistry.RayRunner, RayRunner);   // RA01: M43 - every door here reports to the Rehooker
            if (found.Count == 1) { if (!CrosshairProbe.Hook(found[0], MovRaxRsp, (nint)(delegate* unmanaged<nint, void>)&OnRunner, "ray runner (physics world only)")) HookRegistry.Off(HookRegistry.RayRunner, "the hook could not be put in"); }
            else Log.Write($"RA01 ray probe: the ray runner was found {found.Count} times (needs exactly 1) - the physics world's offset will be taken as zero");

            List<nint> multi = HookRegistry.FindAll(HookRegistry.MultiHit, MultiHitRunner, checkBytes: 32);   // RA01: M13-45. M43 - its bytes 32 on are a call distance that changes with every game update
            if (multi.Count == 1)
            {
                if (!CrosshairProbe.Hook(multi[0], MovRaxRsp, (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&OnMultiHit, "multi-hit ray runner (count callers)", returnAddressInR9: true))
                    HookRegistry.Off(HookRegistry.MultiHit, "the hook could not be put in");
                LinkAimLine(multi[0]);   // RA01: M43 - as before, the PhysX return point is used even if the runner itself could not be hooked
            }
            else
            {
                Log.Write($"RA01 ray probe: the multi-hit ray runner was found {multi.Count} times (needs exactly 1) - its callers won't be counted, the aim line stays the game's own");
                HookRegistry.Off(HookRegistry.MultiHitReturn, "needs the multi-hit runner");
                HookRegistry.Off(HookRegistry.AimCallers, "needs the multi-hit runner");
            }

            physX = GetModuleHandleW("PhysX3_x64.dll");
            if (physX == 0) { Log.Write("RA01 ray probe NOT installed: PhysX3_x64.dll is not loaded"); HookRegistry.Off(HookRegistry.PxRaycast, "PhysX3_x64.dll is not loaded"); HookRegistry.Off(HookRegistry.PxSweep, "PhysX3_x64.dll is not loaded"); return; }
            HookPhysX(HookRegistry.PxRaycast, PxRaycastAt, PxRaycast, MovssRsp20, (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&OnPxRaycast, "PhysX raycast", true);
            HookPhysX(HookRegistry.PxSweep, PxSweepAt, PxSweep, MovRaxRspRbx20, (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&OnPxSweep, "PhysX sweep", false, true);
        }
        catch (Exception e) { Log.Write($"RA01 ray probe failed: {e.Message}"); }
    }

    // RA01: M43 - THE AIM LINE'S TWO MARKERS, found instead of assumed. (1) Where PhysX returns to inside the multi-hit runner: the one "call [rax+2F0h]" (PhysX's
    // raycast slot) in the runner's first 2 KB. (2) Its three callers: on 2.31 the known three are checked (each must be a direct call to this runner);
    // on any other build every direct call to the runner is listed - exactly three are needed, taken in address order (the order 2.31 has), and if the
    // address list knows the aim line's function, the first must lie inside it. Anything else: logged and OFF - the aim line stays the game's own.
    // Builder picture: instead of trusting "the third door on the left", walk the corridor and count the doors that open onto this room.
    // Cost - CPU: extremely low on 2.31 (a 2 KB search and three 5-byte checks); medium once on a new build (one pass over the code, ~50-100 ms); GPU: none.
    static void LinkAimLine(nint runner)
    {
        ReadOnlySpan<byte> body = new((void*)runner, RunnerSearchBytes);
        int call = body.IndexOf(CallRaycastSlot);
        if (call < 0 || body[(call + 1)..].IndexOf(CallRaycastSlot) >= 0) { HookRegistry.Off(HookRegistry.MultiHitReturn, "the PhysX call isn't in the runner exactly once"); Log.Write("RA01 ray probe: the PhysX call inside the multi-hit runner wasn't found exactly once - the aim line stays the game's own"); }
        else
        {
            multiHitReturn = runner + call + CallRaycastSlot.Length - ExeScan.Base;
            HookRegistry.Found(HookRegistry.MultiHitReturn, runner + call + CallRaycastSlot.Length, "found inside the runner");
        }

        bool known = HookRegistry.SameBuild;
        for (int i = 0; known && i < 3; i++)
        {
            nint back = ExeScan.Base + (nint)MultiHitCallers[i];
            known = *(byte*)(back - 5) == 0xE8 && back + *(int*)(back - 4) == runner;
        }
        if (known) { HookRegistry.Found(HookRegistry.AimCallers, ExeScan.Base + (nint)MultiHitCallers[0], "all three checked"); return; }

        List<nint> callers = ExeScan.CallersOf(runner);
        callers.Sort();
        string why = callers.Count != 3 ? $"{callers.Count} callers of the runner (needs 3)" : "";
        int fn = HookRegistry.FunctionRva(HookRegistry.AimCallers);   // RA01: the aim line's own function (2.31 place, or the address list), 0 if unknown
        long first = why.Length == 0 ? callers[0] - ExeScan.Base : 0;
        if (fn > 0 && why.Length == 0 && (first < fn || first >= fn + 0x1000)) why = "the first caller isn't inside the aim line's function";
        for (int i = 0; i < 3; i++) MultiHitCallers[i] = why.Length == 0 ? callers[i] - ExeScan.Base : -1;
        if (why.Length > 0) { HookRegistry.Off(HookRegistry.AimCallers, why); Log.Write($"RA01 ray probe: the aim line's caller can't be told apart ({why}) - the aim line stays the game's own"); }
        else { HookRegistry.Found(HookRegistry.AimCallers, callers[0], "re-found: the 3 callers, in address order"); Log.Write($"RA01 ray probe: aim line callers re-found at RVA 0x{MultiHitCallers[0]:X}, 0x{MultiHitCallers[1]:X}, 0x{MultiHitCallers[2]:X}"); }
    }

    // RA01: hooks one PhysX function at its known offset, but only if its first 24 bytes are exactly the ones this game's copy has (a different PhysX build
    // is left alone, with a log line saying so). M43 - reported to the Rehooker ('id').
    // Cost - CPU: low, once (a 24-byte compare and one hook); GPU: none.
    static void HookPhysX(int id, int offset, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> replaced, nint handler, string what, bool returnInR9, bool returnInRcx = false)
    {
        nint site = physX + offset;
        if (!new ReadOnlySpan<byte>((byte*)site, expected.Length).SequenceEqual(expected)) { Log.Write($"RA01 ray probe: {what} is not where this game's PhysX has it (PhysX3_x64.dll +0x{offset:X}) - not hooked"); HookRegistry.Off(id, "this PhysX3_x64.dll differs"); return; }
        if (CrosshairProbe.Hook(site, replaced, handler, what, returnAddressInR9: returnInR9, returnAddressInRcx: returnInRcx)) HookRegistry.Found(id, ExeScan.Base + offset, "bytes checked");
        else HookRegistry.Off(id, "the hook could not be put in");
    }

    // RA01: M13-45 - the game's multi-hit ray runner was entered: count it under its caller (r9 = where it returns to).
    // Cost - CPU: extremely low (up to three compares and one atomic add); GPU: none.
    [UnmanagedCallersOnly]
    static void OnMultiHit(nint a, nint b, nint c, nint returnTo)
    {
        long rva = returnTo - ExeScan.Base;
        int i = 0;
        while (i < 3 && MultiHitCallers[i] != rva) i++;
        Interlocked.Increment(ref multiHitCounts[i]);
        multiHitCallerHere = i;
    }

    // RA01: M13-46 - TEMPORARY: limit the bend test to one of the multi-hit runner's callers (0..2, MultiHitCallers order), or all of them (-1).
    // Cost - CPU: extremely low (one store); GPU: none.
    public static void BendOnlyMultiHitCaller(int which) => bendOnlyCaller = which;

    // RA01: M13-45 - how many multi-hit rays each caller (MultiHitCallers order, then "other") has sent since start-up.
    // Cost - CPU: extremely low (four reads); GPU: none.
    public static void ReadMultiHit(long[] into)
    {
        for (int i = 0; i < 4; i++) into[i] = Volatile.Read(ref multiHitCounts[i]);
    }

    // RA01: the game's ray runner was entered: note the physics world it uses. Cost - CPU: extremely low (one store); GPU: none.
    [UnmanagedCallersOnly]
    static void OnRunner(nint world) => lastWorld = world;

    // RA01: PxScene::raycast is about to run: rdx = start point, r8 = direction (3 floats each), r9 = where the caller returns to. Tally it; if it starts at the
    // game's camera, count that and (bend test on) turn its direction in place. No allocation, no lock, never throws back into the game.
    // Cost - CPU: extremely low per call (a few compares, one atomic add, six float copies, a distance check); GPU: none.
    // M13-47 - the aim line (from the multi-hit runner, for AimLineCaller) is re-laid in any mode but Game (M13-51: only the aim line again).
    [UnmanagedCallersOnly]
    static void OnPxRaycast(nint scene, nint start, nint direction, nint returnTo)
    {
        bool multiHit = returnTo - ExeScan.Base == multiHitReturn;
        AimMode mode = aimMode;
        bool aimLine = multiHit && multiHitCallerHere == AimLineCaller && mode != AimMode.Game;
        Tally(returnTo, 0, start, direction, multiHit && (bendOnlyCaller < 0 || multiHitCallerHere == bendOnlyCaller), // RA01: M13-46 - the Dev bend test
              aimLine ? mode : AimMode.Game);
    }

    // RA01: M13-47, M13-48 - re-lays the aim line in place. Both modes start from the newest finished camera (CameraBodyProbe): your head's centre and the
    // view's turn in the world, and its spot and turn on V's body.
    //  - Reticle: start = your head's centre; direction = the crosshair's direction from inside your head (XrSession.ReticleLook) turned by the view's turn.
    //  - Barrel: VRIK measured the muzzle on V's body; it is carried into the world like a drawing from the workbench to the site - turned the way her
    //    body faces, then slid to where her feet stand. Her body's turn = the view's world turn with the view's turn on her body taken back off; her feet =
    //    your head in the world minus your head's spot on her body (turned the same way). Start = the muzzle there; direction = the muzzle's +Y, turned too.
    // The start then goes into the physics world's frame (the world's offset taken off, read fresh). Nothing is changed if a reading is stale.
    // True if it was re-laid.
    // Cost - CPU: extremely low (one or two mailbox copies, one safe read, up to five vector/quaternion turns and six stores); GPU: none.
    static bool LayAimLine(AimMode mode, float* start, float* direction)
    {
        if (!TryAimLine(mode, out Vector3 from, out Vector3 along) || !ReadShift(out Vector3 shift)) return false;
        from -= shift;
        if (!float.IsFinite(from.X + from.Y + from.Z)) return false;
        start[0] = from.X; start[1] = from.Y; start[2] = from.Z;
        direction[0] = along.X; direction[1] = along.Y; direction[2] = along.Z;
        return true;
    }

    // RA01: M13-53 - the line itself, in the GAME's world (metres, not shifted): Barrel = from the muzzle along its +Y; Reticle = from your head's centre
    // through the crosshair. The maths is LayAimLine's (see above); Core\Probes\ShotProbe.cs uses it too, to compare with what the game's shot carries.
    // False if a reading is stale or the result isn't a real number.
    // Cost - CPU: extremely low (one or two mailbox copies, up to five vector/quaternion turns); GPU: none.
    public static bool TryAimLine(AimMode mode, out Vector3 from, out Vector3 along)
    {
        from = along = default;
        if (!CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody view)) return false;
        if (mode == AimMode.Barrel)
        {
            if (!WeaponProbe.TryReadMuzzleOnBody(out Vector3 muzzle, out Quaternion muzzleTurn)) return false;
            Quaternion bodyTurn = Quaternion.Normalize(view.WorldTurn * Quaternion.Conjugate(view.Turn));
            Vector3 feet = view.WorldHead - Vector3.Transform(view.Position, bodyTurn);
            from = feet + Vector3.Transform(muzzle, bodyTurn);
            Vector3 barrel = Vector3.Transform(Vector3.UnitY, muzzleTurn);
            if (!Tuning.NoZeroing && GunCalibration.TryGet(GunCalibration.CurrentKey(), out Vector3 sight)) // RA01: M15-2 - ZEROED: aim at the sight line's point ZeroMetres out
                barrel = Vector3.Normalize(Vector3.Transform(sight, muzzleTurn) + barrel * GunCalibration.ZeroMetres);
            along = Vector3.Normalize(Vector3.Transform(barrel, bodyTurn));
            CorrectForGunZoom(from, ref along, view);                                        // RA01: M17-2 - LT zooms the drawn gun: follow the drawn sights
        }
        else
        {
            from = view.WorldHead;
            along = Vector3.Normalize(Vector3.Transform(XrSession.ReticleLook, view.WorldTurn));
        }
        return float.IsFinite(along.X + along.Y + along.Z + from.X + from.Y + from.Z);
    }

    // RA01: M17-2 - LT AIM CORRECTION. While LT reaches the game, it draws V's arms and gun through their own lens zoomed by the gun zoom (camera object +0x280
    // weight, +0x284 target; M16-E stopped holding it - now only READ, so nothing races). That lens enlarges the drawn gun about the middle of the view:
    // a sight that really points a little off the middle is DRAWN that many times further off. You line the drawn reticle up on the target, so the shot
    // must go where the drawn sight shows, not where the barrel truly points. Like a magnifying glass over a ruler: the mark you read is the real mark
    // times the glass's power, measured from its centre. So: the zeroed aim point (ZeroMetres out) is seen from the camera as two slopes (right and up per
    // metre ahead); both are multiplied by the gun zoom, and the shot goes from the muzzle to that point. A zoom of 1 (no LT) changes nothing.
    // Dev row "LT aim correction" turns it off. Logged once a second while it bends a shot.
    // Cost - CPU: extremely low (two 4-byte safe reads, two vector turns and a normalise per aim answer while LT is held); GPU: none.
    static void CorrectForGunZoom(Vector3 from, ref Vector3 along, in CameraBodyProbe.OnBody view)
    {
        if (!Tuning.LtAimCorrection || !ControllerMapping.LeftTriggerSent) return;
        bool read = CameraHook.TryGunZoom(out float zoom, out float weight, out float target);
        long now = Environment.TickCount64;
        bool say = now - Volatile.Read(ref lastZoomCorrectionLog) >= 1000;
        if (!read || zoom <= 1.001f)
        {
            if (say)                                                                         // RA01: M17-3 - also say why nothing was bent
            {
                Volatile.Write(ref lastZoomCorrectionLog, now);
                Vector3 l = Vector3.Transform(along, Quaternion.Conjugate(view.WorldTurn));
                Log.Write($"RA01 LT aim correction: nothing to bend - {(read ? $"gun zoom x{zoom:F2} (weight {weight:F2}, target {target:F2})" : "gun zoom unreadable")}; " +
                          $"the barrel points {MathF.Atan2(l.X, l.Y) * (180f / MathF.PI):F2} deg right, {MathF.Atan2(l.Z, l.Y) * (180f / MathF.PI):F2} deg up of the view centre");
            }
            return;
        }
        Quaternion back = Quaternion.Conjugate(view.WorldTurn);
        Vector3 local = Vector3.Transform(from + along * GunCalibration.ZeroMetres - view.WorldHead, back); // RA01: camera axes: x right, y ahead, z up
        if (!(local.Y > 1f)) return;
        Vector3 drawn = new(local.X * zoom, local.Y, local.Z * zoom);
        Vector3 bent = Vector3.Normalize(view.WorldHead + Vector3.Transform(drawn, view.WorldTurn) - from);
        if (!float.IsFinite(bent.X + bent.Y + bent.Z)) return;
        if (say)
        {
            Volatile.Write(ref lastZoomCorrectionLog, now);
            float deg = MathF.Acos(Math.Clamp(Vector3.Dot(bent, along), -1f, 1f)) * (180f / MathF.PI);
            Log.Write($"RA01 LT aim correction: gun zoom x{zoom:F2} (weight {weight:F2}, target {target:F2}) - shot bent {deg:F2} deg onto the drawn sights");
        }
        along = bent;
    }
    static long lastZoomCorrectionLog;

    // RA01: M16-A2 - the muzzle in the GAME's world (metres) and its turn there (+Y = along the barrel), the same way TryAimLine's Barrel finds it - and the
    // camera it was measured against. False if a reading is stale.
    // Cost - CPU: extremely low (two mailbox copies, two quaternion products, two vector turns); GPU: none.
    public static bool TryMuzzleInWorld(out Vector3 at, out Quaternion turn, out CameraBodyProbe.OnBody view)
    {
        at = default; turn = Quaternion.Identity;
        if (!CameraBodyProbe.TryRead(out view) || !WeaponProbe.TryReadMuzzleOnBody(out Vector3 muzzle, out Quaternion muzzleTurn)) return false;
        Quaternion bodyTurn = Quaternion.Normalize(view.WorldTurn * Quaternion.Conjugate(view.Turn));
        at = view.WorldHead - Vector3.Transform(view.Position, bodyTurn) + Vector3.Transform(muzzle, bodyTurn);
        turn = Quaternion.Normalize(bodyTurn * muzzleTurn);
        return float.IsFinite(at.X + at.Y + at.Z);
    }

    // RA01: M13-47 - sets what the aim line does (Actions decides; see AimMode).
    // Cost - CPU: extremely low (one store); GPU: none.
    public static void SetAimMode(AimMode mode) => aimMode = mode;

    // RA01: M13-57 - the mode Actions set last (ShotProbe steers V's bullets only while it is Barrel).
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static AimMode Mode => aimMode;


    // RA01: counts one query under its caller (finding or claiming the caller's slot), keeps a copy of it, counts it as a camera query if it starts at the
    // game's camera and then, M13-50: if 'relay' is a mode, re-lays it (LayAimLine) and counts that under its caller; otherwise, if 'bendable' and the
    // bend test is on, turns its direction in place. Shared by the raycast and sweep doors.
    // Cost - CPU: extremely low per call (a few compares, one atomic add, six float copies, a distance check; a re-lay adds LayAimLine's cost); GPU: none.
    static void Tally(nint returnTo, long mark, nint start, nint direction, bool bendable, AimMode relay)
    {
        long rva = returnTo - ExeScan.Base;
        if ((ulong)rva >= ExeScan.ImageSize) rva = returnTo - physX + PhysXMark;
        rva += mark;
        int slot = (int)((ulong)(rva * 0x9E3779B1) % Slots);   // RA01: start the search at a spot picked from the address, so most callers are found at once
        int tries = 0;
        for (; tries < Slots; tries++, slot = (slot + 1) % Slots)
        {
            long here = Volatile.Read(ref callerRva[slot]);
            if (here == 0) here = Interlocked.CompareExchange(ref callerRva[slot], rva, 0) == 0 ? rva : Volatile.Read(ref callerRva[slot]);
            if (here == rva) break;
        }
        bool counted = tries < Slots;                             // RA01: M13-50 - a full table still re-lays the query, it just isn't counted
        if (counted) Interlocked.Increment(ref counts[slot]); else Interlocked.Increment(ref overflow);
        if (start < 0x10000 || direction < 0x10000) return;
        float* s = (float*)start, d = (float*)direction;
        if (counted)
        {
            int k = slot * 6;
            samples[k] = s[0]; samples[k + 1] = s[1]; samples[k + 2] = s[2];
            samples[k + 3] = d[0]; samples[k + 4] = d[1]; samples[k + 5] = d[2];
        }
        if (!AtCamera(s)) return;
        if (counted) Interlocked.Increment(ref nearCounts[slot]);
        if (relay != AimMode.Game && LayAimLine(relay, s, d)) return;
        if (bendable && bendNear) TurnRay(d);
    }

    // RA01: PxScene::sweep is about to run: rcx = where the caller returns to (M13-44 - the detour puts it there; the scene pointer isn't needed), rdx = the
    // shape, r8 = its pose (rotation, then position at +16), r9 = direction. Tallied under its caller (marked as a sweep); one starting at the camera
    // is counted as such and (bend test on) turned.
    // Cost - CPU: extremely low per call (as Tally); GPU: none.
    [UnmanagedCallersOnly]
    static void OnPxSweep(nint returnTo, nint shape, nint pose, nint direction)
    {
        Interlocked.Increment(ref sweeps);
        if (pose < 0x10000) return;
        bool atCamera = AtCamera((float*)(pose + 16));
        if (atCamera) Interlocked.Increment(ref nearSweeps);
        Tally(returnTo, SweepMark, pose + 16, direction, false, AimMode.Game); // RA01: M13-45 - not bent; M13-51 - not re-laid (M13-50 showed it doesn't carry damage)
    }

    // RA01: true if a physics-world point is within NearCamera of the game's camera (the point plus the world's offset is the game-world point; compared
    // squared, so no square root is needed).
    // Cost - CPU: extremely low (a 36-byte copy, a few multiplies); GPU: none.
    static bool AtCamera(float* p)
    {
        CameraHook.ReadGameCamera(out float cx, out float cy, out float cz);
        float ex = p[0] + shiftX - cx, ey = p[1] + shiftY - cy, ez = p[2] + shiftZ - cz;
        return ex * ex + ey * ey + ez * ez <= NearCamera * NearCamera;
    }

    // RA01: turns a direction 'turn' degrees to the right, seen from above (about the up axis, z), in place - the game reads it straight after.
    // Like swinging a laser pointer on a turntable: the beam's height stays, only its heading changes. With c = cos and s = sin of the angle:
    // new x = x*c + y*s, new y = y*c - x*s (pointing ahead along +y, a turn to the right swings it toward +x).
    // Cost - CPU: extremely low (four multiplies, two adds); GPU: none.
    static void TurnRay(float* d)
    {
        float c = turnCos, s = turnSin, x = d[0], y = d[1];
        d[0] = x * c + y * s;
        d[1] = y * c - x * s;
    }

    // RA01: switches the bend test: on = every camera raycast from the multi-hit runner is turned 'degreesRight' first (M13-45); off = nothing is turned.
    // Cost - CPU: extremely low (a sine, a cosine and a few stores); GPU: none.
    public static void BendCameraRays(bool on, float degreesRight)
    {
        float a = degreesRight * (MathF.PI / 180f);
        turnCos = MathF.Cos(a); turnSin = MathF.Sin(a);
        bendNear = on;
    }

    // RA01: re-reads the physics world's offset from the game's world (world +0x134, 3 floats - the game subtracts it before every query). Safe reads; the
    // RA01: old offset stays if the world can't be read. Returns it for the log.
    // RA01: Cost - CPU: extremely low (one safe read, about once a second); GPU: none.
    public static (float x, float y, float z) RefreshShift()
    {
        if (ReadShift(out Vector3 v)) { shiftX = v.X; shiftY = v.Y; shiftZ = v.Z; }
        return (shiftX, shiftY, shiftZ);
    }

    // RA01: M13-47 - the physics world's offset, read now (false if no world has been seen or it can't be read).
    // Cost - CPU: extremely low (one 12-byte safe read); GPU: none.
    static bool ReadShift(out Vector3 shift)
    {
        nint world = lastWorld;
        float* f = stackalloc float[3];
        bool ok = world > 0x10000 && Read(world + 0x134, f, 12);
        shift = ok ? new Vector3(f[0], f[1], f[2]) : default;
        return ok;
    }

    // RA01: the tally so far: each slot's caller (exe RVA, or PhysX offset + PhysXMark; 0 = unused), its raycast count and how many of those started at the
    // camera, since start-up; plus all sweeps and camera sweeps; returns the rays that found no free slot.
    // Cost - CPU: extremely low (about 200 reads); GPU: none.
    public static long ReadTally(long[] rvas, long[] totals, long[] fromCamera, out long allSweeps, out long cameraSweeps)
    {
        for (int i = 0; i < Slots; i++) { rvas[i] = Volatile.Read(ref callerRva[i]); totals[i] = Volatile.Read(ref counts[i]); fromCamera[i] = Volatile.Read(ref nearCounts[i]); }
        allSweeps = Volatile.Read(ref sweeps); cameraSweeps = Volatile.Read(ref nearSweeps);
        return Volatile.Read(ref overflow);
    }

    // RA01: the last raycast slot 'i' asked for, before any bend: start point (physics world) and direction.
    // RA01: Cost - CPU: extremely low (six reads); GPU: none.
    public static (float sx, float sy, float sz, float dx, float dy, float dz) LastRay(int i)
    {
        int k = i * 6;
        return (samples[k], samples[k + 1], samples[k + 2], samples[k + 3], samples[k + 4], samples[k + 5]);
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got = 0;
        return ReadProcessMemory(GetCurrentProcess(), address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
