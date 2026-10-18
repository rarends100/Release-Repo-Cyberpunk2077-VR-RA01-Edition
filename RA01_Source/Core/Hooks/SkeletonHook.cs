using System.Diagnostics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M6-14 - the permanent way in, now that M6-13's pointer hunt has proven itself across a restart and a run across the map. This skips the heap search entirely: go
// straight from the executable's own fixed spot to the bone array, every launch, no hunting. Before this is trusted for anything beyond logging, its first and only
// job is DumpAll below - it prints every slot's raw position once, so it can be checked against the WolvenKit rest-pose file by the DISTANCE between neighbouring
// slots (a length, unaffected by which way the character is facing) rather than by trusting that the live array's order matches the file's bone order.
// M7-3 - WARNING: the M7-2 write test into this array crashed the game instantly, and its pointer never changes all session, so this is most likely a shared game
// system (the Sept 29 notes say the camera system), not V's skeleton. Kept read-only for DumpAll; the V-only doorway is now PoseHook (see Visuals/PoseTest.cs).
// safety: every read below goes through ReadProcessMemory (see Win32.cs), never a raw pointer dereference - a bad address just fails that one call instead of crashing the game.
// Cost - CPU: DumpAll is one short burst of up to 200 small reads when triggered, then done; GPU: none.
internal static unsafe class SkeletonHook
{
    const nint ArrayPointerOffset = 0x48C6DF0; // RA01: M6-13 - confirmed across a restart and a run to a new part of the map: this fixed spot in the executable always holds a pointer straight to the live bone array's first slot
    const int SlotBytes = 64;                  // RA01: M6-13 - matches the WolvenKit rig data once padded: rotation, scale and translation as three 16-byte Vector4s (48 bytes), padded out to a 64-byte cache line
    const int TranslationOffset = 32;          // RA01: the position floats sit 32 bytes into each slot, after rotation and scale
    const int MaxSlots = 200;                  // RA01: generous over both skeletons' real bone counts (181 male, 183 female) - reading past the real array just logs an unreadable slot and stops, never a crash

    static readonly nint CurrentProcess = GetCurrentProcess(); // RA01: this process's own pseudo-handle, same as BoneFinder's and PointerFinder's - fetched once, never changes

    // RA01: Dev Only Tools trigger - resolves the fixed pointer fresh (module base changes every launch, the offset never does), then logs every slot once. No sweeping, no
    // narrowing window - this either works in well under a second or it doesn't, because there is nothing left to search for.
    public static void DumpAll()
    {
        if (!TryResolveArrayBase(out nint arrayBase)) { Log.Write("RA01 skeleton hook: could not resolve the array - is module+0x48C6DF0 still a valid pointer this launch?"); return; }
        Log.Write($"RA01 skeleton hook: array base 0x{arrayBase:X} - logging up to {MaxSlots} slots");

        bool havePrev = false;
        float prevX = 0, prevY = 0, prevZ = 0;
        for (int i = 0; i < MaxSlots; i++)
        {
            nint slotAddress = arrayBase + i * SlotBytes + TranslationOffset;
            if (!TryReadTriplet(slotAddress, out float x, out float y, out float z)) { Log.Write($"RA01   slot {i}: unreadable - stopping"); break; }
            float gap = havePrev ? MathF.Sqrt((x - prevX) * (x - prevX) + (y - prevY) * (y - prevY) + (z - prevZ) * (z - prevZ)) : 0;
            Log.Write($"RA01   slot {i}: ({x:F3}, {y:F3}, {z:F3}) - {gap:F3} m from the slot before it");
            prevX = x; prevY = y; prevZ = z; havePrev = true;
        }
    }

    // RA01: module base (found fresh this launch, via the same BCL call PointerFinder uses) plus the fixed offset, dereferenced once to reach the array's first byte.
    // M7-1 - made internal (was private) so other diagnostics can borrow this same resolve instead of duplicating the lookup.
    // Cost - CPU: extremely low (one module lookup plus one pointer-sized read); GPU: none.
    internal static bool TryResolveArrayBase(out nint arrayBase)
    {
        arrayBase = 0;
        ProcessModule? module = Process.GetCurrentProcess().MainModule;
        if (module is null) return false;
        nint pointerSlot = module.BaseAddress + ArrayPointerOffset;
        nint value;
        nuint bytesRead;
        if (!ReadProcessMemory(CurrentProcess, pointerSlot, &value, (nuint)sizeof(nint), &bytesRead) || bytesRead < (nuint)sizeof(nint)) return false;
        arrayBase = value;
        return true;
    }

    // RA01: M6-9's safe-read pattern, same as BoneFinder's own TryReadTriplet - a bad address just returns false, never a crash.
    static bool TryReadTriplet(nint address, out float x, out float y, out float z)
    {
        float* p = stackalloc float[3];
        nuint bytesRead;
        if (!ReadProcessMemory(CurrentProcess, address, p, 12, &bytesRead) || bytesRead < 12) { x = y = z = 0; return false; }
        x = p[0]; y = p[1]; z = p[2];
        return true;
    }
}
