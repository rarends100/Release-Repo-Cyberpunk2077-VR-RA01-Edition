using System.Diagnostics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M6-21 - the "radiate outward" idea: instead of one hunt anchored to a single fixed point (BoneFinder's camera, or M6-18/20's fixed pointer table), every point already
// known to be real becomes its own search centre, and one heap sweep asks - for every float triplet in the game's memory - is this close enough to ANY of them to plausibly
// be its neighbour on the skeleton? A hit becomes a fresh anchor for the NEXT click, so each press of the button walks one hop further out from whatever is already
// confirmed, the same way a search party radiates outward from wherever the last person was found instead of always walking back to base first.
// seeded from two things already proven real this launch: the camera itself, and whatever BoneFinder's own swing-tested hunt (Bone discovery) left standing, if one has
// been run - a real head start instead of starting from nothing.
// "close enough to be a neighbour" is not a guess: your own WolvenKit rig data says the single longest bone-to-bone gap anywhere in the whole 181-bone skeleton (hips to
// root) is 0.9512m, so MaxSegment below sits a little past that - close enough that a real joint is never missed, nowhere near wide enough to leap over an entire limb in
// one hop.
// how it works: reuses BoneFinder's own heap-sweep engine almost exactly (the same committed/private/writable heap only, the same multi-threaded chunked
// ReadProcessMemory search) - the only real difference is the match test. A cheap first check (the same kind of fast reject BoneFinder's InRange does) asks whether a
// point falls anywhere within the single box that contains every known anchor, padded by MaxSegment; only a point that passes that pays the finer cost of checking its
// distance to each known anchor individually, to find the nearest one. That keeps this fast even as the known list grows across several clicks, instead of slowing down
// in proportion to how much of the skeleton has already been mapped.
// one honest simplification: nothing here deduplicates near-identical points (the same real bone is often stored more than once nearby, as several earlier hunts found) -
// the known list can carry a few redundant entries. That costs a little extra time, never correctness, so it is left alone rather than adding machinery to solve it.
// stay clear of other people while running this - anything real and nearby, including another character's own skeleton, can get pulled in the same way yours is.
// safety: identical posture to BoneFinder - every read goes through ReadProcessMemory (see Win32.cs), read-only, never a raw dereference.
// Cost - CPU: heavy but bounded, the same order as BoneFinder's own hunt - one full pass over the game's committed heap per click, several seconds to a couple of minutes
// depending how much memory is committed; runs on its own threads, never the game's; idle between clicks. GPU: none.
internal static unsafe class SkeletonRadiate
{
    const uint Committed = 0x1000, Private = 0x20000, Writable = 0x04 | 0x08 | 0x40 | 0x80, Blocked = 0x100 | 0x01; // RA01: identical meaning to BoneFinder's own constants - see there
    const int ChunkBytes = 1024 * 1024;
    const float MaxSegment = 1.1f; // RA01: M6-21 - a little past the longest real bone-to-bone gap in the rig (0.9512m, hips to root) - see the file comment above
    const int MaxLogged = 1000;    // RA01: same idea as BoneFinder's own cap - this only runs once per click, so printing everything found costs nothing worth guarding against

    internal struct Anchor { public float X, Y, Z; }
    internal struct Found { public nint Address; public float X, Y, Z; public float NearestAnchorDistance; }

    static List<Anchor> anchors = new();
    static List<Found> lastFound = new();
    static bool sweeping;
    static long totalBytesToScan, bytesScanned;
    static int lastFoundCount;
    static readonly nint CurrentProcess = GetCurrentProcess();

    // RA01: Dev Only Tools trigger - one hop outward per click. First click seeds the known list; every click after that folds the previous click's finds into it first.
    public static void Start()
    {
        if (Volatile.Read(ref sweeping)) { Log.Write("RA01 skeleton radiate: already running, ignored"); return; }

        if (anchors.Count == 0)
        {
            if (!CameraHook.TryReadEnginePosition(out float cx, out float cy, out float cz)) { Log.Write("RA01 skeleton radiate: no engine camera yet - get into the game first"); return; }
            anchors.Add(new Anchor { X = cx, Y = cy, Z = cz });
            foreach (BoneFinder.Candidate c in BoneFinder.Survivors) anchors.Add(new Anchor { X = c.LastX, Y = c.LastY, Z = c.LastZ });
            Log.Write($"RA01 skeleton radiate: starting from {anchors.Count} known point(s) - the camera, plus whatever Bone discovery already confirmed this launch");
        }
        else
        {
            foreach (Found f in lastFound) anchors.Add(new Anchor { X = f.X, Y = f.Y, Z = f.Z });
        }

        Volatile.Write(ref sweeping, true);
        Volatile.Write(ref totalBytesToScan, 0);
        Volatile.Write(ref bytesScanned, 0);
        Log.Write($"RA01 skeleton radiate: sweeping the heap for anything within {MaxSegment:F2}m of {anchors.Count} known point(s)");
        new Thread(Sweep) { IsBackground = true, Name = "RA01 skeleton radiate" }.Start();
    }

    // RA01: clears the known list, so the next click starts over from just the camera (and Bone discovery's survivors, if any) instead of an ever-growing history.
    public static void Reset()
    {
        anchors = new();
        lastFound = new();
        Volatile.Write(ref lastFoundCount, 0);
        Log.Write("RA01 skeleton radiate: reset - the next click starts fresh");
    }

    // RA01: BoneFinder.Sweep's own shape, almost exactly - EnumerateRegions, load-balance across a few worker threads, chunked ReadProcessMemory reads. The only change is what
    // each worker tests a candidate float triplet against: a snapshot of every known anchor, not one fixed point.
    static void Sweep()
    {
        List<(nint Address, nint Size)> regions = EnumerateRegions();
        long total = 0;
        foreach (var r in regions) total += r.Size;
        Volatile.Write(ref totalBytesToScan, total);

        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        List<(nint Address, nint Size)>[] assigned = new List<(nint Address, nint Size)>[workers];
        long[] load = new long[workers];
        for (int w = 0; w < workers; w++) assigned[w] = new();
        regions.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var region in regions)
        {
            int lightest = 0;
            for (int w = 1; w < workers; w++) if (load[w] < load[lightest]) lightest = w;
            assigned[lightest].Add(region);
            load[lightest] += (long)region.Size;
        }

        Anchor[] anchorSnapshot = anchors.ToArray(); // RA01: a fixed snapshot for this one pass - the real list only grows between clicks, never mid-sweep
        (float minX, float maxX, float minY, float maxY, float minZ, float maxZ) box = Bounds(anchorSnapshot);

        List<Found>[] results = new List<Found>[workers];
        Task[] tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
        {
            int mine = w;
            results[mine] = new List<Found>();
            tasks[mine] = Task.Run(() =>
            {
                byte[] buffer = new byte[ChunkBytes];
                foreach (var (addr, size) in assigned[mine])
                    ScanRegionChunked(addr, size, results[mine], buffer, anchorSnapshot, box);
            });
        }
        while (!Task.WaitAll(tasks, 2000))
            Log.Write($"RA01 skeleton radiate: swept {Volatile.Read(ref bytesScanned) / 1048576:N0} MB of {total / 1048576:N0} MB so far");

        List<Found> found = new();
        foreach (var r in results) found.AddRange(r);
        lastFound = found;
        Volatile.Write(ref lastFoundCount, found.Count);
        Volatile.Write(ref sweeping, false);

        Log.Write($"RA01 skeleton radiate: {found.Count} new point(s) this hop (known total including them: {anchors.Count + found.Count})");
        int shown = 0;
        foreach (Found f in found)
        {
            if (shown++ >= MaxLogged) { Log.Write($"RA01   ...and {found.Count - MaxLogged} more (not shown)"); break; }
            Log.Write($"RA01   0x{f.Address:X}: ({f.X:F3}, {f.Y:F3}, {f.Z:F3}) - {f.NearestAnchorDistance:F3} m from its nearest known point");
        }
        Log.Write(found.Count > 0
            ? "RA01 skeleton radiate: click Radiate again to hop one step further out, or Reset radiate to start over"
            : "RA01 skeleton radiate: nothing new - either the whole reachable skeleton is already known, or there is nothing left within reach of it");
    }

    // RA01: the box that contains every known anchor, padded by MaxSegment on every side - a point outside this can not be within MaxSegment of any anchor, so it is never worth
    // RA01: the finer per-anchor check below. The same cheap-reject-first idea as BoneFinder's own InRange, just built from many points instead of one.
    static (float, float, float, float, float, float) Bounds(Anchor[] a)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Anchor an in a)
        {
            if (an.X < minX) minX = an.X; if (an.X > maxX) maxX = an.X;
            if (an.Y < minY) minY = an.Y; if (an.Y > maxY) maxY = an.Y;
            if (an.Z < minZ) minZ = an.Z; if (an.Z > maxZ) maxZ = an.Z;
        }
        return (minX - MaxSegment, maxX + MaxSegment, minY - MaxSegment, maxY + MaxSegment, minZ - MaxSegment, maxZ + MaxSegment);
    }

    static void ScanRegionChunked(nint start, nint size, List<Found> found, byte[] buffer, Anchor[] anchorSnapshot, (float minX, float maxX, float minY, float maxY, float minZ, float maxZ) box)
    {
        for (nint offset = 0; offset < size; offset += ChunkBytes)
        {
            nint chunkStart = start + offset;
            nuint chunkSize = (nuint)Math.Min((long)ChunkBytes, (long)size - (long)offset);
            nuint bytesRead;
            bool ok;
            fixed (byte* b = buffer) ok = ReadProcessMemory(CurrentProcess, chunkStart, b, chunkSize, &bytesRead);
            if (ok && bytesRead > 0) ScanBuffer(buffer, bytesRead, chunkStart, found, anchorSnapshot, box);
            Interlocked.Add(ref bytesScanned, (long)chunkSize);
        }
    }

    // RA01: the cheap box test first (three range checks, same shape as BoneFinder's InRange, also rejects NaN for free since any compare against NaN is false), then only for
    // what survives that: the finer, real per-anchor distance check, keeping whichever anchor is nearest.
    static void ScanBuffer(byte[] buffer, nuint length, nint remoteStart, List<Found> found, Anchor[] anchorSnapshot, (float minX, float maxX, float minY, float maxY, float minZ, float maxZ) box)
    {
        long steps = (long)length / 4 - 2;
        fixed (byte* raw = buffer)
        {
            float* p = (float*)raw;
            for (long i = 0; i < steps; i++)
            {
                float x = p[i];
                if (!(x > box.minX && x < box.maxX)) continue;
                float y = p[i + 1];
                if (!(y > box.minY && y < box.maxY)) continue;
                float z = p[i + 2];
                if (!(z > box.minZ && z < box.maxZ)) continue;

                float best = float.MaxValue;
                for (int a = 0; a < anchorSnapshot.Length; a++)
                {
                    float d = Distance(x, y, z, anchorSnapshot[a].X, anchorSnapshot[a].Y, anchorSnapshot[a].Z);
                    if (d < best) best = d;
                }
                if (best <= MaxSegment) found.Add(new Found { Address = remoteStart + (nint)(i * 4), X = x, Y = y, Z = z, NearestAnchorDistance = best });
            }
        }
    }

    // RA01: M6-7's own region pass, unchanged in spirit - lists every committed, private, writable block without reading a single byte of it.
    static List<(nint Address, nint Size)> EnumerateRegions()
    {
        List<(nint, nint)> regions = new();
        nint address = 0x10000;
        while (true)
        {
            MemoryBasicInformation info;
            if (VirtualQuery(address, &info, (nuint)sizeof(MemoryBasicInformation)) == 0) break;
            if (Qualifies(info)) regions.Add(((nint)info.BaseAddress, (nint)info.RegionSize));
            address = (nint)((long)info.BaseAddress + (long)info.RegionSize);
        }
        return regions;
    }

    static bool Qualifies(in MemoryBasicInformation info) =>
        info.State == Committed && info.Type == Private && (info.Protect & Writable) != 0 && (info.Protect & Blocked) == 0;

    // RA01: F10 "Dev Only Tools" Status row - what this is doing right now.
    public static string Status()
    {
        if (Volatile.Read(ref sweeping))
        {
            long total = Volatile.Read(ref totalBytesToScan), done = Volatile.Read(ref bytesScanned);
            if (total <= 0) return "Sweeping the heap - starting...";
            int percent = (int)(100 * done / total);
            return $"Sweeping the heap - {percent}% ({done / 1048576:N0} / {total / 1048576:N0} MB)";
        }
        return anchors.Count == 0
            ? "Press Radiate, then hold still in-game"
            : $"Known: {anchors.Count} point(s), {Volatile.Read(ref lastFoundCount)} new last hop - Radiate again or Reset";
    }

    static float Distance(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        float dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
