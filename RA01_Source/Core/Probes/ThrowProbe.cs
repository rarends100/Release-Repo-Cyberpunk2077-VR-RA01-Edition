namespace RA01.Core;

// RA01: M21-4 - THE THROW PROBE (LOG ONLY, Dev row "Throw probe (test)"). Before grenades and knives can be thrown with your real arm, we need to know how the
// game throws them today. While the row is on, a small thread looks 50 times a second at:
//  - V's grenade state (GameAnchor.TryGadgetState: 2 in hand, 3 charging, 4 throwing, 5 putting away, 6 quick throw) and her melee state (19 = the
//    knife throw, GameAnchor.TryMeleeState), logging every change together with your right hand's position (floor space, metres: x right, y up,
//    -z forward) and its speed and direction worked out from the last few samples - like timing a pitcher's arm with a stopwatch and a tape;
//  - and, while a throw is under way (charging, throwing, quick throw, knife throw, and half a second after), every aim answer the game hands out for V
//    (ShotProbe calls NoteAim): where it starts and which way it points. That shows whether a grenade takes its launch line from the same place guns do,
//    so the throw could later be bent onto your hand's swing with the hook we already have.
// M22-2 - THE ARC TAP (Rob approved 2026-10-07). The 08:05 run showed throws never ask the gun aim path. A thrown grenade sets up its own flight arc:
// its script calls the native AddParabolic (2.31: exe+0x4732B8), whose worker (exe+0x47323C) gets (rcx = the projectile's component, rdx = the arc's
// settings object, gameprojectileParabolicTrajectoryParams). The game's own copy routine for that object (exe+0x473568) copies exactly +0x40 (4 bytes),
// +0x50 and +0x60 (16 bytes each) and +0x70/+0x74/+0x78 (4 bytes each) - so those are the arc's numbers (gravity, speed or target, angle...; which is which
// is what this run shows). The first time the Dev row is switched on, the worker's first instruction is sent through a machine-code copier (checked with
// capstone) that drops the component, the settings pointer and the settings' 64 bytes from +0x40 into a ring of 1024 slots; this thread logs each one with
// the game camera's position and whether V was throwing then. Rockets and NPC grenades show too - the "V throwing" tag tells them apart. Step 2 (M22-3)
// will write your hand's throw into the numbers found here, at the moment of release.
// M22-2b - THE FIRST RUN (Rob, 13:36-13:41): every KNIFE throw set up an arc here (+40 = 110, +50 = (0, 0, -20) the pull of gravity in m/s2, +60 unset
// (all "largest float" = no target), +74 = 150), but the 8 GRENADE throws (held and quick) set up none - grenades take another road. So a second tap
// now sits one step further down, where EVERY projectile's flight path is handed over to the projectile (2.31: exe+0x84F9AC "set trajectory" - called by
// AddParabolic, AddLinear, AddFollow, AddFollowCurve, the setup-event path that builds a path from a launch message, and two more). It copies the
// path object's first 128 bytes (its type's table address first, so each kind of path can be told apart) and the reader logs it the same way.
// M22-4 - the path tap and its order card moved to Core\Hooks\ThrowLever.cs (always on, used by Actions\PhysicalThrow.cs); the two M22-3 test rows are gone
// (both levers confirmed: speed x2 flew twice as far; target + angle + start mode 1 landed every grenade 8 m ahead). This probe still logs the arcs and
// the curved paths (read from the lever's ring) while its row is on.
// Builder picture: a carbon copy of the launch card every grenade fills in before it flies - first we read the cards, then we fill them in ourselves.
// Changes nothing in the game. At most 20 aim lines and 20 arc lines a second.
// Cost - CPU: extremely low while off (one bool test 50 times a second); low while on (two field reads, one hand sample and maybe a log line, 50 times a
// second); the arc tap: extremely low (about 30 machine instructions per arc set up, only when something is thrown or launched); GPU: none.
internal static unsafe class ThrowProbe
{
    // RA01: M22-2 - the copier, checked with a disassembler (capstone) before delivery:
    // RA01:  00 mov r11, <ring> (8 bytes at +0x02) / mov eax,1 / lock xadd [r11],eax / and eax,3FFh / shl eax,7 / add rax,r11 / add rax,40h  - the next 128-byte slot
    // RA01:  23 mov [rax],rcx (component) / mov [rax+8],rdx (settings) / test rdx,rdx / je +40h
    // RA01:  2F eight times: mov r10,[rdx+40h+8k] / mov [rax+10h+8k],r10                                       - the settings' 64 bytes from +0x40
    // RA01:  6F mov [rsp+10h],rbx (the worker's own first instruction) / jmp [rip+0] -> the 8 bytes at +0x7A (the worker, 5 bytes in)
    // RA01: Only rax, r10, r11 and the flags change - none carry anything into a function's first instruction (its inputs are rcx and rdx, left untouched).
    static ReadOnlySpan<byte> ArcDetour => new byte[] { 0x49, 0xBB, 0, 0, 0, 0, 0, 0, 0, 0, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x41, 0x0F, 0xC1, 0x03,
        0x25, 0xFF, 0x03, 0x00, 0x00, 0xC1, 0xE0, 0x07, 0x4C, 0x01, 0xD8, 0x48, 0x83, 0xC0, 0x40, 0x48, 0x89, 0x08, 0x48, 0x89, 0x50, 0x08, 0x48, 0x85, 0xD2, 0x74, 0x40,
        0x4C, 0x8B, 0x52, 0x40, 0x4C, 0x89, 0x50, 0x10, 0x4C, 0x8B, 0x52, 0x48, 0x4C, 0x89, 0x50, 0x18, 0x4C, 0x8B, 0x52, 0x50, 0x4C, 0x89, 0x50, 0x20,
        0x4C, 0x8B, 0x52, 0x58, 0x4C, 0x89, 0x50, 0x28, 0x4C, 0x8B, 0x52, 0x60, 0x4C, 0x89, 0x50, 0x30, 0x4C, 0x8B, 0x52, 0x68, 0x4C, 0x89, 0x50, 0x38,
        0x4C, 0x8B, 0x52, 0x70, 0x4C, 0x89, 0x50, 0x40, 0x4C, 0x8B, 0x52, 0x78, 0x4C, 0x89, 0x50, 0x48,
        0x48, 0x89, 0x5C, 0x24, 0x10, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    const int ArcRingAt = 0x02, ArcBackAt = 0x7A, ArcReplaced = 5;  // RA01: the jump covers "mov [rsp+10h],rbx" (5 bytes), run again in the copier
    const int ArcSlots = 1024, ArcSlotBytes = 128, ArcHeaderBytes = 0x40, ArcLinesPerSecond = 20;
    static nint arcRing;
    static int arcInstalled;
    static uint arcNext, pathNext;

    static long arcSecond;
    static int arcLines;

    const int TickMs = 20, Samples = 6;
    const long TailMs = 500;                             // RA01: aim answers are still logged this long after the throw states end
    static readonly float[] hx = new float[Samples], hy = new float[Samples], hz = new float[Samples];
    static readonly long[] ht = new long[Samples];
    static int next, filled, lastGadget = -1, lastMelee = -1, started;
    static long watchUntil, nextAimLog;
    static int aimLines;

    // RA01: true while V's aim answers should be logged (a throw under way). Read by ShotProbe on the game's threads. Cost - CPU: extremely low (two reads); GPU: none.
    public static bool Watching => Tuning.ThrowProbe && Environment.TickCount64 < Volatile.Read(ref watchUntil);

    // RA01: starts the probe's thread. Call once at start-up. Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(Loop) { IsBackground = true, Name = "RA01 throw probe" }.Start();
    }

    // RA01: 50 times a second while the Dev row is on: sample the hand, read the two states, log changes. Never throws.
    // Cost - CPU: see the class comment; GPU: none.
    static void Loop()
    {
        while (true)
        {
            Thread.Sleep(TickMs);
            try
            {
                if (!Tuning.ThrowProbe) { filled = 0; lastGadget = lastMelee = -1; if (arcRing != 0) arcNext = (uint)Volatile.Read(ref *(int*)arcRing); if (ThrowLever.Ready) pathNext = (uint)Volatile.Read(ref *(int*)ThrowLever.Ring); continue; }
                if (Interlocked.Exchange(ref arcInstalled, 1) == 0) InstallArcTap();  // RA01: M22-2 - the first time the row is on
                long now = Environment.TickCount64;
                ReadArcs(now);
                ReadPaths(now);
                SampleHand(now);
                int gadget = GameAnchor.TryGadgetState(out int g) ? g : -1, melee = GameAnchor.TryMeleeState(out int m) ? m : -1;
                bool throwing = gadget is 3 or 4 or 6 || melee == 19;
                if (throwing) Volatile.Write(ref watchUntil, now + TailMs);
                if (gadget != lastGadget || melee != lastMelee)
                {
                    Log.Write($"RA01 throw probe: grenade state {Name(gadget)}, melee state {(melee >= 0 ? GameAnchor.MeleeStateName(melee) : "?")} ({WeaponProbe.HeldName}); {HandText()}");
                    lastGadget = gadget; lastMelee = melee;
                }
            }
            catch (Exception e) { Log.Write($"RA01 throw probe: skipped a beat ({e.Message})"); }
        }
    }

    // RA01: M22-2 - puts the copier on AddParabolic's worker (found by ThrowLever at start-up). Any problem: logged, nothing is hooked.
    // Cost - CPU: low, once (a 5-byte write); GPU: none.
    static void InstallArcTap()
    {
        try
        {
            List<nint> found = ThrowLever.ArcWorker != 0 ? new() { ThrowLever.ArcWorker } : new();
            arcRing = ThrowLever.Hook("RA01 throw probe: arc tap (AddParabolic's worker)", found, ArcDetour, ArcRingAt, ArcBackAt, ArcReplaced, ArcHeaderBytes + ArcSlots * ArcSlotBytes);
            if (!ThrowLever.Ready) Log.Write("RA01 throw probe: the throw lever is not installed - no curved flight paths to log (see its start-up line)");
        }
        catch (Exception e) { Log.Write($"RA01 throw probe: arc tap failed: {e.Message}"); }
    }

    // RA01: M22-2 - logs the arcs set up since the last tick: the settings' numbers as floats (+0x40 also as a whole number), the lengths of the two 16-byte
    // groups, the game camera's position and whether V was throwing. At most ArcLinesPerSecond lines a second.
    // Cost - CPU: extremely low (usually nothing new; a few float reads and a log line per arc); GPU: none.
    static void ReadArcs(long now)
    {
        if (arcRing == 0) return;
        uint count = (uint)Volatile.Read(ref *(int*)arcRing);
        if (count - arcNext > ArcSlots) arcNext = count - ArcSlots;
        if (now / 1000 != arcSecond) { arcSecond = now / 1000; arcLines = 0; }
        bool vThrowing = now < Volatile.Read(ref watchUntil);
        for (; arcNext != count; arcNext++)
        {
            if (arcLines++ >= ArcLinesPerSecond) { arcNext = count; break; }
            byte* slot = (byte*)arcRing + ArcHeaderBytes + (arcNext & (ArcSlots - 1)) * ArcSlotBytes;
            long component = *(long*)slot, settings = *(long*)(slot + 8);
            float* f = (float*)(slot + 0x10);                  // RA01: f[0] = +0x40, f[4..7] = +0x50, f[8..11] = +0x60, f[12..14] = +0x70/+0x74/+0x78
            uint whole = *(uint*)(slot + 0x10);
            CameraHook.ReadGameCamera(out float cx, out float cy, out float cz);
            float len50 = MathF.Sqrt(f[4] * f[4] + f[5] * f[5] + f[6] * f[6]), len60 = MathF.Sqrt(f[8] * f[8] + f[9] * f[9] + f[10] * f[10]);
            Log.Write($"RA01 throw probe: ARC set up{(vThrowing ? " DURING V'S THROW" : "")} (component 0x{component:X}, settings 0x{settings:X}): " +
                $"+40 = {f[0]:G5} ({whole}); +50 = ({f[4]:F2}, {f[5]:F2}, {f[6]:F2}, {f[7]:F2}) length {len50:F2}; +60 = ({f[8]:F2}, {f[9]:F2}, {f[10]:F2}, {f[11]:F2}) length {len60:F2}; " +
                $"+70 = {f[12]:G5}, +74 = {f[13]:G5}, +78 = {f[14]:G5}; game camera at ({cx:F2}, {cy:F2}, {cz:F2}); {HandText()}");
        }
    }

    // RA01: M22-2c - logs the CURVED paths handed to projectiles since the last tick, laid out exactly like the arc lines (the arc settings' +0x40..+0x78),
    // with the game camera, the V-throwing tag and the hand. Other path types are skipped. Shares the arc lines' per-second limit.
    // Cost - CPU: extremely low (usually nothing new; a few float reads and a log line per path); GPU: none.
    static void ReadPaths(long now)
    {
        if (!ThrowLever.Ready) return;
        nint pathRing = ThrowLever.Ring;
        uint count = (uint)Volatile.Read(ref *(int*)pathRing);
        if (count - pathNext > ThrowLever.Slots) pathNext = count - ThrowLever.Slots;
        bool vThrowing = now < Volatile.Read(ref watchUntil);
        for (; pathNext != count; pathNext++)
        {
            byte* slot = (byte*)pathRing + ThrowLever.HeaderBytes + (pathNext & (ThrowLever.Slots - 1)) * ThrowLever.SlotBytes + 0x10;   // RA01: the lever's rax: holder at -0x10, type at 0, settings from +8
            if (*(long*)slot != ThrowLever.CurvedType) continue;
            if (arcLines++ >= ArcLinesPerSecond) { pathNext = count; break; }
            long holder = *(long*)(slot - 0x10);
            float* f = (float*)(slot + 8);                     // RA01: f[0] = +0x40, f[4..7] = +0x50, f[8..11] = +0x60, f[12..14] = +0x70/+0x74/+0x78
            CameraHook.ReadGameCamera(out float cx, out float cy, out float cz);
            Log.Write($"RA01 throw probe: PATH (curved) set{(vThrowing ? " DURING V'S THROW" : "")} (holder 0x{holder:X}): " +
                $"+40 = {f[0]:G5} ({*(uint*)f}); +50 = ({V(f[4])}, {V(f[5])}, {V(f[6])}, {V(f[7])}); +60 = ({V(f[8])}, {V(f[9])}, {V(f[10])}, {V(f[11])}); " +
                $"+70 = {f[12]:G5}, +74 = {f[13]:G5}, +78 = {f[14]:G5}; game camera at ({cx:F2}, {cy:F2}, {cz:F2}); {HandText()}");
        }
    }

    // RA01: M22-2c - a float for the log: "unset" for the largest float (the game's "no value"). Cost - CPU: extremely low; GPU: none.
    static string V(float v) => v == float.MaxValue ? "unset" : v.ToString("F2");

    // RA01: the game's own names for the grenade states (gamePSMCombatGadget). Cost - CPU: extremely low (a switch); GPU: none.
    static string Name(int s) => s switch { 0 => "none", 1 => "equip request", 2 => "in hand", 3 => "charging", 4 => "THROWING", 5 => "putting away", 6 => "QUICK THROW", -1 => "?", _ => s.ToString() };

    // RA01: keeps the right hand's last few positions (OpenXR floor space) with their times. Cost - CPU: extremely low (a few stores); GPU: none.
    static void SampleHand(long now)
    {
        if (!Controllers.RightGripPoseValid) return;
        Xr.Vector3f p = Controllers.RightGripPose.Position;
        hx[next] = p.X; hy[next] = p.Y; hz[next] = p.Z; ht[next] = now;
        next = (next + 1) % Samples;
        if (filled < Samples) filled++;
    }

    // RA01: the hand's position now and its average speed over the kept samples (newest minus oldest), as text. Cost - CPU: extremely low (a few sums); GPU: none.
    static string HandText()
    {
        if (filled < 2) return "right hand: not tracked";
        int newest = (next + Samples - 1) % Samples, oldest = filled < Samples ? 0 : next;
        float dt = (ht[newest] - ht[oldest]) / 1000f;
        if (dt <= 0) return "right hand: no time between samples";
        float vx = (hx[newest] - hx[oldest]) / dt, vy = (hy[newest] - hy[oldest]) / dt, vz = (hz[newest] - hz[oldest]) / dt;
        float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        return $"right hand at ({hx[newest]:F2}, {hy[newest]:F2}, {hz[newest]:F2}) m, moving {speed:F2} m/s along ({vx:F2}, {vy:F2}, {vz:F2}) over {dt * 1000:F0} ms";
    }

    // RA01: one of V's aim answers during a throw (from ShotProbe, on a game thread): its start and direction, with the game camera for comparison.
    // Cost - CPU: extremely low (a camera read and at most 20 log lines a second); GPU: none.
    public static unsafe void NoteAim(float* f)
    {
        long now = Environment.TickCount64;
        if (now >= Volatile.Read(ref nextAimLog)) { Volatile.Write(ref nextAimLog, now + 1000); aimLines = 0; }
        if (Interlocked.Increment(ref aimLines) > 20) return;
        CameraHook.ReadGameCamera(out float cx, out float cy, out float cz);
        float len = MathF.Sqrt(f[3] * f[3] + f[4] * f[4] + f[5] * f[5]);
        Log.Write($"RA01 throw probe: V's aim answer starts ({f[0]:F2}, {f[1]:F2}, {f[2]:F2}) - {MathF.Sqrt((f[0] - cx) * (f[0] - cx) + (f[1] - cy) * (f[1] - cy) + (f[2] - cz) * (f[2] - cz)):F2} m from the game camera - along ({f[3]:F3}, {f[4]:F3}, {f[5]:F3}), length {len:F2}");
    }
}
