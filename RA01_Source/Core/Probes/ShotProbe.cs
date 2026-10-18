using System.Numerics;
using System.Runtime.InteropServices;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M13-52 to M13-60 - THE PLAYER'S AIM, at its source. Full hunt: project doc claude/RA01_M13-46_47_Aim_Line_2026-10-04.md.
// Every weapon of V's - the hitscan shot and its damage, every shotgun pellet, special rounds, the tracer, Smart and launcher projectiles - takes its
// start point and direction from one function, AIM SOURCE (2.31: 0x4D8354, "where is this character aiming from, and which way"). For V it answers
// from the game's own camera; the shot builders copy that answer (per shot: once from FIRE SHOT's per-bullet loop, return 0x2048BED, and twice through
// the bullet's aim provider, return 0x657410). Steering the answer HERE keeps hole, hit and damage agreeing - like moving the surveyor's tripod instead
// of correcting each builder's notes. (Steering any one consumer - the aim line, the bullet answer, the target slot - moved only part of the shot.)
// How: AIM SOURCE is hooked at its start (where its answers will be written: rdx = start, r8 = direction, an aim vector) and at its single exit
// (AimSourceExit: lea r11,[rsp+610h], where every path joins before the return). At the exit, an answer that starts at the game's camera (within
// NearCamera - NPCs aim from their own heads) is V's, and is rewritten as Actions' AimProbe mode says:
//  - Barrel (a gun with Physical aiming, while you fire): start = the muzzle, direction = down the barrel;
//  - anything else: left exactly as the game made it - the game's own aim is the middle of its picture, and since M13-61 the crosshair is always drawn
//    there (XrSession), so head-aimed weapons, Smart lock-on and the launcher need no steering. (M13-60 steered them through the lowered HUD panel's
//    centre - 17 degrees low with the user's HUD height - which put Smart shots off and launcher projectiles into the ground: removed.)
// The direction keeps the length the game gave it (it can be an aim vector, not unit length). At most 5 log lines a second.
// Cost - CPU: extremely low (per aim answer: a few stores, two 12-byte safe reads and a distance check; for V's steered answers a vector sum and two
// 12-byte safe writes - a few per shot); GPU: none.
internal static unsafe class ShotProbe
{
    // RA01: AIM SOURCE (2.31: 0x4D8354): its first 40 bytes (found exactly once), the 7 its entry jump replaces (mov rax,rsp; mov [rax+10h],rbx), and its exit
    static ReadOnlySpan<byte> AimSource => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48, 0x89, 0x78, 0x20, 0x55, 0x41, 0x54, 0x41, 0x55,
        0x41, 0x56, 0x41, 0x57, 0x48, 0x8D, 0xA8, 0xC8, 0xFA, 0xFF, 0xFF, 0x48, 0x81, 0xEC, 0x10, 0x06, 0x00, 0x00, 0x48, 0x8B };
    static ReadOnlySpan<byte> MovRaxRspRbx10 => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10 };
    static ReadOnlySpan<byte> LeaR11Rsp610 => new byte[] { 0x4C, 0x8D, 0x9C, 0x24, 0x10, 0x06, 0x00, 0x00 }; // RA01: lea r11,[rsp+610h] - 8 bytes
    const int AimSourceExit = 0x668;
    const float NearCamera = 0.5f;                             // RA01: metres - "starts at the camera"
    [ThreadStatic] static nint sourceStart, sourceDirection, sourceOwner; // RA01: M21-4 - and whose aim it is (for the throw probe)   // RA01: where this thread's AIM SOURCE will write, noted at its entry
    static long lastSteerLog;
    static int installed;

    // RA01: call once at start-up, after CameraHook (which loads ExeScan). One log line per hook.
    // Cost - CPU: low, once (one scan of the code section, about 10 ms, and two jumps written); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> source = HookRegistry.FindAll(HookRegistry.AimSource, AimSource);   // RA01: M43 - reported to the Rehooker
            if (source.Count == 1 && new ReadOnlySpan<byte>((byte*)(source[0] + AimSourceExit), LeaR11Rsp610.Length).SequenceEqual(LeaR11Rsp610))
            {
                if (!CrosshairProbe.Hook(source[0], MovRaxRspRbx10, (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnAimSource, "aim source, start")
                    || !CrosshairProbe.Hook(source[0] + AimSourceExit, LeaR11Rsp610, (nint)(delegate* unmanaged<void>)&OnAimSourceDone, "aim source, exit (steers V's aim)"))
                    HookRegistry.Off(HookRegistry.AimSource, "a hook could not be put in");
            }
            else
            {
                Log.Write($"RA01 aim source NOT hooked: found {source.Count} times or its exit differs - shots keep the game's own aim");
                if (source.Count == 1) HookRegistry.Off(HookRegistry.AimSource, "its exit differs from 2.31");
            }
        }
        catch (Exception e) { Log.Write($"RA01 aim source failed: {e.Message}"); }
    }

    // RA01: AIM SOURCE is starting (rcx = whose aim, rdx = where the start goes, r8 = where the direction goes): note both on this thread.
    // Cost - CPU: extremely low (two stores); GPU: none.
    [UnmanagedCallersOnly]
    static void OnAimSource(nint owner, nint start, nint direction)
    {
        sourceStart = start; sourceDirection = direction; sourceOwner = owner;
    }

    // RA01: AIM SOURCE is finishing: read its answer; if it starts at the game's camera it is V's - steer it (Steer). Never throws back into the game.
    // Cost - CPU: extremely low (two 12-byte safe reads and a distance check; Steer's cost for V's answers); GPU: none.
    [UnmanagedCallersOnly]
    static void OnAimSourceDone()
    {
        nint start = sourceStart, direction = sourceDirection, owner = sourceOwner;
        sourceStart = sourceDirection = sourceOwner = 0;
        if (start < 0x10000 || direction < 0x10000) return;
        float* f = stackalloc float[6];
        if (!Read(start, f, 12) || !Read(direction, f + 3, 12)) return;
        if (ThrowProbe.Watching && owner != 0 && owner == GameAnchor.V) ThrowProbe.NoteAim(f); // RA01: M21-4 - log only, any of V's answers during a throw
        CameraHook.ReadGameCamera(out float cx, out float cy, out float cz);
        float ex = f[0] - cx, ey = f[1] - cy, ez = f[2] - cz;
        if (ex * ex + ey * ey + ez * ez > NearCamera * NearCamera) return;
        NoteSpread(f);                                                   // RA01: M17-0 - log only, before any steering
        Steer(start, direction, f);
    }

    // RA01: M17-0 - LOG ONLY: how far the game's own shot direction (before we steer it) leans away from the middle of its camera's view - the spread, if the
    // game adds it here - with the left trigger held or not. One line per shot, at most 10 a second, while Tuning.AimSwitchProbe is on. If the angle grows
    // from the hip and shrinks with LT, the spread passes through this spot and can be tightened here (M17-1); if it is always about 0, it is added later.
    // Cost - CPU: extremely low (a mailbox read, a normalise and one angle per shot); GPU: none.
    static void NoteSpread(float* was)
    {
        if (!Tuning.AimSwitchProbe || !CameraBodyProbe.TryRead(out CameraBodyProbe.OnBody view)) return;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref lastSpreadLog) < 100) return;
        Volatile.Write(ref lastSpreadLog, now);
        Vector3 shot = Vector3.Normalize(new Vector3(was[3], was[4], was[5])), centre = Vector3.Transform(Vector3.UnitY, view.WorldTurn);
        float deg = MathF.Acos(Math.Clamp(Vector3.Dot(shot, centre), -1f, 1f)) * (180f / MathF.PI);
        if (float.IsFinite(deg)) Log.Write($"RA01 spread probe: the game's shot is {deg:F2} deg from its view centre, LT {(ControllerMapping.LeftTriggerSent ? "held" : "not held")} ({WeaponProbe.HeldName})");
    }
    static long lastSpreadLog;

    // RA01: rewrites one of V's aim answers ('was' = the game's start x, y, z and direction x, y, z) as the class comment says. Safe writes; anything stale or
    // not a real number leaves the game's answer alone. At most 5 log lines a second.
    // Cost - CPU: extremely low (a mailbox read, a few vector sums and two 12-byte safe writes); GPU: none.
    static void Steer(nint start, nint direction, float* was)
    {
        if (AimProbe.Mode != AimProbe.AimMode.Barrel || !AimProbe.TryAimLine(AimProbe.AimMode.Barrel, out Vector3 from, out Vector3 along)) return;
        along *= new Vector3(was[3], was[4], was[5]).Length();                              // RA01: keep the length of the game's aim vector
        if (!float.IsFinite(from.X + from.Y + from.Z + along.X + along.Y + along.Z)) return;
        nuint wrote = 0;
        float* f = stackalloc float[6] { from.X, from.Y, from.Z, along.X, along.Y, along.Z };
        WriteProcessMemory(GetCurrentProcess(), start, f, 12, &wrote);
        WriteProcessMemory(GetCurrentProcess(), direction, f + 3, 12, &wrote);
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref lastSteerLog) < 200) return;
        Volatile.Write(ref lastSteerLog, now);
        Log.Write($"RA01 aim steered (barrel): start ({was[0]:F2}, {was[1]:F2}, {was[2]:F2}) along ({was[3]:F3}, {was[4]:F3}, {was[5]:F3}) " +
                  $"-> start ({from.X:F2}, {from.Y:F2}, {from.Z:F2}) along ({along.X:F3}, {along.Y:F3}, {along.Z:F3})");
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got = 0;
        return ReadProcessMemory(GetCurrentProcess(), address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
