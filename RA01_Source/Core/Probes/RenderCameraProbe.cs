using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M26-0 - THE RENDER CAMERA PROBE (LOG ONLY, Dev row "Render camera probe (log)"). The first step of the frame lag's real fix (Option A, Rob 2026-10-08).
// Today each picture is paired with the head pose it was drawn from by COUNTING (CameraHook: picture N goes with camera build N + a gap) and the gap drifts
// after menus and stutters - so "Frame lag" drifts. This probe reads the camera the RENDER side actually draws with, and logs which of our camera builds it is.
// WHERE: exe+0x623FD8 (2.31) builds the render view for each pass of a frame (found in the M21 culling hunt: the call chain on redDispatcher4). Its third
// argument (r8) is the frame's render data: [r8+0x18] the frame's camera - position x, y, z as whole numbers of 1/131072 m at +0x00, the rotation at +0x10
// (the same layout as the setup block CameraHook steers, copied there by exe+0x4E4030) - and [r8+0x20] the frame's number.
// HOW: a tiny MACHINE-CODE detour (no C# on the render threads) copies those into a ring of 512 slots and jumps back. At every Present the probe takes the
// newest frame's camera (and the one before it), finds the camera build with exactly that position (CameraHook.FindBuild - our head pose makes every build's
// position unique), and counts how far that build is from the one the counting picked at Frame lag 0. If one of the two is always the same distance away,
// that distance IS the frame lag, measured instead of guessed - and the next step labels every picture from it. Nothing in the game is changed.
// M26-0 RESULT (Rob 2026-10-08 04:20): 100% of render frames matched a build exactly; the newest one at each Present was the counting's lag-0 build in
// 94-99% of Presents and the next build in the rest - and Frame lag 0 always looked right. So the picture at a Present IS the newest render frame, and the
// 1-6% the counting got wrong are the random jumping Rob saw at lag 0 (fixed by hand with 1, then back to 0).
// M26-1 - EXACT PICTURE PAIRING (Video tab "Exact picture pairing", on by default): now installed at start-up and read at EVERY Present. The newest render
// frame's build gives PresentStamp - the exact head pose that picture was drawn with - and FrameCopy / HudLayer use it instead of the counting. When there
// is no exact match (a menu, a loading screen, a camera cut, the hook not found), the counting and Frame lag take over as before. The Dev row only adds
// the 5 s statistics lines; a 30 s summary is always logged.
// Builder picture: instead of counting boxes on two conveyor belts, we read the shipping label printed on each box.
// Cost - CPU: extremely low on the render threads (about 25 instructions per render view, about 110 views a frame); the reading: low (a short loop over
// the new ring slots and a 64-build search per Present, 100-200 a second; a log line every 30 s, every 5 s with the Dev row on); GPU: none.
internal static unsafe class RenderCameraProbe
{
    // RA01: the first 24 bytes of exe+0x623FD8 (2.31, unique in the exe): mov [rsp+8],rbx / push rdi / sub rsp,20h / mov [rcx+18h],r8 / xorps xmm0,xmm0 /
    // RA01: mov rax,[r8+0C3B00h]. Only the first instruction (5 bytes) is replaced.
    static ReadOnlySpan<byte> Site => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x4C, 0x89, 0x41, 0x18, 0x0F, 0x57, 0xC0, 0x49, 0x8B, 0x80, 0x00, 0x3B, 0x0C, 0x00 };
    const int Replaced = 5;

    // RA01: the detour, checked with a disassembler (capstone) before delivery. Only rax, r10, r11 and the flags change - none carry anything into this function.
    // RA01:  00 mov r10,[r8+18h] / test r10,r10 / je 6C                      - the frame's camera (skip if none)
    // RA01:  09 mov r11,<ring> (8 bytes at +0x0B)
    // RA01:  13 mov eax,1 / lock xadd [r11],eax / and eax,1FFh / shl eax,6 / add rax,r11   - the next slot (64 bytes each, after a 0x40-byte header)
    // RA01:  28 mov qword [rax+78h],0                                        - "being written"
    // RA01:  30 four 8-byte copies [r10+0..18h] -> [rax+40h..58h]            - position x, y, z (+ a spare word), rotation x, y, z, w
    // RA01:  4F mov r11d,[r8+20h] / mov [rax+60h],r11                        - the frame's number
    // RA01:  57 mov [rax+68h],r10 / mov r11,gs:[48h] / mov [rax+70h],r11     - the camera's address and the thread's id
    // RA01:  68 mov byte [rax+78h],1                                         - "done" (x86 stores land in order, so a reader that sees 1 sees the rest)
    // RA01:  6C mov [rsp+8],rbx / jmp [rip+0] -> the 8-byte address at +0x77 (the function, 5 bytes in)
    static ReadOnlySpan<byte> Detour => new byte[]
    {
        0x4D, 0x8B, 0x50, 0x18, 0x4D, 0x85, 0xD2, 0x74, 0x63, 0x49, 0xBB, 0, 0, 0, 0, 0, 0, 0, 0, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x41, 0x0F, 0xC1, 0x03,
        0x25, 0xFF, 0x01, 0x00, 0x00, 0xC1, 0xE0, 0x06, 0x4C, 0x01, 0xD8, 0x48, 0xC7, 0x40, 0x78, 0x00, 0x00, 0x00, 0x00, 0x4D, 0x8B, 0x1A, 0x4C, 0x89, 0x58, 0x40,
        0x4D, 0x8B, 0x5A, 0x08, 0x4C, 0x89, 0x58, 0x48, 0x4D, 0x8B, 0x5A, 0x10, 0x4C, 0x89, 0x58, 0x50, 0x4D, 0x8B, 0x5A, 0x18, 0x4C, 0x89, 0x58, 0x58,
        0x45, 0x8B, 0x58, 0x20, 0x4C, 0x89, 0x58, 0x60, 0x4C, 0x89, 0x50, 0x68, 0x65, 0x4C, 0x8B, 0x1C, 0x25, 0x48, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x70,
        0xC6, 0x40, 0x78, 0x01, 0x48, 0x89, 0x5C, 0x24, 0x08, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0
    };
    const int RingAt = 0x0B, BackAt = 0x77;
    const int Slots = 512, SlotBytes = 64, HeaderBytes = 0x40;
    const int Span = 4;                           // RA01: offsets -4..+4 are counted one by one; anything further is "far"
    const long ReportMs = 5000;

    static nint ring;
    static int installed;
    static uint next;
    static long nextReport;
    static int presents, views, frames, exactNewest, exactPrevious;
    static float missCm;
    static int misses;
    static readonly int[] newestAt = new int[2 * Span + 2], previousAt = new int[2 * Span + 2]; // RA01: offset + Span; the last cell = far or no match
    static readonly HashSet<long> threads = new();
    static long presentStamp;                     // RA01: M26-1 - the exact pose stamp of the picture at this Present (0 = not known: use the counting)
    static int pairedExact, pairedCounting, disagreed;   // RA01: M26-1 - for the 30 s summary
    static long nextSummary;

    // RA01: M26-1 - the exact pose stamp of the picture being presented right now, or 0 when it isn't known (then the caller uses CameraHook's counting).
    // RA01: Present thread only (set by OnPresent just before FrameCopy reads it). Cost - CPU: extremely low (a read); GPU: none.
    public static long PresentStamp => Tuning.ExactPairing ? presentStamp : 0;

    // RA01: M26-1 - true while exact pairing is on and gave an exact pose within the last half second (any thread). Cost - CPU: extremely low (a read); GPU: none.
    public static bool Working => Tuning.ExactPairing && Environment.TickCount64 - Volatile.Read(ref lastExactAt) < 500;
    static long lastExactAt;

    // RA01: the Dev row: on = the 5 s statistics lines; off = only the summary every 30 s (M26-1: the hook is installed at start-up). Not saved.
    public static bool On { get => on; set { on = value; Log.Write(value ? "RA01 render camera probe: ON - play normally for a minute, walk through a menu or a loading screen, then turn it off and send the log" : "RA01 render camera probe: off"); } }
    static volatile bool on;

    // RA01: finds the site, makes the ring and the detour, writes the jump. Any problem: logged, nothing hooked (the counting carries on alone). M26-1 - called
    // once at start-up by Entry. Cost - CPU: low, once (one byte search of the exe, a 5-byte write); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = HookRegistry.FindAll(HookRegistry.RenderCamera, Site);   // RA01: M43 - reported to the Rehooker
            if (found.Count != 1) { Log.Write($"RA01 render camera probe NOT installed: its site was found {found.Count} times (expected 1)"); return; }
            nint site = found[0];
            ring = VirtualAlloc(0, (nuint)(HeaderBytes + Slots * SlotBytes), 0x3000, 0x04);
            nint detour = ring != 0 ? CameraHook.AllocateNear(site) : 0;
            if (detour == 0) { Log.Write("RA01 render camera probe NOT installed: no memory for its ring or detour"); HookRegistry.Off(HookRegistry.RenderCamera, "no memory for its ring or detour"); return; }
            byte* code = (byte*)detour;
            Detour.CopyTo(new Span<byte>(code, Detour.Length));
            *(long*)(code + RingAt) = ring;
            *(long*)(code + BackAt) = site + Replaced;
            next = (uint)Volatile.Read(ref *(int*)ring);
            if (!CameraHook.WriteJump(site, detour)) { Log.Write("RA01 render camera probe NOT installed: the jump could not be written"); HookRegistry.Off(HookRegistry.RenderCamera, "the jump could not be written"); return; }
            Log.Write($"RA01 render camera probe: hooked the render view builder at RVA 0x{site - ExeScan.Base:X} (2.31: 0x623FD8)");
        }
        catch (Exception e) { Log.Write($"RA01 render camera probe failed: {e.Message}"); }
    }

    // RA01: at every Present, right after CameraHook counted it (PresentHook). Takes the new ring slots, finds the newest frame's camera and the frame before's,
    // matches both to our builds, counts the offsets from the counting's lag-0 build. Never throws.
    // Cost - CPU: low while on (see the class comment), a single read while off; GPU: none.
    public static void OnPresent()
    {
        presentStamp = 0;
        if (ring == 0) return;
        try
        {
            uint count = (uint)Volatile.Read(ref *(int*)ring);
            if (count - next > Slots) next = count - Slots;
            int newestFrame = -1, previousFrame = -1;
            int nx = 0, ny = 0, nz = 0, px = 0, py = 0, pz = 0;
            for (; next != count; next++)
            {
                byte* slot = (byte*)ring + HeaderBytes + (next & (Slots - 1)) * SlotBytes;
                if (slot[0x38] != 1) continue;                                     // RA01: still being written
                int frame = *(int*)(slot + 0x20);
                views++;
                if (on && threads.Count < 64) threads.Add(*(long*)(slot + 0x30));
                if (frame == newestFrame) continue;
                previousFrame = newestFrame; px = nx; py = ny; pz = nz;
                newestFrame = frame; nx = *(int*)slot; ny = *(int*)(slot + 4); nz = *(int*)(slot + 8);
                frames++;
            }
            if (newestFrame < 0) { Summary(); return; }                           // RA01: nothing new drawn since the last Present (a menu): the counting decides
            int build = CameraHook.FindBuild(nx, ny, nz, out _);                   // RA01: M26-1 - the newest render frame = the picture being presented
            int wanted = CameraHook.WantedBuild(0);
            if (build != 0)
            {
                presentStamp = CameraHook.StampOfBuild(build);
                if (presentStamp != 0) { Volatile.Write(ref lastExactAt, Environment.TickCount64); pairedExact++; if (wanted > 0 && build != wanted - Tuning.FrameLag) disagreed++; }
                else pairedCounting++;
            }
            else pairedCounting++;
            if (on && wanted > 0)
            {
                presents++;
                Count(newestAt, nx, ny, nz, wanted, ref exactNewest);
                if (previousFrame >= 0) Count(previousAt, px, py, pz, wanted, ref exactPrevious);
                Report();
            }
            Summary();
        }
        catch (Exception e) { ring = 0; presentStamp = 0; Log.Write($"RA01 render camera: stopped ({e.Message}) - the counting pairs the pictures from now on"); }
    }

    // RA01: M26-1 - every 30 s: how many pictures got their exact pose, how many fell back to the counting, and how often the exact pose differed from what the
    // counting (at the current Frame lag) would have given - each of those was a jump the old way. Cost - CPU: extremely low (a line every 30 s); GPU: none.
    static void Summary()
    {
        long now = Environment.TickCount64;
        if (now < nextSummary) return;
        nextSummary = now + 30000;
        int total = pairedExact + pairedCounting;
        if (total == 0) return;
        Log.Write($"RA01 render camera: {total} pictures in 30 s - {pairedExact} paired exactly ({100 * pairedExact / total}%), {pairedCounting} by counting; " +
                  $"{disagreed} of the exact ones differed from the counting at frame lag {Tuning.FrameLag} (each was a jump before){(Tuning.ExactPairing ? "" : " - exact pairing is OFF")}");
        pairedExact = pairedCounting = disagreed = 0;
    }

    // RA01: one camera matched to a build and its offset from the counting's lag-0 build counted. Cost - CPU: extremely low (one 64-build search); GPU: none.
    static void Count(int[] at, int x, int y, int z, int wanted, ref int exact)
    {
        int build = CameraHook.FindBuild(x, y, z, out float nearestCm);
        if (build == 0) { at[^1]++; if (nearestCm >= 0) { missCm += nearestCm; misses++; } return; }
        exact++;
        int offset = build - wanted;
        at[Math.Abs(offset) <= Span ? offset + Span : at.Length - 1]++;
    }

    // RA01: every ReportMs: one line with both offset tallies, then a fresh start. Offset -k means "the counting's Frame lag k picks this camera".
    // Cost - CPU: extremely low (a string every 5 s); GPU: none.
    static void Report()
    {
        long now = Environment.TickCount64;
        if (now < nextReport) return;
        nextReport = now + ReportMs;
        if (presents == 0) return;
        string Row(int[] at) => string.Join(" ", Enumerable.Range(0, 2 * Span + 1).Where(i => at[i] > 0).Select(i => $"{i - Span:+0;-0;0}:{at[i]}")) + (at[^1] > 0 ? $" none/far:{at[^1]}" : "");
        Log.Write($"RA01 render camera probe: {presents} Presents, {views} render views, {frames} render frames, {threads.Count} render threads; frame lag now {Tuning.FrameLag}. " +
                  $"NEWEST frame's camera = build (lag-0 build + k): {Row(newestAt)} ({100 * exactNewest / presents}% exact); " +
                  $"FRAME BEFORE: {Row(previousAt)} ({100 * exactPrevious / presents}% exact)" + (misses > 0 ? $"; unmatched were {missCm / misses:F1} cm from the nearest build" : ""));
        presents = views = frames = exactNewest = exactPrevious = misses = 0; missCm = 0;
        Array.Clear(newestAt); Array.Clear(previousAt); threads.Clear();
    }
}
