using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: M5a-10, M12-2 - the game resolution (VR Video Settings tab, "Game resolution"). Cyberpunk draws only at a resolution from the list it builds when it starts
// (M12-0 proved forcing the frame only stretches; M12-0c proved the list is built before RA01.dll is loaded). So the list is answered by the doorman
// (Launcher\Doorman, loaded with the game) with ONE size: the headset's per-eye size times the slider's percent, in the headset's own shape, longer side capped
// at MaxSide (3840, 4K's width). The launcher hands the doorman that size at start, in RA01_Resolution.txt next to it; this class rewrites that file when the
// slider settles, so applying any resolution in the game's Options > Video (which makes the game ask for its list again) takes it straight away.
// The file is only written when the doorman is actually in the game folder, so a game started without the launcher never gets a stray file.
// The resize calls (ResizeBuffers / ResizeBuffers1) are still watched and logged, so the log shows which size the game really applied.
// Cost - CPU: extremely low (a few compares per presented frame; one tiny file write, on a worker thread, only when the slider settles); GPU: none of its own
// - the game's own drawing cost grows with the size picked (100% of a headset eye is 4x the pixels of 50%).
internal static unsafe class ResolutionHook
{
    const int SlotResizeBuffers = 13;        // RA01: IDXGISwapChain
    const int SlotResizeBuffers1 = 39;       // RA01: IDXGISwapChain3
    const int MaxSide = 3840;                // RA01: 4K's width - the longer side never goes past this (the launcher uses the same rule)
    const long SettleMs = 700;               // RA01: the slider must rest this long before the file is rewritten
    const string DoormanDll = "xinput9_1_0.dll", SizeFile = "RA01_Resolution.txt";

    static nint originalResizeBuffers, originalResizeBuffers1;
    static int seenWidth, seenHeight, writtenWidth, writtenHeight; // RA01: the size the settings ask for now, and the size last written for the doorman
    static long seenChangedMs;
    static int writing;

    // RA01: installs the two resize watchers once, from PresentHook.Install, with the function table of its throwaway swapchain (all swapchains of this kind share it).
    // Cost - CPU: extremely low (two table writes, once at start-up); GPU: none.
    public static void Install(nint swapChainTable)
    {
        try
        {
            Hook(swapChainTable, SlotResizeBuffers, "ResizeBuffers", (nint)(delegate* unmanaged<nint, uint, uint, uint, int, uint, int>)&ResizeBuffersDetour, out originalResizeBuffers);
            Hook(swapChainTable, SlotResizeBuffers1, "ResizeBuffers1", (nint)(delegate* unmanaged<nint, uint, uint, uint, int, uint, nint, nint, int>)&ResizeBuffers1Detour, out originalResizeBuffers1);
        }
        catch (Exception e) { Log.Write($"RA01 resolution: the resize watch failed: {e.Message}"); }
    }

    // RA01: reads the function at 'slot', and if it lives inside dxgi.dll swaps in 'detour' (the original is kept in 'original'). Otherwise nothing is patched.
    // Cost - CPU: extremely low (a module lookup and a table write, once per hook at start-up); GPU: none.
    static void Hook(nint table, int slot, string name, nint detour, out nint original)
    {
        original = ((nint*)table)[slot];
        if (!PresentHook.InsideModule(original, name, "dxgi.dll")) { original = 0; return; }
        PresentHook.Patch(table, slot, detour);
    }

    // RA01: the game resolution for a headset eye of this size at this percent: both sides times the same number (so the headset's shape is kept), the longer side
    // never past MaxSide, rounded to even numbers. False when the headset size isn't known yet.
    // Cost - CPU: extremely low (a few multiplies); GPU: none.
    public static bool Size(int headsetWidth, int headsetHeight, float percent, out int width, out int height)
    {
        width = height = 0;
        if (headsetWidth <= 0 || headsetHeight <= 0) return false;
        float factor = MathF.Min(percent / 100f, (float)MaxSide / Math.Max(headsetWidth, headsetHeight));
        width = Math.Max(2, (int)MathF.Round(headsetWidth * factor / 2f) * 2);
        height = Math.Max(2, (int)MathF.Round(headsetHeight * factor / 2f) * 2);
        return true;
    }

    // RA01: called by PresentHook each presented frame: once the size the settings ask for has held still for SettleMs, rewrite the doorman's file (on a worker
    // thread - never a file write inside Present).
    // Cost - CPU: extremely low (a few compares and a clock read per frame); GPU: none.
    public static void OnPresent()
    {
        if (!Size(Tuning.HeadsetWidth, Tuning.HeadsetHeight, Tuning.GameResolutionPercent, out int w, out int h)) return;
        long now = Environment.TickCount64;
        if (w != seenWidth || h != seenHeight) { seenWidth = w; seenHeight = h; seenChangedMs = now; return; }
        if ((w == writtenWidth && h == writtenHeight) || now - seenChangedMs < SettleMs) return;
        if (Interlocked.CompareExchange(ref writing, 1, 0) != 0) return;
        writtenWidth = w; writtenHeight = h;
        Task.Run(() => WriteSize(w, h));
    }

    // RA01: writes "WIDTHxHEIGHT" into RA01_Resolution.txt beside the doorman in the game folder - only if the doorman is there (the launcher started this session).
    // Cost - CPU: low (one tiny file write, only when the slider settles); GPU: none.
    static void WriteSize(int width, int height)
    {
        try
        {
            string folder = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            if (!File.Exists(Path.Combine(folder, DoormanDll))) return;
            string path = Path.Combine(folder, SizeFile);
            if (File.Exists(path) && File.ReadAllText(path).Trim() == $"{width}x{height}") return; // RA01: the launcher already wrote this size at start
            File.WriteAllText(path, $"{width}x{height}");
            Log.Write($"RA01 resolution: game resolution set to {width}x{height} ({Tuning.GameResolutionPercent:F0}% of the headset's {Tuning.HeadsetWidth}x{Tuning.HeadsetHeight}) - takes hold at the next start, or now by applying any resolution in Options > Video");
        }
        catch (Exception e) { Log.Write($"RA01 resolution: couldn't tell the doorman the new size ({e.Message})"); }
        finally { writing = 0; }
    }

    // RA01: what the game calls instead of ResizeBuffers: logged, passed on unchanged.
    // Cost - CPU: extremely low (one log line, only when a resolution is applied); GPU: none.
    [UnmanagedCallersOnly]
    static int ResizeBuffersDetour(nint swapChain, uint count, uint width, uint height, int format, uint flags)
    {
        Note(width, height, "");
        try { return ((delegate* unmanaged<nint, uint, uint, uint, int, uint, int>)originalResizeBuffers)(swapChain, count, width, height, format, flags); }
        finally { depth--; }
    }

    // RA01: the same for ResizeBuffers1 (the newer form with two extra arguments).
    // Cost - CPU: extremely low (one log line, only when a resolution is applied); GPU: none.
    [UnmanagedCallersOnly]
    static int ResizeBuffers1Detour(nint swapChain, uint count, uint width, uint height, int format, uint flags, nint nodeMask, nint queues)
    {
        Note(width, height, " (ResizeBuffers1)");
        try { return ((delegate* unmanaged<nint, uint, uint, uint, int, uint, nint, nint, int>)originalResizeBuffers1)(swapChain, count, width, height, format, flags, nodeMask, queues); }
        finally { depth--; }
    }

    // RA01: M13-12 - how many resizes are running inside each other on this thread. At one start-up (2026-10-02 00:02) the game resized about 9,500 times in two
    // RA01: seconds without a single one finishing - a resize asked for from inside a resize (most likely the window and the game disagreeing about its size) - until
    // RA01: the thread ran out of stack. Each of those wrote a log line. Now: the outer resize is logged as before, a nested one only at depth 2, 10, 100 and 1000,
    // RA01: so the log names the loop without flooding (and without our file writing deepening the stack at every level).
    [ThreadStatic] static int depth;

    // RA01: counts one level in and logs it if it is the outer one or a depth worth reporting. The detour lowers the count when the real resize returns.
    // Cost - CPU: extremely low (an increment and a compare; a log line only as described); GPU: none.
    static void Note(uint width, uint height, string which)
    {
        int d = ++depth;
        if (d == 1) Log.Write($"RA01 resolution: the game resized its picture to {width}x{height}{which}");
        else if (d == 2 || d == 10 || d == 100 || d == 1000) Log.Write($"RA01 resolution: a resize to {width}x{height}{which} started INSIDE another one - {d} deep on this thread (the game is resizing in a loop)");
    }
}
