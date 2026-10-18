using System.Diagnostics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M6-13 - turns one of BoneFinder's own confirmed candidates (an address only good for this one play session, since ASLR moves the heap on every launch) into something that
// should survive a restart. The trick: the game's own executable is not reshuffled relative to itself - Windows moves where the whole thing is loaded, but everything inside it
// sits the same fixed number of bytes from its own start, every single launch, the same way a moving truck can park somewhere new each day without anything inside it getting
// rearranged relative to the truck itself. If a spot inside the executable's own memory (never the heap) holds a pointer that leads into the block a candidate lives in, then
// "the executable's own base address, found fresh each launch, plus this fixed number of bytes" always finds that same spot again - and whatever it points at is always wherever
// the skeleton really is this time, even though the exact shelf changes every launch.
// how it works: for each of BoneFinder's surviving candidates, the small neighbourhood of memory just before it is worked out (its own block, from VirtualQuery, same as
// BoneFinder's own sweep). Then every 8-byte-aligned slot in the executable's own writable memory (a pointer's own size on this 64-bit build) is read and checked: does this
// value, read as an address, land in that neighbourhood? A hit is logged as two fixed numbers - how far into the executable the slot sits, and how far past whatever it points at
// the candidate itself sits - which is the whole pointer chain. Confirming a hit is genuinely durable still means relaunching the game and checking it still leads somewhere sane.
// safety: read-only, same as BoneFinder and for the same reason - every read goes through ReadProcessMemory (see Win32.cs), never a raw pointer.
// Cost - CPU: one pass over the executable's own writable memory, which for a module (as opposed to the whole heap) is normally well under a second; runs on its own thread, never
// the game's; GPU: none.
internal static unsafe class PointerFinder
{
    const nint MaxStructOffset = 0x4000; // RA01: how many bytes before a candidate a pointer may land and still count as "pointing at its struct" - generous for any one skeleton/rig struct, tight enough to stay meaningful rather than matching half the heap
    const int ChunkBytes = 1024 * 1024;  // RA01: same chunk size as BoneFinder's own sweep, for the same reason - one reusable buffer, no giant single allocation

    static bool running, finished;
    static int hitCount;
    static readonly nint CurrentProcess = GetCurrentProcess(); // RA01: this process's own pseudo-handle, same as BoneFinder's - fetched once, never changes

    // RA01: Dev Only Tools trigger - takes whatever BoneFinder's last hunt left standing and looks for a fixed way back to it. Ignored if one is already running or nothing survived yet.
    // Cost - CPU: extremely low itself (a field check and a Thread.Start); the real work happens on the new thread; GPU: none.
    public static void Start()
    {
        if (Volatile.Read(ref running)) { Log.Write("RA01 pointer hunt: already running, ignored"); return; }
        IReadOnlyList<BoneFinder.Candidate> survivors = BoneFinder.Survivors;
        if (survivors.Count == 0) { Log.Write("RA01 pointer hunt: no candidates yet - run Bone discovery first and let something survive the window"); return; }

        Volatile.Write(ref running, true);
        Volatile.Write(ref finished, false);
        Log.Write($"RA01 pointer hunt: searching the executable's own memory for a way back to {survivors.Count} candidate(s) that does not depend on where this launch happened to put them");
        new Thread(() => Scan(survivors)) { IsBackground = true, Name = "RA01 pointer hunt" }.Start();
    }

    // RA01: works out each candidate's own small neighbourhood first, then walks the executable's own writable memory once looking for anything pointing into one of them.
    static void Scan(IReadOnlyList<BoneFinder.Candidate> survivors)
    {
        ProcessModule? module = Process.GetCurrentProcess().MainModule;
        if (module is null) { Log.Write("RA01 pointer hunt: could not find the executable's own module - nothing to search"); Volatile.Write(ref running, false); return; }
        nint moduleBase = module.BaseAddress;
        nint moduleSize = module.ModuleMemorySize;

        List<(nint Candidate, nint Low, nint High)> targets = new();
        foreach (BoneFinder.Candidate c in survivors)
        {
            MemoryBasicInformation info;
            if (VirtualQuery(c.Address, &info, (nuint)sizeof(MemoryBasicInformation)) == 0) continue; // RA01: freed since the hunt finished - nothing to search for this one
            nint low = Math.Max((nint)info.BaseAddress, c.Address - MaxStructOffset);
            targets.Add((c.Address, low, c.Address));
        }

        List<(nint Slot, nint Value, nint Candidate)> hits = new();
        byte[] buffer = new byte[ChunkBytes];
        for (nint offset = 0; offset < moduleSize; offset += ChunkBytes)
        {
            nint chunkStart = moduleBase + offset;
            nuint chunkSize = (nuint)Math.Min((long)ChunkBytes, (long)moduleSize - (long)offset);
            nuint bytesRead;
            bool ok;
            fixed (byte* b = buffer) ok = ReadProcessMemory(CurrentProcess, chunkStart, b, chunkSize, &bytesRead);
            if (ok && bytesRead > 0) ScanChunk(buffer, bytesRead, chunkStart, targets, hits);
        }

        Volatile.Write(ref hitCount, hits.Count);
        Volatile.Write(ref running, false);
        Volatile.Write(ref finished, true);
        if (hits.Count == 0)
        {
            Log.Write("RA01 pointer hunt: nothing found - the game may keep this behind more than one pointer, or the candidate was noise after all");
            return;
        }
        Log.Write($"RA01 pointer hunt: {hits.Count} pointer(s) found");
        foreach (var (slot, value, candidate) in hits)
            Log.Write($"RA01   module+0x{slot - moduleBase:X} -> 0x{value:X}, candidate 0x{candidate:X} is +0x{candidate - value:X} past that");
    }

    // RA01: 8-byte-aligned, since a pointer is 8 bytes on this 64-bit build and the compiler lines struct fields up on their own size by default.
    // Cost - CPU: proportional to the chunk's size, the bulk of Scan's overall cost; GPU: none.
    static void ScanChunk(byte[] buffer, nuint length, nint chunkStart, List<(nint Candidate, nint Low, nint High)> targets, List<(nint, nint, nint)> hits)
    {
        long steps = (long)length / 8;
        fixed (byte* raw = buffer)
        {
            nint* p = (nint*)raw;
            for (long i = 0; i < steps; i++)
            {
                nint value = p[i];
                foreach (var (candidate, low, high) in targets)
                    if (value >= low && value <= high)
                        hits.Add((chunkStart + (nint)(i * 8), value, candidate));
            }
        }
    }

    // RA01: one line for the F10 "Dev Only Tools" tab's status row, same idea as BoneFinder's own Status().
    // Cost - CPU: extremely low (a couple of flag reads); GPU: none.
    public static string Status()
    {
        if (Volatile.Read(ref running)) return "Searching the executable's own memory...";
        if (Volatile.Read(ref finished)) return $"Done: {Volatile.Read(ref hitCount)} pointer(s) found - see the log";
        return "Run Bone discovery first, then press Start here";
    }
}
