using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: M6-0, layer 2 - the real synthetic controller feed. Finds the one pointer inside the game's own import table that points at XInputGetState (ExeScan.FindImport), the same
// "overwrite one pointer in a table" trick PresentHook already uses on a DXGI vtable slot (see PresentHook.Patch), just found by walking the EXE's import table instead of a
// COM vtable - no machine code is patched either way. For controller slot 0 the detour stops asking Windows at all and answers directly with whatever ControllerMapping.Build()
// says (layer 1's tracked values, turned into an Xbox-shaped state, see controls_mapping\ControllerMapping.cs) - RA01 becomes the sole answer for that slot rather than racing
// anything else for it. Any other slot still passes straight through to the real function untouched, in case a real controller is ever plugged into it.
// Cost - CPU: extremely low (per call to slot 0: ControllerMapping.Build() plus a small struct compare, no call to the real function at all; any other slot: one unmodified
// real call forwarded through); GPU: none (input only).
internal static unsafe class XInputHook
{
    const uint ErrorSuccess = 0;
    static readonly string[] DllNames = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll", "xinput1_2.dll" }; // RA01: newest first; a game links against exactly one of these by name

    static int installed;
    static nint original;
    static string hookedDll = "";
    static long calls;
    static long nextReport;
    static uint packetNumber;
    static ControllerMapping.Gamepad lastSent;

    // RA01: installs the hook once at start-up: tries each possible XInput DLL name in turn against the EXE's own import table (ExeScan must already be loaded, see Entry.cs), patches the first one found.
    // Cost - CPU: low (a handful of import-table walks, each a few dozen string compares, once at start-up); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1)
        {
            Log.Write("RA01 XInput hook already installed, skipping");
            return;
        }
        try
        {
            nint slot = 0;
            foreach (string dll in DllNames)
            {
                slot = ExeScan.FindImport(dll, "XInputGetState");
                if (slot != 0) { hookedDll = dll; break; }
            }
            if (slot == 0)
            {
                Log.Write("RA01 XInput hook NOT installed: the exe does not import XInputGetState by name from any known xinput DLL (it may load it dynamically instead - not covered by this hook)");
                HookRegistry.Off(HookRegistry.XInput, "the exe doesn't import XInputGetState by name");
                return;
            }

            original = *(nint*)slot;
            HookRegistry.Found(HookRegistry.XInput, slot, $"import from {hookedDll}");   // RA01: M43
            nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            PresentHook.Patch(slot, 0, (nint)(delegate* unmanaged<uint, ControllerMapping.State*, uint>)&GetStateDetour);
            Log.Write($"RA01 XInput hook installed on {hookedDll}!XInputGetState (controller slot 0 now answers from the tracked motion controllers; every other slot is unaffected)");
        }
        catch (Exception e)
        {
            Log.Write($"RA01 XInput hook failed: {e.Message}");
        }
    }

    // RA01: what the game calls instead of XInputGetState. Slot 0 is answered directly from Controllers' tracked values (via ControllerMapping); every other slot still goes to the
    // real function unchanged, so a genuine second controller on another slot is never touched by any of this.
    // Cost - CPU: extremely low (slot 0: ControllerMapping.Build() plus bookkeeping; any other slot: one unmodified real call); GPU: none.
    [UnmanagedCallersOnly]
    static uint GetStateDetour(uint userIndex, ControllerMapping.State* state)
    {
        if (userIndex != 0)
            return ((delegate* unmanaged<uint, ControllerMapping.State*, uint>)original)(userIndex, state);

        ControllerMapping.Gamepad pad;
        try { pad = ControllerMapping.Build(); }
        catch { pad = default; } // RA01: M13-11 - an error escaping into the game here would end it on the spot (NativeAOT fast-fails); one neutral read instead
        Tick(pad);
        if (state != null)
        {
            if (!Same(pad, lastSent)) packetNumber++; // RA01: XInput's own contract: the packet number only changes when the state actually does
            state->PacketNumber = packetNumber;
            state->Gamepad = pad;
        }
        lastSent = pad;
        return ErrorSuccess;
    }

    // RA01: whether two built gamepad states are identical, field by field - used only to decide whether the packet number needs to move.
    // Cost - CPU: extremely low (seven integer comparisons); GPU: none.
    static bool Same(ControllerMapping.Gamepad a, ControllerMapping.Gamepad b) =>
        a.Buttons == b.Buttons && a.LeftTrigger == b.LeftTrigger && a.RightTrigger == b.RightTrigger &&
        a.ThumbLX == b.ThumbLX && a.ThumbLY == b.ThumbLY && a.ThumbRX == b.ThumbRX && a.ThumbRY == b.ThumbRY;

    // RA01: runs on every call to slot 0. Must never throw into the game, so it is wrapped; the built state has already gone back to the game by the time this runs.
    // Cost - CPU: extremely low (an atomic add, a clock read and a compare every call; the log line itself at most once a second); GPU: none.
    static void Tick(ControllerMapping.Gamepad pad)
    {
        try
        {
            Interlocked.Increment(ref calls);
            long now = Stopwatch.GetTimestamp();
            long due = Volatile.Read(ref nextReport);
            if (now >= due && Interlocked.CompareExchange(ref nextReport, now + Stopwatch.Frequency, due) == due)
                Report(pad);
        }
        catch { } // RA01: a hook must never crash the game; a report is not worth losing a frame over
    }

    // RA01: once a second - what RA01 is actually telling the game controller slot 0 looks like right now.
    // Cost - CPU: extremely low (one log line, once a second, only while the game is actually polling); GPU: none.
    static void Report(ControllerMapping.Gamepad pad)
    {
        long total = Interlocked.Exchange(ref calls, 0);
        if (total == 0) return;
        Log.Write($"RA01 XInput: {total} calls in the last second, slot 0, buttons 0x{pad.Buttons:X4}, triggers {pad.LeftTrigger}/{pad.RightTrigger}, sticks ({pad.ThumbLX},{pad.ThumbLY})/({pad.ThumbRX},{pad.ThumbRY})");
    }
}
