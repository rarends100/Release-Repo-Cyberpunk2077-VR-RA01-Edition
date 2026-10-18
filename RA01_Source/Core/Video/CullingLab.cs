using System.Text;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M21-1 - THE CULLING LAB: live switches on the game's own "what not to draw" settings, for finding what hides a room's walls at a wide view.
// The game skips drawing things it believes are hidden behind "blocker walls" (occluders): every frame it draws those blockers into a tiny hidden depth
// picture through its own test camera, then hides anything behind them. That test camera's front glass (near plane) is forced to sit at least 0.2 m
// ahead of you, and the wider the view, the wider that glass gets (0.29 m each side at 110 degrees, 0.55 m at 140) - near a wall it can poke through
// a building's blocker shell, and the room behind the glass is then "hidden". Like a builder's sheet of plywood held too close to the face: tilt it
// wider and its edges go into the wall.
// The settings are the game's own config variables. Each is one object in the exe's data holding a pointer to its name (+0x08), its group (+0x10) and
// its value (+0x30), so each is FOUND BY NAME - the two string pointers side by side are searched for - which keeps working after game updates.
// The one switch that isn't a setting, "Occlusion on/off", is the jump that skips building the blocker picture when a camera's +0x19E byte is 0
// (found by its bytes, 2.31: exe+0x79E52C): off turns that jump into "always skip" (0F 84 -> 90 E9, same target), on puts it back.
// Nothing changes until the player moves a row; nothing is saved; every game start begins with the game's own values. A test bench, not a fix -
// the fix will set only what the lab proves, only as far as needed.
// Cost - CPU: medium once (about 15 s after start-up, one background search of the exe's data for five name pairs, tens of milliseconds); extremely low
// after that (a memory read per row the menu draws, a write when a row is moved); GPU: none itself - but with occlusion off the game draws everything
// behind walls, which can cost a lot of GPU (watch the Performance tab).
internal static unsafe class CullingLab
{
    // RA01: the game's settings this lab can move: group, name, and the type of the value (0 float, 1 bool).
    static readonly (string Group, string Name, int Type)[] Wanted =
    {
        ("Occlusion", "MinNearPlane", 0),
        ("Occlusion", "ForceFullTest", 1),
        ("Occlusion", "MultiFrustumUseGlobalOcclusion", 1),
        ("Streaming/Culling", "Enabled", 1),
        ("Debug", "LODUpdateUseRenderCamera", 1),   // RA01: M21-2 - on (normal): detail levels are chosen from the camera that draws the picture; off: the game's other way (read at 2.31 exe+0x4E7593)
    };
    public const int NearPlane = 0, FullTest = 1, SharedViews = 2, Streaming = 3, LodRenderCamera = 4;
    const int ValueAt = 0x30;
    static readonly nint[] found = new nint[5];
    static long nextNearLog;                        // RA01: M21-2 - the near-plane slider logs at most twice a second (it wrote ~1,000 lines in one sweep)     // RA01: each setting's object (0 = not found)
    static nint gate;                               // RA01: the occlusion gate's jump (0 = not found)
    static ReadOnlySpan<byte> GateBytes => new byte[] { 0x44, 0x38, 0x81, 0x9E, 0x01, 0x00, 0x00, 0x0F, 0x84 }; // RA01: cmp [rcx+19Eh],r8b; je ...
    const int GateJumpAt = 7;
    static volatile string status = "searching (starts about 15 s after the game starts)";
    static int started;

    // RA01: what the lab found, for the Dev tab. Cost - CPU: extremely low (a string read); GPU: none.
    public static string Status => status;

    // RA01: starts the one-off search on a background thread, a little after start-up (so the game has registered its settings). Call once.
    // Cost - CPU: extremely low here (starts a thread); the search itself is rated on the class; GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(() => { Thread.Sleep(15000); Find(); }) { IsBackground = true, Name = "RA01 culling lab" }.Start();
    }

    // RA01: finds the five settings by name and the occlusion gate by its bytes, then logs what it found (with the game's own values).
    // Cost - CPU: medium (once: a few string searches and one 16-byte search per name pair over the exe's data sections, tens of milliseconds); GPU: none.
    static void Find()
    {
        try
        {
            for (int i = 0; i < Wanted.Length; i++) found[i] = FindSetting(Wanted[i].Group, Wanted[i].Name);
            List<nint> gates = HookRegistry.FindAll(HookRegistry.CullingGate, GateBytes);   // RA01: M43 - reported to the Rehooker
            gate = gates.Count == 1 ? gates[0] + GateJumpAt : 0;
            if (gates.Count == 1) InstallProbe(gates[0] - GateFromStart);
            if (gates.Count == 1) InstallDebugViews(gates[0] - GateFromStart);
            if (gates.Count == 1) InstallNearTest(gates[0] - GateFromStart + PrepareFromSetup);
            int count = found.Count(f => f != 0);
            status = $"found {count} of 5 settings, occlusion switch {(gate != 0 ? "found" : $"NOT found ({gates.Count} matches)")}";
            Log.Write($"RA01 culling lab: {status}; game values: near plane {(found[NearPlane] != 0 ? $"{*(float*)(found[NearPlane] + ValueAt):F2} m" : "?")}, full test {Bool(FullTest)}, shared views {Bool(SharedViews)}, streaming culling {Bool(Streaming)}, LOD uses render camera {Bool(LodRenderCamera)}");
        }
        catch (Exception e) { status = $"search failed ({e.Message})"; Log.Write($"RA01 culling lab: {status}"); }
    }

    // RA01: M21-3 - THE CULLING CAMERA PROBE (read-only). The blocker system's setup (2.31: exe+0x79E50C) is given the camera it culls with at +0xE0 (0xAC bytes,
    // RA01: copied whole by exe+0x79B830) and the "use occlusion" byte at +0x19E. It is only ever reached through one entry of a function table (2.31: exe+0x2B11A98),
    // RA01: so that one entry is swapped for OnOcclusionSetup, which - while the Dev row is on - logs that camera's numbers every 5 s (up to 3 calls, as several
    // RA01: views may share it) and then always runs the game's own function, unchanged. From those numbers the lens (projection) the culling uses can be read
    // RA01: and compared with the 112-degree picture: if it is narrower, the edges of the view are thrown out before they are drawn.
    // RA01: The function is found from the gate's bytes (it starts 0x20 before them) and checked against its first 11 bytes; the table entry is found by
    // RA01: searching the data for that function's address - nothing is fixed to one game version.
    const int GateFromStart = 0x20;
    static ReadOnlySpan<byte> SetupStart => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x7C, 0x24, 0x18, 0x55 }; // RA01: mov [rsp+10h],rbx; mov [rsp+18h],rdi; push rbp
    const int CameraAt = 0xE0, CameraBytes = 0xAC, UseOcclusionAt = 0x19E;
    static delegate* unmanaged<nint, nint, void> originalSetup;
    static long nextProbe, probeWindow;
    static int probeCount;
    public static bool ProbeOn;                     // RA01: the Dev row "Culling camera probe (log)"; off at every start

    // RA01: finds the table entry that leads to the blocker setup and swaps in OnOcclusionSetup. Logs what it did. Any doubt (bytes differ, not exactly one
    // entry) = no swap. Cost - CPU: low (once: one 8-byte search of the data sections and a table write); GPU: none.
    static void InstallProbe(nint function)
    {
        if (!new ReadOnlySpan<byte>((void*)function, SetupStart.Length).SequenceEqual(SetupStart)) { Log.Write("RA01 culling lab: probe NOT installed (the blocker setup's first bytes differ)"); return; }
        long address = function;
        List<nint> slots = ExeScan.FindAll(new ReadOnlySpan<byte>(&address, 8), code: false);
        slots.RemoveAll(h => (h & 7) != 0);
        if (slots.Count != 1) { Log.Write($"RA01 culling lab: probe NOT installed ({slots.Count} table entries lead to the blocker setup)"); return; }
        originalSetup = (delegate* unmanaged<nint, nint, void>)function;
        PresentHook.Patch(slots[0], 0, (nint)(delegate* unmanaged<nint, nint, void>)&OnOcclusionSetup);
        Log.Write($"RA01 culling lab: probe installed on the blocker setup (exe+0x{function - ExeScan.Base:X}, table entry exe+0x{slots[0] - ExeScan.Base:X})");
    }

    // RA01: the swapped-in table entry: logs (when the Dev row is on and it's time) and then runs the game's own blocker setup, always. It never throws into the game.
    // Cost - CPU: extremely low (a few times a frame: a bool test and a clock read; while logging, every 5 s up to 3 lines of 43 numbers); GPU: none.
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void OnOcclusionSetup(nint self, nint job)
    {
        try { if (ProbeOn && self >= 0x10000) Probe(self); } catch { }
        originalSetup(self, job);
    }

    // RA01: one log line: the view's "use occlusion" byte and its culling camera as 43 numbers (4 bytes each), plus the picture's field of view for comparison.
    // Cost - CPU: low (a string of 43 numbers, at most 3 times every 5 s); GPU: none.
    static void Probe(nint self)
    {
        long now = Environment.TickCount64;
        if (now >= nextProbe) { nextProbe = now + 5000; probeWindow = now + 200; probeCount = 0; }
        if (now > probeWindow || probeCount >= 3) return;
        probeCount++;
        float* camera = (float*)(self + CameraAt);
        StringBuilder text = new($"RA01 culling probe: view 0x{self:X}, use occlusion {*(byte*)(self + UseOcclusionAt)}, picture fov {CameraHook.Fov:F1} (fit {XrSession.FitFov:F1}); camera +0xE0:");
        for (int i = 0; i < CameraBytes / 4; i++) text.Append(i % 4 == 0 ? $" | {camera[i]:G5}" : $" {camera[i]:G5}");
        Log.Write(text.ToString());
    }

    // RA01: M21-5 - ENGINE DEBUG VIEWS (Dev row "Engine debug view"). The engine keeps a list of 2048 on/off "show" switches for each view it draws (view +0x230,
    // RA01: 32 numbers of 64 bits). CDPR's developers used them to SEE their systems: the rooms (sectors), the interior areas, buildings as their outside shells.
    // RA01: Each switch is registered at start-up by a tiny function: the switch's name, then a 256-byte "mask" (the switch's own bit plus its parents' bits, filled
    // RA01: in by the game) - so each mask is FOUND BY NAME: the name string, then the one registration that points at it, then the mask it fills in. The occlusion
    // RA01: "order form" (2.31: exe+0x79E748, 0x23C after the blocker setup, checked against its first 7 bytes) gets the view at its input +0x10 every frame; a detour
    // RA01: at its start ORs the chosen mask into that view's switches just before the game reads them. Off takes back exactly the bits we added, in each view the
    // RA01: next time the game hands it over (up to 4 views are tracked).
    // RA01: Builder picture: switching on the site's own survey lights - nothing is moved, we just see the chalk lines the builders drew.
    // RA01: Shipped games sometimes strip the DRAWING behind a switch; then nothing shows, and the log still says the switch was found and set.
    // RA01: Cost - CPU: medium once (about 15 s after start-up: six string searches and one 7-byte search of the code, tens of milliseconds); extremely low after
    // RA01: (one int test per call while off; 32 ORs per view per frame while on); GPU: none itself - whatever the game's own debug drawing costs while on (low).
    static readonly string[] DebugViewFlags = { "", "Rendering/Visibility/CameraShowSectors", "Components/AreaShapes/Interiors", "Environment/InteriorMap/DrawBounds",
        "Environment/InteriorMap/DrawCameraInfo", "Prefabs/Proxy/NodeProxyMeshOnly", "Rendering/Visibility/CameraShowOccluders" };
    public static readonly string[] DebugViewNames = { "Off", "Sectors", "Interior areas", "Interior map bounds", "Interior map camera", "Shells only", "Blockers" };
    static readonly nint[] debugMasks = new nint[DebugViewFlags.Length];   // RA01: each switch's mask (0 = not found)
    static ReadOnlySpan<byte> PrepareStart => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08 }; // RA01: mov rax, rsp; mov [rax+8], rbx
    static ReadOnlySpan<byte> FlagStubStart => new byte[] { 0x48, 0x83, 0xEC, 0x38, 0x48, 0x8D, 0x0D }; // RA01: sub rsp, 38h; lea rcx, [name]
    static ReadOnlySpan<byte> FlagStubStore => new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x20, 0xE8 };       // RA01: mov [rsp+20h], rcx; call (the name's length)
    const int PrepareFromSetup = 0x23C, ViewInJob = 0x10, FlagsAt = 0x230, FlagWords = 32, MaxViews = 4;
    static volatile int debugView;                                          // RA01: the chosen row (0 = off); not saved
    static int tracked, nextSlot;                                           // RA01: how many views carry our bits; which slot to reuse when all are taken
    static readonly nint[] views = new nint[MaxViews];                      // RA01: views we added bits to (only written to again when the game hands them over)
    static readonly int[] viewChoice = new int[MaxViews];                   // RA01: which debug view each one carries
    static readonly ulong[] added = new ulong[MaxViews * FlagWords];        // RA01: per tracked view, the bits WE turned on (to put back)
    static readonly object viewLock = new();
    static bool loggedApply;

    // RA01: the chosen debug view (0 = off) for the menu. Cost - CPU: extremely low (one read); GPU: none.
    public static int DebugView => debugView;

    // RA01: chooses a debug view (0 = off) and logs it. Cost - CPU: extremely low (a store and a log line); GPU: none.
    public static void SetDebugView(int which)
    {
        which = Math.Clamp(which, 0, DebugViewFlags.Length - 1);
        if (which == debugView) return;
        debugView = which;
        loggedApply = false;
        Log.Write($"RA01 culling lab: engine debug view {DebugViewNames[which]}{(which > 0 ? $" ({DebugViewFlags[which]}, {(debugMasks[which] != 0 ? "found" : "NOT found - nothing will show")})" : "")}");
    }

    // RA01: finds the six switches' masks by name and puts the detour on the order form. Logs what it found. Any doubt = no detour.
    // Cost - CPU: medium (once: see the class block above); GPU: none.
    static void InstallDebugViews(nint setup)
    {
        List<nint> stubs = ExeScan.FindAll(FlagStubStart);
        int count = 0;
        for (int i = 1; i < DebugViewFlags.Length; i++) if ((debugMasks[i] = FindFlagMask(DebugViewFlags[i], stubs)) != 0) count++;
        nint prepare = setup + PrepareFromSetup;
        bool hooked = count > 0 && CrosshairProbe.Hook(prepare, PrepareStart, (nint)(delegate* unmanaged<nint, void>)&OnPrepare, "occlusion order form (engine debug views)");
        Log.Write($"RA01 culling lab: engine debug views - {count} of {DebugViewFlags.Length - 1} switches found, {(hooked ? "ready" : "NOT ready (order form not hooked)")}");
        if (!hooked) for (int i = 0; i < debugMasks.Length; i++) debugMasks[i] = 0;
    }

    // RA01: one switch's mask: the name string (whole - a zero before it), the one registration stub whose first lea points at it, and the next lea in that stub
    // (the switch's record; its mask starts 8 bytes in). Checked: the mask must have at least one bit set (the game fills it in at start-up). 0 if not found.
    // Cost - CPU: low (one string search plus a walk over about 900 stubs); GPU: none.
    static nint FindFlagMask(string name, List<nint> stubs)
    {
        foreach (nint text in FindString(name))
        {
            if (*(byte*)(text - 1) != 0) continue;
            foreach (nint stub in stubs)
            {
                if (stub + 11 + *(int*)(stub + 7) != text) continue;
                if (!new ReadOnlySpan<byte>((byte*)stub + 11, FlagStubStore.Length).SequenceEqual(FlagStubStore)) continue;
                for (int at = 16; at < 48; at++)
                {
                    byte* p = (byte*)stub + at;
                    if (p[0] != 0x48 || p[1] != 0x8D || p[2] != 0x0D) continue;
                    ulong* mask = (ulong*)(stub + at + 7 + *(int*)(p + 3) + 8);
                    for (int w = 0; w < FlagWords; w++) if (mask[w] != 0) return (nint)mask;
                    return 0;
                }
            }
        }
        return 0;
    }

    // RA01: the detour on the order form, on a game job thread, before its own code runs. Only ever writes to the view the game hands it right now (never to a
    // remembered address, which may be gone): if that view still carries bits from an earlier choice, they are taken off first; then, while a debug view is
    // chosen, its mask is ORed in and the bits that were off before are remembered. Never throws back into the game.
    // Cost - CPU: extremely low (one read while off and nothing to take back; 32 ORs plus a 4-entry look-up per view per frame while on); GPU: none.
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void OnPrepare(nint job)
    {
        int want = debugView;
        if (want == 0 && Volatile.Read(ref tracked) == 0) return;
        try
        {
            nint view = *(nint*)(job + ViewInJob);
            if (view < 0x10000) return;
            lock (viewLock)
            {
                int slot = Array.IndexOf(views, view);
                ulong* flags = (ulong*)(view + FlagsAt);
                if (slot >= 0 && viewChoice[slot] != want)
                {
                    for (int w = 0; w < FlagWords; w++) { flags[w] &= ~added[slot * FlagWords + w]; added[slot * FlagWords + w] = 0; }
                    views[slot] = 0; tracked--; slot = -1;
                }
                if (want == 0 || debugMasks[want] == 0) return;
                if (slot < 0)
                {
                    slot = Array.IndexOf(views, (nint)0);
                    if (slot < 0) { slot = nextSlot; nextSlot = (nextSlot + 1) % MaxViews; tracked--; Array.Clear(added, slot * FlagWords, FlagWords); } // RA01: full: forget the oldest (never written to again)
                    views[slot] = view; viewChoice[slot] = want; tracked++;
                }
                ulong* mask = (ulong*)debugMasks[want];
                for (int w = 0; w < FlagWords; w++) { added[slot * FlagWords + w] |= mask[w] & ~flags[w]; flags[w] |= mask[w]; }
                if (!loggedApply) { loggedApply = true; Log.Write($"RA01 culling lab: engine debug view {DebugViewNames[want]} switched on in view 0x{view:X}"); }
            }
        }
        catch { }
    }

    // RA01: M21-7 - RENDER CAMERA NEAR PLANE TEST (Dev slider "Camera near plane (test)"). The rooms vanish depending on where you stand and how wide the view is,
    // RA01: not where you look - as if "which room is the camera in?" were asked with a bubble around the lens sized from the near plane (the glass right in front
    // RA01: of it). The real near plane is on the render camera at +0x40 (far at +0x44, field of view in degrees at +0x20), read by its matrix update (2.31:
    // RA01: exe+0x1E412C) to build the lens. That update is found from the order form above, which calls it first at +0x81 (checked: an E8 call whose target starts
    // RA01: with the 7 bytes below), and a detour at its start puts the slider's value at +0x40 - only for cameras whose field of view is within 1 degree of the
    // RA01: player's picture and whose near plane looks like one (0.005 to 0.5 m): the player's camera and its copies, never a light's shadow camera.
    // RA01: Back at 0 the game's own value is put back on any player camera still carrying ours.
    // RA01: The first 5 different cameras it sees are logged (field of view, near, far), and while the slider is on, every 2 s how many it changed.
    // RA01: Builder picture: holding the plywood sheet closer to your face - same view, smaller sheet. Too small a value can make far surfaces flicker - a test.
    // RA01: Cost - CPU: extremely low (a few reads per camera matrix update, tens to hundreds a frame; a store when it matches); GPU: none.
    static ReadOnlySpan<byte> MatrixUpdateStart => new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08 }; // RA01: mov rax, rsp; mov [rax+8], rbx
    const int MatrixCallAt = 0x81, FovAt = 0x20, NearAt = 0x40, FarAt = 0x44;
    static volatile float nearTest;      // RA01: the slider, metres; 0 = the game's own (nothing written); not saved
    static int nearChanged, nearSeenCount;
    static float gameNear, lastWritten;  // RA01: the game's own near plane (to put back when the slider goes to 0) and the last value we wrote
    static long nextNearReport;
    static readonly float[] nearSeen = new float[5];

    // RA01: the slider's value (0 = off) and setting it. Cost - CPU: extremely low; GPU: none.
    public static float NearTest => nearTest;
    public static void SetNearTest(float metres) => nearTest = metres; // RA01: no log here (a slider drag sends hundreds); OnMatrixUpdate reports every 2 s

    // RA01: finds the matrix update from the order form's first call and puts the detour on it. Logs what it did; any doubt = no detour.
    // Cost - CPU: extremely low (once: a few byte compares and the detour); GPU: none.
    static void InstallNearTest(nint prepare)
    {
        byte* call = (byte*)prepare + MatrixCallAt;
        if (call[0] != 0xE8) { Log.Write("RA01 culling lab: near plane test NOT installed (the order form's call isn't where it was)"); return; } // RA01: the order form's own first bytes are not checked here - the debug views' detour already sits on them
        nint update = (nint)(call + 5 + *(int*)(call + 1));
        if (!new ReadOnlySpan<byte>((byte*)update, MatrixUpdateStart.Length).SequenceEqual(MatrixUpdateStart)) { Log.Write("RA01 culling lab: near plane test NOT installed (the matrix update doesn't start as expected)"); return; }
        CrosshairProbe.Hook(update, MatrixUpdateStart, (nint)(delegate* unmanaged<nint, void>)&OnMatrixUpdate, "render camera matrix update (near plane test)");
    }

    // RA01: the detour, on whichever thread updates a camera: log new cameras, and while the slider is on put its value into the player camera's near plane.
    // Never throws back into the game. Cost - CPU: extremely low (see the block above); GPU: none.
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void OnMatrixUpdate(nint camera)
    {
        try
        {
            if (camera < 0x10000) return;
            float fov = *(float*)(camera + FovAt), near = *(float*)(camera + NearAt), want = nearTest;
            if (nearSeenCount < nearSeen.Length && fov > 0 && Array.IndexOf(nearSeen, fov) < 0)
            {
                int slot = Interlocked.Increment(ref nearSeenCount) - 1;
                if (slot < nearSeen.Length) { nearSeen[slot] = fov; Log.Write($"RA01 culling lab: render camera seen - field of view {fov:F2}, near {near:G4} m, far {*(float*)(camera + FarAt):G5} m (picture fov {CameraHook.Fov:F2})"); }
            }
            bool player = MathF.Abs(fov - CameraHook.Fov) < 1f && near >= 0.005f && near <= 0.5f;
            if (!(want > 0))
            {
                if (player && lastWritten > 0 && near == lastWritten && gameNear > 0) *(float*)(camera + NearAt) = gameNear; // RA01: off: put the game's own back where ours still sits
                return;
            }
            if (player)
            {
                if (near != lastWritten && near != want) gameNear = near;                 // RA01: remember the game's own value (not one of ours)
                *(float*)(camera + NearAt) = want; lastWritten = want; Interlocked.Increment(ref nearChanged);
            }
            long now = Environment.TickCount64;
            if (now < nextNearReport) return;
            nextNearReport = now + 2000;
            Log.Write($"RA01 culling lab: near plane test {want:F3} m - {Interlocked.Exchange(ref nearChanged, 0)} camera updates changed in the last 2 s (this one: field of view {fov:F2}, near {near:G4})");
        }
        catch { }
    }

    static string Bool(int which) => found[which] != 0 ? (*(byte*)(found[which] + ValueAt) != 0 ? "on" : "off") : "?";

    // RA01: one setting's object: every copy of the name and group strings in the exe, then the place in the data where a name pointer sits right before a group
    // pointer (+0x08 and +0x10 of the object). 0 if not exactly one object matches.
    // Cost - CPU: medium (two string searches and one or a few 16-byte searches over the data sections); GPU: none.
    static nint FindSetting(string group, string name)
    {
        List<nint> names = FindString(name), groups = FindString(group), hits = new();
        byte* pair = stackalloc byte[16];
        foreach (nint n in names)
            foreach (nint g in groups)
            {
                *(long*)pair = n; *(long*)(pair + 8) = g;
                hits.AddRange(ExeScan.FindAll(new ReadOnlySpan<byte>(pair, 16), code: false));
            }
        hits.RemoveAll(h => (h & 7) != 0);           // RA01: a real object's pointers are 8-byte aligned
        return hits.Count == 1 ? hits[0] - 8 : 0;
    }

    // RA01: every place in the exe's data where 'text' ends a C string (followed by a zero byte). The start is not required to follow a zero: the
    // compiler may share one string's tail with another ("Occlusion" inside "AmbientOcclusion"); the pair search above sorts out the real one.
    // Cost - CPU: low (one search of the data sections); GPU: none.
    static List<nint> FindString(string text) => ExeScan.FindAll(Encoding.ASCII.GetBytes(text + "\0"), code: false);

    // RA01: the near plane right now, in metres (the game's 0.2 if not found). Cost - CPU: extremely low (one read); GPU: none.
    public static float GetNearPlane() => found[NearPlane] != 0 ? *(float*)(found[NearPlane] + ValueAt) : 0.2f;

    // RA01: sets the occlusion camera's near plane (0.01 to 0.5 m), live. Cost - CPU: extremely low (one write and a log line); GPU: none.
    public static void SetNearPlane(float metres)
    {
        if (found[NearPlane] == 0) return;
        *(float*)(found[NearPlane] + ValueAt) = Math.Clamp(metres, 0.01f, 0.5f);
        long now = Environment.TickCount64;
        if (now < nextNearLog) return;
        nextNearLog = now + 500;
        Log.Write($"RA01 culling lab: occlusion near plane {metres:F2} m");
    }

    // RA01: one of the on/off settings (FullTest, SharedViews, Streaming, LodRenderCamera) right now, and setting it, live. Unknown reads as the game's default.
    // Cost - CPU: extremely low (one byte read or write); GPU: none.
    public static bool GetSwitch(int which, bool fallback) => found[which] != 0 ? *(byte*)(found[which] + ValueAt) != 0 : fallback;
    public static void SetSwitch(int which, bool on)
    {
        if (found[which] == 0) return;
        *(byte*)(found[which] + ValueAt) = on ? (byte)1 : (byte)0;
        Log.Write($"RA01 culling lab: {Wanted[which].Group}/{Wanted[which].Name} {(on ? "on" : "off")}");
    }

    // RA01: whether the blocker picture is being built (the gate jump is the game's own, 0F 84). Cost - CPU: extremely low (one read); GPU: none.
    public static bool OcclusionOn => gate == 0 || *(byte*)gate == 0x0F;

    // RA01: turns the building of the blocker picture on (the game's own jump) or off (always skip), by rewriting the jump's first two bytes. The jump's target stays
    // the same (90 E9 = no-op, then a 5-byte jump ending exactly where the 6-byte one did). Cost - CPU: extremely low (two protection changes and a 2-byte write);
    // GPU: none itself (with it off the game draws more - see the class comment).
    public static void SetOcclusion(bool on)
    {
        if (gate == 0 || OcclusionOn == on) return;
        if (!VirtualProtect(gate, 2, 0x40, out uint old)) return;      // RA01: 0x40 = PAGE_EXECUTE_READWRITE
        *(ushort*)gate = on ? (ushort)0x840F : (ushort)0xE990;          // RA01: little-endian: 0F 84 or 90 E9
        VirtualProtect(gate, 2, old, out _);
        FlushInstructionCache(GetCurrentProcess(), gate, 2);
        Log.Write($"RA01 culling lab: occlusion (blocker walls) {(on ? "ON - the game's own" : "OFF - nothing is hidden behind walls")}");
    }
}
