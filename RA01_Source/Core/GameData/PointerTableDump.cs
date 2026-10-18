using System.Diagnostics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M6-18 through M6-20 - a one-off, read-only look at the pointer table M6-17's hunt turned up by accident: a run of fixed slots in the executable's own static memory,
// each one holding a pointer, several of them landing near a real live body position two separate game launches in a row. Eight bytes apart is exactly the size of one
// pointer on this machine, so that is not coincidence - it is a real array of pointers sitting in the game's own data, the same kind of fixed structure SkeletonHook
// already reads from a single slot, just wider. M6-20's idea: if this really is one pointer per bone, laid out back to back, the whole skeleton is just sitting there
// waiting to be read in order - no walking, no swing test, no hunting required. So this reads a much wider stretch than M6-18/19 did (room for a couple thousand
// slots, comfortably more than the skeleton's own ~181 bones), on the chance the table is exactly that clean.
// how it works: for every 8-byte slot in the window, read whatever pointer is stored there, then search the ScanBytes right after wherever that pointer leads, the same
// 4-byte-aligned float-triplet search BoneFinder already does over the whole heap, just aimed at one small, already-promising spot instead of gigabytes of it. M6-18
// tried reading a fixed 12 bytes right at the target and came back all NaN, even on slots already confirmed elsewhere - the real position was never AT the target, it
// was some distance past it, a different distance per slot, so a search beats a guessed offset. Only slots that actually find something get a log line - at a couple
// thousand slots, one line per empty slot would bury the handful that matter, so this only counts the empty ones and reports that count once at the end.
// safety: same ReadProcessMemory pattern as SkeletonHook and BoneFinder - a slot or a stretch of memory that cannot be read just fails that read and moves on, never
// dereferenced directly.
// Cost - CPU: one short burst when triggered - a couple thousand slots, each with one small read for its pointer and one ScanBytes read for the search past it; still
// comfortably under a second. GPU: none.
internal static unsafe class PointerTableDump
{
    const nint TableStart = 0x34B8000; // RA01: M6-20 - about 0x1FE8 bytes before the fifteen slots Find root pointer originally confirmed, room for a per-bone table to start well before what we happened to catch first
    const nint TableEnd = 0x34BC000;   // RA01: M6-20 - about 0x2000 bytes past that same confirmed range - together this window holds room for over 2,000 pointer slots, several times the skeleton's own ~181 bones
    const int SlotBytes = 8;           // RA01: one pointer, on this 64-bit process
    const int ScanBytes = 1024;        // RA01: M6-19 - comfortably covers the +0x60 to +0x380 range Find root pointer already confirmed real data sits at, for any slot in the table
    const float SearchRadius = 3f;     // RA01: M6-19 - generous: standing still, no leash or swing test here yet, just "is this anywhere near the body at all" - comfortably covers a whole standing person head to foot
    const int MaxPerSlot = 5;          // RA01: a safety cap on one slot's own log lines, the same idea as FinishNarrowing's cap on the whole survivor list - one slot's search should not flood the log if its stretch of memory happens to be unusually busy

    static readonly nint CurrentProcess = GetCurrentProcess();

    // RA01: Dev Only Tools trigger.
    public static void Dump()
    {
        ProcessModule? module = Process.GetCurrentProcess().MainModule;
        if (module is null) { Log.Write("RA01 pointer table: no main module - is the game actually running?"); return; }
        if (!CameraHook.TryReadEnginePosition(out float camX, out float camY, out float camZ)) { Log.Write("RA01 pointer table: no engine camera yet - get into the game first"); return; }

        Log.Write($"RA01 pointer table: reading module+0x{TableStart:X} to 0x{TableEnd:X}, searching {ScanBytes} bytes past each pointer, camera at ({camX:F2}, {camY:F2}, {camZ:F2})");

        byte[] buffer = new byte[ScanBytes];
        int emptySlots = 0, totalSlots = 0;
        for (nint offset = TableStart; offset <= TableEnd; offset += SlotBytes)
        {
            totalSlots++;
            nint slotAddress = module.BaseAddress + offset;
            if (!TryReadPointer(slotAddress, out nint target) || target == 0) { emptySlots++; continue; }

            nuint bytesRead;
            bool ok;
            fixed (byte* b = buffer) ok = ReadProcessMemory(CurrentProcess, target, b, (nuint)ScanBytes, &bytesRead);
            if (!ok || bytesRead == 0) { emptySlots++; continue; }

            int shown = 0;
            long steps = (long)bytesRead / 4 - 2;
            fixed (byte* raw = buffer)
            {
                float* p = (float*)raw;
                for (long i = 0; i < steps; i++)
                {
                    float x = p[i], y = p[i + 1], z = p[i + 2];
                    float distance = Distance(x, y, z, camX, camY, camZ);
                    if (!(distance <= SearchRadius)) continue; // RA01: also rejects NaN - any comparison against NaN is false
                    if (shown++ >= MaxPerSlot) { Log.Write($"RA01   +0x{offset:X}: -> 0x{target:X} ...more matches past +0x{i * 4:X}, not shown"); break; }
                    Log.Write($"RA01   +0x{offset:X}: -> 0x{target:X} +0x{i * 4:X}, ({x:F3}, {y:F3}, {z:F3}) - {distance:F3} m from camera");
                }
            }
            if (shown == 0) emptySlots++;
        }
        Log.Write($"RA01 pointer table: done - {totalSlots - emptySlots} of {totalSlots} slots found something within {SearchRadius:F0}m");
    }

    // RA01: reads one 8-byte pointer at 'address' - the same safe-read pattern as SkeletonHook.TryResolveArrayBase, just generalised to any slot instead of one fixed offset.
    static bool TryReadPointer(nint address, out nint value)
    {
        value = 0;
        nint local;
        nuint bytesRead;
        if (!ReadProcessMemory(CurrentProcess, address, &local, (nuint)sizeof(nint), &bytesRead) || bytesRead < (nuint)sizeof(nint)) return false;
        value = local;
        return true;
    }

    // RA01: plain 3D distance - the Pythagorean theorem with one more term for the extra dimension.
    static float Distance(float x1, float y1, float z1, float x2, float y2, float z2)
    {
        float dx = x1 - x2, dy = y1 - y2, dz = z1 - z2;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
