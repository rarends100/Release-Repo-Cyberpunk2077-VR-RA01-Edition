using System.Diagnostics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M6-6 - a one-off, READ-ONLY heap hunt for the player's skeleton, started with F11 (see Keys.cs; not F9, which is Cyberpunk's own quickload key). It exists purely to answer the
// same question M4's camera hook already answered for the camera, now asked of the skeleton instead: is there a block of memory, rewritten every frame, that holds a real bone's
// position? It never writes to the game's memory, only reads it. Any address it finds is only good for this one run of the game (Windows moves everything around on every launch,
// called ASLR) - the point of this pass is to prove the data is really there and see roughly where it sits, which is the first half of building a durable pointer chain to it later.
// how it works: on F11, every committed, private, writable block of the game's OWN heap (never its code or read-only assets) is swept once for float triplets sitting close to
// where the engine's own camera - which shares the skeleton's coordinate space, see CameraHook.TryReadEnginePosition - says the body is right now. That is the candidate pool,
// likely large and mostly coincidence. Then, about once a second for the next several seconds while you walk a slow circle (the log says when), each surviving candidate is
// watched for two things: does it stay within a generous, body-sized leash of wherever the camera currently is, and does its own direction of travel reverse itself more than a
// couple of times over the whole walk. M6-15 - camera position on its own was the wrong thing to compare against for this: the camera is its own object, not a bone, so anything
// found by matching the camera's movement exactly was always going to be whatever the camera itself is glued to, never a real limb. A leg swings back and forth as you walk; a
// camera attach point, or anything just riding along on the body, does not - so reversal, not exact matching, is what actually tells a bone apart from the rest.
// safety: M6-9 - every read, in the bulk sweep and in the narrowing phase, goes through ReadProcessMemory (see Win32.cs) instead of a raw pointer. An earlier version checked a
// block was safe with VirtualQuery and then read it directly - and it crashed twice in testing, at two different points, because checking and reading were still two separate
// steps: the OS can pause our thread for an unpredictable stretch between them (worse with several of our own threads competing with the game's many threads for the same cores),
// and shrinking the gap between the two steps never fully closes it. ReadProcessMemory does the check and the copy as one step - if the memory is not there any more, that one
// call just returns false, nothing after it can go wrong, and there is no gap left for bad timing to find. Net risk: very low, not literally zero (an access violation cannot be
// caught in .NET, so prevention like this is the only real safeguard) - the unlucky case is still the game crashing, not anything being corrupted, and pressing F11 again after a
// relaunch costs nothing but time.
// Cost - CPU: the one-off sweep is genuinely heavy - walking however much heap the game currently has committed, which for a game like this can be many gigabytes, so even split
// across several threads it can still take anywhere from several seconds to a couple of minutes; it runs on its own threads (never the game's own) so it never blocks a game
// frame, and reports its own progress in the log and the Dev Only Tools tab while it works, so "still going" and "stuck" never look the same. The narrowing phase afterward only
// touches its own small candidate list, about once a second, extremely low; idle the rest of the time. GPU: none.
internal static unsafe class BoneFinder
{
    const uint Committed = 0x1000;                    // RA01: MEM_COMMIT
    const uint Private = 0x20000;                     // RA01: MEM_PRIVATE - the game's own heap, not a mapped file, not its code or read-only assets
    const uint Writable = 0x04 | 0x08 | 0x40 | 0x80;   // RA01: PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY
    const uint Blocked = 0x100 | 0x01;                 // RA01: PAGE_GUARD | PAGE_NOACCESS

    const float SearchRadius = 2f;                     // RA01: metres - M6-15: wide enough to reach down to the feet and out to the hands, not just the head/neck the old 1m net was tuned for
    const float MaxLeash = 2.5f;                       // RA01: M6-15 - how far a candidate may sit from wherever the camera currently is before it is dropped as too far from the body to plausibly be a bone - generous enough for a stepping foot or a reaching hand, tight enough to exclude someone standing a couple of paces away
    const float MinMovement = 0.15f;                   // RA01: M6-15 - metres travelled in total over the whole window before a candidate counts as having moved for real, not just sat there
    const int MinSwings = 3;                           // RA01: M6-15 - direction reversals required over the window - a real limb racks these up every stride; something just riding along a walked curve gets, at most, one or two
    static readonly long NarrowWindowTicks = Stopwatch.Frequency * 12; // RA01: M6-15 - twelve seconds, long enough to walk a comfortable, easy circle and get several strides in without rushing
    static readonly long SampleTicks = Stopwatch.Frequency;           // RA01: check survivors about once a second
    const int MaxLogged = 1000;                                        // RA01: M6-17 - FinishNarrowing's print cap. This only ever runs once per hunt, not per frame, so logging the whole list costs nothing worth guarding against; 1000 is just a hard ceiling against a truly broken run flooding the file

    // RA01: X/Y/Z is the value AT THE SWEEP, kept only for the log line at the end. M6-15 added the running state Track() needs to judge a whole walk: SwingCount/TotalDistance are what
    // FinishNarrowing judges against once the window closes. M6-16: LastRel/X/Y/Z and PrevDelta are measured relative to wherever the camera is each sample, not the candidate's raw
    // position in the world - see Track() for why. LastX/Y/Z stays as the plain last-seen position, purely for that final log line. M6-13: internal (not private) so PointerFinder can
    // read what a hunt left behind.
    internal struct Candidate
    {
        public nint Address;
        public float X, Y, Z;
        public float LastX, LastY, LastZ;
        public float LastRelX, LastRelY, LastRelZ;
        public float PrevDeltaX, PrevDeltaY, PrevDeltaZ;
        public bool HasPrevDelta;
        public float TotalDistance;
        public int SwingCount;
    }

    static List<Candidate> candidates = new();
    static float baseX, baseY, baseZ;
    static long narrowUntil, nextSampleAt;
    static bool sweeping, narrowing, finished;
    static int lastSurvivorCount;
    static long totalBytesToScan, bytesScanned; // RA01: M6-7 - how big this sweep is and how much of it is done; the worker threads in Sweep() add to bytesScanned, Status() reads both for a live progress line
    static readonly nint CurrentProcess = GetCurrentProcess(); // RA01: M6-9 - this process's own pseudo-handle, needed by ReadProcessMemory even when reading our own memory; fetched once, never changes

    // RA01: M6-13 - whatever the last completed hunt left standing, for PointerFinder to search for a durable way back to. Safe to read any time nothing is actively narrowing: Poll
    // RA01: only ever shrinks this list in place while narrowing is true, and Start() only replaces it with a fresh one right as a brand new hunt begins.
    internal static IReadOnlyList<Candidate> Survivors => candidates;

    // RA01: F11 - starts a fresh hunt. Ignored if one is already running.
    // Cost - CPU: extremely low itself (a few field resets and a Thread.Start); the heavy work happens on the new thread, not here; GPU: none.
    public static void Start()
    {
        if (Volatile.Read(ref sweeping) || Volatile.Read(ref narrowing)) { Log.Write("RA01 bone hunt: already running, ignored"); return; }
        if (!CameraHook.TryReadEnginePosition(out baseX, out baseY, out baseZ)) { Log.Write("RA01 bone hunt: no engine camera yet - get into the game first"); return; }

        // RA01: M6-12 - the previous hunt's own candidate list lives in this same process, right alongside the game's own memory - and every entry in it is, by definition, a float
        // RA01: triplet that was close to THAT hunt's own start point. Back-to-back hunts in roughly the same spot mean a leftover list is bait the next sweep is almost guaranteed to
        // RA01: catch all over again, on top of whatever is genuinely there - and each catch is bigger than the last, since it now drags along every earlier hunt's leftovers too.
        // RA01: Handing the field a fresh, empty list makes the old one pure garbage, then a forced, compacting collect reclaims what it was sitting in right now instead of whenever
        // RA01: .NET next gets around to it - so the sweep about to start only ever finds memory that is genuinely still there, not a pile of its own old printouts.
        Volatile.Write(ref candidates, new List<Candidate>());
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        Volatile.Write(ref sweeping, true);
        Volatile.Write(ref totalBytesToScan, 0);
        Volatile.Write(ref bytesScanned, 0);
        Log.Write($"RA01 bone hunt: sweeping the heap near ({baseX:F2}, {baseY:F2}, {baseZ:F2}) - this can take anywhere from several seconds to a couple of minutes depending how much memory the game has committed; progress shows here and in the Dev Only Tools tab");
        new Thread(Sweep) { IsBackground = true, Name = "RA01 bone hunt" }.Start();
    }

    // RA01: M6-7 - the heavy one-off sweep, on its own threads so it never blocks a game frame. Two passes: EnumerateRegions first just lists every committed, private, writable block
    // (cheap - free and reserved space is skipped in one check each, however large), then those blocks are handed out across a few worker threads that do the real byte-by-byte
    // search in parallel, each into its own list so nothing needs to be locked between them. While they work, this thread logs progress every couple of seconds instead of going quiet.
    static void Sweep()
    {
        List<(nint Address, nint Size)> regions = EnumerateRegions();
        long total = 0;
        foreach (var region in regions) total += region.Size;
        Volatile.Write(ref totalBytesToScan, total);

        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6); // RA01: half the logical cores, at least 2 and at most 6 - fast, but always leaves the game itself room to breathe
        List<(nint Address, nint Size)>[] assigned = new List<(nint Address, nint Size)>[workers];
        long[] load = new long[workers];
        for (int w = 0; w < workers; w++) assigned[w] = new();

        // RA01: greedy load-balancing: biggest blocks handed out first, always to whichever worker is currently carrying the least - like loading the heaviest boxes onto whichever
        // RA01: truck is emptiest, so all the trucks arrive at roughly the same time instead of one straggling in long after the rest.
        regions.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var region in regions)
        {
            int lightest = 0;
            for (int w = 1; w < workers; w++) if (load[w] < load[lightest]) lightest = w;
            assigned[lightest].Add(region);
            load[lightest] += (long)region.Size;
        }

        List<Candidate>[] results = new List<Candidate>[workers];
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int mine = w;
            results[mine] = new List<Candidate>();
            tasks[mine] = Task.Run(() =>
            {
                byte[] buffer = new byte[ChunkBytes]; // RA01: M6-9 - one buffer per worker, refilled by ReadProcessMemory for every slice instead of allocating fresh each time
                foreach (var (addr, size) in assigned[mine])
                    ScanRegionChunked(addr, size, results[mine], buffer);
            });
        }
        while (!Task.WaitAll(tasks, 2000))
            Log.Write($"RA01 bone hunt: swept {Volatile.Read(ref bytesScanned) / 1048576:N0} MB of {total / 1048576:N0} MB so far");

        List<Candidate> found = new();
        foreach (var r in results) found.AddRange(r);

        Volatile.Write(ref candidates, found); // RA01: publish the finished list BEFORE the flag that says it is ready, so the OpenXR thread never sees "narrowing" with a half-built list
        Volatile.Write(ref sweeping, false);
        narrowUntil = Stopwatch.GetTimestamp() + NarrowWindowTicks;
        nextSampleAt = Stopwatch.GetTimestamp() + SampleTicks;
        Volatile.Write(ref narrowing, true);
        Log.Write($"RA01 bone hunt: {found.Count} candidate(s) near the start point - now walk a slow, easy circle, big enough to swing your legs naturally, for the next {NarrowWindowTicks / Stopwatch.Frequency} seconds");
    }

    // RA01: M6-7 - the fast first pass: lists every committed, private, writable block (its address and size) without reading a single byte of it. Free and reserved space, which is
    // RA01: most of a 64-bit process's address space, is skipped in one VirtualQuery call each however large it is - only real, allocated heap costs anything here.
    // RA01: Cost - CPU: low (one VirtualQuery per block in the whole address space, a few microseconds each; runs once per hunt); GPU: none.
    static List<(nint Address, nint Size)> EnumerateRegions()
    {
        List<(nint, nint)> regions = new();
        nint address = 0x10000; // RA01: Windows' own minimum allocation granularity - nothing real sits below this
        while (true)
        {
            MemoryBasicInformation info;
            if (VirtualQuery(address, &info, (nuint)sizeof(MemoryBasicInformation)) == 0) break; // RA01: the top of the address space, or a real error either way - nothing more to walk
            if (Qualifies(info)) regions.Add(((nint)info.BaseAddress, (nint)info.RegionSize));
            address = (nint)((long)info.BaseAddress + (long)info.RegionSize);
        }
        return regions;
    }

    // RA01: M6-7 - the one test a block of memory has to pass to be worth scanning: really allocated (not just reserved), private (the game's own heap, not a mapped file or its code),
    // writable, and not walled off (a guard page or otherwise unreadable). Shared by both passes so they can never disagree about what counts.
    // Cost - CPU: extremely low (a handful of flag compares); GPU: none.
    static bool Qualifies(in MemoryBasicInformation info) =>
        info.State == Committed && info.Type == Private && (info.Protect & Writable) != 0 && (info.Protect & Blocked) == 0;

    const int ChunkBytes = 1024 * 1024; // RA01: M6-9 - a big committed block is read this many bytes at a time, into a reusable buffer, so no worker ever needs a huge allocation for one giant block

    // RA01: M6-9 - one committed block, read ChunkBytes at a time into 'buffer' via ReadProcessMemory (see Win32.cs) rather than being scanned in place. Each slice's read is its own
    // safety check: a slice the game has since freed simply comes back false or short, and is skipped, not crashed on - the rest of the block is still read normally, since a
    // game freeing part of a block does not mean the rest stopped being real memory.
    // Cost - CPU: one ReadProcessMemory call per slice (a syscall, a few microseconds) plus ScanBuffer's own cost below; GPU: none.
    static void ScanRegionChunked(nint start, nint size, List<Candidate> found, byte[] buffer)
    {
        for (nint offset = 0; offset < size; offset += ChunkBytes)
        {
            nint chunkStart = start + offset;
            nuint chunkSize = (nuint)Math.Min((long)ChunkBytes, (long)size - (long)offset);
            nuint bytesRead;
            bool ok;
            fixed (byte* b = buffer) ok = ReadProcessMemory(CurrentProcess, chunkStart, b, chunkSize, &bytesRead);
            if (ok && bytesRead > 0) ScanBuffer(buffer, bytesRead, chunkStart, found);
            Interlocked.Add(ref bytesScanned, (long)chunkSize); // RA01: M6-9 - counted per slice, not per whole block, so progress moves smoothly even through one very large block
        }
    }

    // RA01: our own safe copy of one slice of the game's memory, searched 4-byte-aligned for a float triplet within SearchRadius of the hunt's start point. A cheap box check on each
    // axis in turn means most positions are rejected after reading just one float, before ever reading the other two or paying for the real (square-rooted) distance below.
    // 'remoteStart' is the slice's REAL address in the game's memory - what gets recorded on a match is always that, never anything pointing at our own local buffer.
    // Cost - CPU: proportional to the slice's size, the bulk of Sweep's overall cost; GPU: none.
    static void ScanBuffer(byte[] buffer, nuint length, nint remoteStart, List<Candidate> found)
    {
        long steps = (long)length / 4 - 2; // RA01: the last 4-byte-aligned position a 12-byte triplet can still start at
        fixed (byte* raw = buffer)
        {
            float* p = (float*)raw;
            for (long i = 0; i < steps; i++)
            {
                float x = p[i];
                if (!InRange(x, baseX)) continue;
                float y = p[i + 1];
                if (!InRange(y, baseY)) continue;
                float z = p[i + 2];
                if (!InRange(z, baseZ)) continue;
                if (Distance(x, y, z, baseX, baseY, baseZ) <= SearchRadius)
                    // RA01: M6-15 - LastX/Y/Z starts equal to X/Y/Z so the log's "last seen at" always has a value even if the window closes before the first sample. M6-16: LastRelX/Y/Z
                    // RA01: starts as this candidate's offset from the camera AT THE SWEEP (baseX/Y/Z), so the very first sample in Track() below measures a real change from that offset
                    // RA01: instead of from an uninitialised (0,0,0), which would otherwise look like a huge fake swing on sample one.
                    found.Add(new Candidate { Address = remoteStart + (nint)(i * 4), X = x, Y = y, Z = z, LastX = x, LastY = y, LastZ = z, LastRelX = x - baseX, LastRelY = y - baseY, LastRelZ = z - baseZ });
            }
        }
    }

    // RA01: called once per headset frame from XrSession.PollKeys. Does nothing unless a hunt's narrowing window is open - cheap when idle.
    // Cost - CPU: extremely low most frames (two flag reads); about once a second while narrowing, one CameraHook read and a pass over the (by then small) candidate list; GPU: none.
    public static void Poll(long now)
    {
        if (Volatile.Read(ref sweeping) || !Volatile.Read(ref narrowing)) return;
        if (now >= narrowUntil) { FinishNarrowing(); return; }
        if (now < nextSampleAt) return;
        nextSampleAt = now + SampleTicks;

        if (!CameraHook.TryReadEnginePosition(out float refX, out float refY, out float refZ)) return; // RA01: camera not building this instant (menu, loading) - skip this sample, the deadline still stands
        // RA01: M6-15 - a plain reverse loop, not RemoveAll: each surviving candidate needs its running totals updated in place (how far it has moved, whether it just reversed), and
        // RA01: RemoveAll's predicate only gets a copy of the struct to look at, never a way to write the update back. Only this thread ever touches the list once Sweep has handed it
        // RA01: over, so mutating it directly here is safe.
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            Candidate c = candidates[i];
            if (Track(ref c, refX, refY, refZ)) candidates[i] = c;
            else candidates.RemoveAt(i);
        }
    }

    // RA01: M6-15/M6-16 - one sample for one candidate. Two things drop it outright: the read failing (never a real bone, or its memory was freed), or it sitting further from wherever
    // the camera currently is than any real limb plausibly would (MaxLeash) - a camera-glued object or something on the far side of the street would fail this every time.
    // M6-16: everything else is judged by how far it has moved RELATIVE TO THE CAMERA, not its raw path through the world. Picture the difference between watching a dog on a leash
    // from a park bench versus from a drone flying overhead at the same pace as the person walking it: from the bench, the dog visibly wanders back and forth; from the drone, it
    // barely seems to move at all, even though its raw path across the ground is huge, because the drone is carrying that whole distance along with it. The first version of this
    // judged from the drone's view (the candidate's raw position) - so anything rigidly glued to the body racked up just as much "distance" and "swinging" as a real limb simply
    // because the whole body walked a long way around the circle. Subtracting the camera's own current position first puts the judgment back on the park bench, where only a
    // genuinely swinging limb still moves.
    // Cost - CPU: extremely low (one ReadProcessMemory call, two distance-scale checks, a handful of multiplies, per candidate per sample); GPU: none.
    static bool Track(ref Candidate c, float refX, float refY, float refZ)
    {
        if (!TryReadTriplet(c.Address, out float x, out float y, out float z)) return false;
        if (Distance(x, y, z, refX, refY, refZ) > MaxLeash) return false;

        float relX = x - refX, relY = y - refY, relZ = z - refZ; // RA01: M6-16 - this candidate's offset from the body right now, not its raw position
        float deltaX = relX - c.LastRelX, deltaY = relY - c.LastRelY, deltaZ = relZ - c.LastRelZ;
        c.TotalDistance += Distance(relX, relY, relZ, c.LastRelX, c.LastRelY, c.LastRelZ);
        if (c.HasPrevDelta)
        {
            // RA01: multiply matching axes of this step's direction against the step before it and add the three together - a pendulum swinging back gives a negative total (the two
            // RA01: directions disagree more than they agree), the same way a positive number times a negative one is always negative. Something that just keeps heading the same
            // RA01: general way relative to the body essentially never does this.
            float dot = deltaX * c.PrevDeltaX + deltaY * c.PrevDeltaY + deltaZ * c.PrevDeltaZ;
            if (dot < 0) c.SwingCount++;
        }
        c.PrevDeltaX = deltaX; c.PrevDeltaY = deltaY; c.PrevDeltaZ = deltaZ; c.HasPrevDelta = true;
        c.LastRelX = relX; c.LastRelY = relY; c.LastRelZ = relZ;
        c.LastX = x; c.LastY = y; c.LastZ = z; // RA01: kept separately, in raw world space, purely for the "last seen at" log line
        return true;
    }

    // RA01: M6-9 - reads three floats at 'address' via ReadProcessMemory, same as the bulk sweep - a bad address just makes this return false, never a crash.
    // Cost - CPU: extremely low (one ReadProcessMemory call, 12 bytes); GPU: none.
    static bool TryReadTriplet(nint address, out float x, out float y, out float z)
    {
        float* p = stackalloc float[3]; // RA01: 12 bytes on this thread's own stack - ReadProcessMemory fills it, nothing here ever touches the game's memory directly
        nuint bytesRead;
        if (!ReadProcessMemory(CurrentProcess, address, p, 12, &bytesRead) || bytesRead < 12) { x = y = z = 0; return false; }
        x = p[0]; y = p[1]; z = p[2];
        return true;
    }

    // RA01: M6-15 - the narrowing window has closed. Everything that made it this far already stayed on its leash the whole time (Track dropped anything that did not); what is judged
    // here, for the first time, is the whole walk at once - did it move enough in total, and did it swing back and forth enough - since a candidate could easily look fine on any
    // one sample while never actually behaving like a limb across the full window. M6-17: logs the whole survivor list, up to MaxLogged - this used to stop at 25, but with the
    // list this small (low hundreds, not the tens of thousands the sweep started with) there is no real cost to printing it all, and the whole point now is laying every address
    // next to the WolvenKit bone index by hand - a 25-line sample can't be compared against anything.
    // Cost - CPU: extremely low (one pass over a small list, up to MaxLogged log lines, once per hunt); GPU: none.
    static void FinishNarrowing()
    {
        Volatile.Write(ref narrowing, false);
        List<Candidate> survivors = new();
        foreach (Candidate c in candidates)
            if (c.TotalDistance >= MinMovement && c.SwingCount >= MinSwings) survivors.Add(c);
        Volatile.Write(ref candidates, survivors);
        Volatile.Write(ref lastSurvivorCount, survivors.Count);
        Volatile.Write(ref finished, true);
        Log.Write($"RA01 bone hunt: {survivors.Count} candidate(s) swung back and forth near the body through the whole walk");
        int shown = 0;
        foreach (Candidate c in survivors)
        {
            if (shown++ >= MaxLogged) { Log.Write($"RA01   ...and {survivors.Count - MaxLogged} more (not shown)"); break; }
            Log.Write($"RA01   0x{c.Address:X}: swung {c.SwingCount} time(s), {c.TotalDistance:F3} m travelled, last seen at ({c.LastX:F3}, {c.LastY:F3}, {c.LastZ:F3})");
        }
    }

    // RA01: M6-6 - one line for the F10 "Dev Only Tools" tab's Status row: what the hunt is doing right now, so the whole thing can be started and watched without ever leaving the
    // headset or touching the keyboard - the F11 key and the log file still work exactly the same, this is just a second, hands-in-VR way to see the same thing.
    // Cost - CPU: extremely low (a few flag reads and one short string, called a couple of times per headset frame while the F10 window is open); GPU: none.
    public static string Status()
    {
        if (Volatile.Read(ref sweeping))
        {
            long total = Volatile.Read(ref totalBytesToScan), done = Volatile.Read(ref bytesScanned);
            if (total <= 0) return "Sweeping the heap - starting..."; // RA01: M6-7 - EnumerateRegions is still building the list, so there is no percentage to show yet
            int percent = (int)(100 * done / total);
            return $"Sweeping the heap - {percent}% ({done / 1048576:N0} / {total / 1048576:N0} MB)";
        }
        if (Volatile.Read(ref narrowing))
        {
            long left = (narrowUntil - Stopwatch.GetTimestamp()) / Stopwatch.Frequency;
            return $"Walk a slow circle now - {Math.Max(0, left)}s left";
        }
        if (Volatile.Read(ref finished)) return $"Done: {Volatile.Read(ref lastSurvivorCount)} candidate(s) - see the log for addresses";
        return "Press Start, then hold still in-game";
    }

    // RA01: a cheap first check on one axis before the real (square-rooted) distance is ever computed - also rejects NaN and wild garbage values, since any comparison against NaN is false.
    // Cost - CPU: extremely low (two compares); GPU: none.
    static bool InRange(float value, float centre) => value > centre - SearchRadius && value < centre + SearchRadius;

    // RA01: plain 3D distance - the Pythagorean theorem with one more term for the extra dimension.
    // Cost - CPU: extremely low (a handful of multiplies, one Sqrt); GPU: none.
    static float Distance(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        float dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
