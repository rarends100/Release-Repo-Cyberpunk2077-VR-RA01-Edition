using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RA01.Doorman;

// RA01: M12-1 - the doorman. Cyberpunk2077.exe asks Windows for xinput9_1_0.dll for exactly one function, XInputGetState (the controller read), and Windows looks in
// the game's own folder first - so this DLL, copied into bin\x64 by the launcher, is loaded the moment the game starts, long before the launcher injects RA01.dll.
// That matters because the game builds its resolution list once, at start-up (M12-0c: no list request ever came after the late injection), and a resolution it
// doesn't list it won't draw at. The doorman's job is to have our resolution-list hook in place before that happens.
// A C# native DLL only wakes on the first call into it, so it wakes on the game's first controller read: it loads the real xinput9_1_0.dll from Windows'
// system folder (every controller read is passed straight through to it - controllers work exactly as before, and RA01.dll's own XInput hook still sits in front),
// then hooks the monitor's mode-list calls.
// M12-1b - all four of the real DLL's functions are passed through (M12-1 had only XInputGetState and the game crashed right after waking it).
// M12-1b's test showed it wakes in time (about 8 s in; the game asks for its list right after, and again whenever a resolution is applied in Options > Video).
// M12-2 - delivering the game resolution: every list request is answered with ONE mode - the size in RA01_Resolution.txt beside this DLL (written by the
// launcher at start from RA01_Settings.ini, and by RA01.dll when the slider settles) - so it is the only resolution the game can pick: it starts at it, and
// nothing in the game's own menus can move it off. No file (headset not seen yet) = the real list, untouched. The mode copies the real list's last entry
// (its refresh rate, format and scaling) with our width and height.
// Cost - CPU: extremely low (one start-up of a few milliseconds on the first controller read, a log line per mode-list request, then one pointer jump per
// controller read); GPU: none.
internal static unsafe class Doorman
{
    const int SlotRelease = 2;               // RA01: IUnknown
    const int SlotEnumOutputs = 7;           // RA01: IDXGIAdapter
    const int SlotGetDisplayModeList = 8;    // RA01: IDXGIOutput
    const int SlotEnumAdapters1 = 12;        // RA01: IDXGIFactory1
    const int SlotGetDisplayModeList1 = 19;  // RA01: IDXGIOutput1
    const string IidFactory = "50c83a1c-e072-4c48-87b0-3630fa36a6d0"; // RA01: IDXGIFactory2, as RA01.dll uses
    const int MoreData = unchecked((int)0x887A0003);                  // RA01: DXGI_ERROR_MORE_DATA: the caller's list was too short
    const string SizeFile = "RA01_Resolution.txt";                    // RA01: M12-2 - "WIDTHxHEIGHT", beside this DLL
    const uint NotConnected = 1167;          // RA01: ERROR_DEVICE_NOT_CONNECTED - what XInput answers if the real DLL could not be reached
    const int MaxLines = 40;

    static int woke;
    static nint realModule;                                        // RA01: the real xinput9_1_0.dll from Windows' system folder
    static nint realGetState, realSetState, realGetCaps, realGetGuids; // RA01: its four functions, looked up on first use
    static int firstGetState, firstSetState, firstGetCaps, firstGetGuids; // RA01: M12-1b - each function logs its first call once
    static nint originalList, originalList1;
    static int lines;

    // RA01: the four functions the real xinput9_1_0.dll offers. The game's .exe only imports XInputGetState, but the game (or a library inside it) can also look the
    // others up by name from whichever xinput9_1_0.dll is loaded - ours - so all four must be here, each passed straight to the real one (M12-1's crash:
    // only one was here). The first XInputGetState also wakes the doorman.
    // Cost - CPU: extremely low (a compare and one indirect call per call; the first call of each also looks the real function up and writes a log line); GPU: none.
    [UnmanagedCallersOnly(EntryPoint = "XInputGetState")]
    static uint XInputGetState(uint user, nint state)
    {
        if (woke == 0 && Interlocked.Exchange(ref woke, 1) == 0) Wake();
        nint real = Real(ref realGetState, "XInputGetState", ref firstGetState);
        return real == 0 ? NotConnected : ((delegate* unmanaged<uint, nint, uint>)real)(user, state);
    }

    [UnmanagedCallersOnly(EntryPoint = "XInputSetState")]
    static uint XInputSetState(uint user, nint vibration)
    {
        nint real = Real(ref realSetState, "XInputSetState", ref firstSetState);
        return real == 0 ? NotConnected : ((delegate* unmanaged<uint, nint, uint>)real)(user, vibration);
    }

    [UnmanagedCallersOnly(EntryPoint = "XInputGetCapabilities")]
    static uint XInputGetCapabilities(uint user, uint flags, nint capabilities)
    {
        nint real = Real(ref realGetCaps, "XInputGetCapabilities", ref firstGetCaps);
        return real == 0 ? NotConnected : ((delegate* unmanaged<uint, uint, nint, uint>)real)(user, flags, capabilities);
    }

    [UnmanagedCallersOnly(EntryPoint = "XInputGetDSoundAudioDeviceGuids")]
    static uint XInputGetDSoundAudioDeviceGuids(uint user, nint renderGuid, nint captureGuid)
    {
        nint real = Real(ref realGetGuids, "XInputGetDSoundAudioDeviceGuids", ref firstGetGuids);
        return real == 0 ? NotConnected : ((delegate* unmanaged<uint, nint, nint, uint>)real)(user, renderGuid, captureGuid);
    }

    // RA01: M12-1b - the real function by name: the real DLL is loaded once, each function looked up once and kept. Its first call is logged (with the time since the
    // game started), so a crash right after one of them points straight at it.
    // Cost - CPU: extremely low (a read per call; once per function a DLL lookup and a log line); GPU: none.
    static nint Real(ref nint cache, string name, ref int first)
    {
        nint real = cache;
        if (real == 0)
        {
            if (realModule == 0) realModule = LoadLibraryW(Path.Combine(Environment.SystemDirectory, "xinput9_1_0.dll"));
            real = realModule == 0 ? 0 : GetProcAddress(realModule, name);
            cache = real;
        }
        if (first == 0 && Interlocked.Exchange(ref first, 1) == 0)
            Say($"first {name} call, {Uptime():F1} s after the game started{(real == 0 ? " - the real one was NOT found, answered 'not connected'" : "")}");
        return real;
    }

    // RA01: a name the launcher looks for to tell our doorman apart from any other xinput9_1_0.dll (another mod's) before it replaces or removes one.
    // Cost - CPU: extremely low (never called by the game; the launcher only looks it up); GPU: none.
    [UnmanagedCallersOnly(EntryPoint = "RA01_Doorman")]
    static int Marker() => 1;

    // RA01: first controller read: hook the mode-list calls. Never throws into the game.
    // Cost - CPU: low, once (a throwaway DXGI factory, adapter and output, two table writes - a few milliseconds); GPU: none.
    static void Wake()
    {
        try
        {
            Say($"awake on the game's first controller read, {Uptime():F1} s after the game started");
            HookModeLists();
        }
        catch (Exception e) { Say($"wake failed: {e.Message}"); }
    }

    // RA01: hooks GetDisplayModeList / GetDisplayModeList1 in the monitor output's function table (shared by every output object), found through a throwaway factory,
    // adapter and output that are released again - the same way RA01.dll's ResolutionHook did it.
    // Cost - CPU: low, once (a few DXGI calls and two table writes); GPU: none.
    static void HookModeLists()
    {
        nint factory = 0, adapter = 0, output = 0;
        try
        {
            Guid iid = new(IidFactory);
            if (CreateDXGIFactory1(&iid, &factory) < 0) { Say("no DXGI factory - mode lists not watched"); return; }
            if (((delegate* unmanaged<nint, uint, nint*, int>)Slot(factory, SlotEnumAdapters1))(factory, 0, &adapter) < 0) { Say("no adapter - mode lists not watched"); return; }
            if (((delegate* unmanaged<nint, uint, nint*, int>)Slot(adapter, SlotEnumOutputs))(adapter, 0, &output) < 0) { Say("no monitor output - mode lists not watched"); return; }
            nint table = *(nint*)output;
            originalList = ((nint*)table)[SlotGetDisplayModeList];   // RA01: M12-1b - the real function is noted BEFORE the detour goes in, so a call in between can never find it empty
            originalList1 = ((nint*)table)[SlotGetDisplayModeList1];
            Patch(table, SlotGetDisplayModeList, (nint)(delegate* unmanaged<nint, int, uint, uint*, byte*, int>)&ListDetour);
            Patch(table, SlotGetDisplayModeList1, (nint)(delegate* unmanaged<nint, int, uint, uint*, byte*, int>)&List1Detour);
            Say("watching the game's resolution-list requests");
        }
        finally { Release(output); Release(adapter); Release(factory); }
    }

    // RA01: the mode-list calls the game makes (28-byte DXGI_MODE_DESC / 32-byte DXGI_MODE_DESC1 entries).
    // Cost - CPU: extremely low (as List); GPU: none.
    [UnmanagedCallersOnly]
    static int ListDetour(nint output, int format, uint flags, uint* count, byte* modes) => List(output, format, flags, count, modes, 28, originalList, "GetDisplayModeList");

    [UnmanagedCallersOnly]
    static int List1Detour(nint output, int format, uint flags, uint* count, byte* modes) => List(output, format, flags, count, modes, 32, originalList1, "GetDisplayModeList1");

    // RA01: M12-2 - the shared body of both. A caller first asks how many modes there are (modes = null), then asks for them with room for that many. With a size to
    // deliver, the count is answered 1 and the one entry is a copy of the real list's last mode carrying our size. Anything odd is passed through untouched.
    // Cost - CPU: extremely low (one small file read, the real list fetched once into a scratch buffer of about a hundred entries, a log line; only when the game
    // builds its list); GPU: none.
    static int List(nint output, int format, uint flags, uint* count, byte* modes, int size, nint original, string name)
    {
        var call = (delegate* unmanaged<nint, int, uint, uint*, byte*, int>)original;
        if (count == null || !Wanted(out int w, out int h))
        {
            int plain = call(output, format, flags, count, modes);
            Say($"{name} {Uptime():F1} s in: no game resolution to deliver (no {SizeFile} yet) - the real list ({(count == null ? 0 : *count)} modes) was given");
            return plain;
        }
        uint real = 0;
        int result = call(output, format, flags, &real, null);
        if (result != 0 || real == 0) return call(output, format, flags, count, modes); // RA01: the monitor has nothing for this format - nothing to copy, pass through
        if (modes == null) { *count = 1; return 0; }
        if (*count < 1) { *count = 1; return MoreData; }
        byte* all = (byte*)NativeMemory.Alloc((nuint)(real * size));
        try
        {
            result = call(output, format, flags, &real, all);
            if (result != 0 || real == 0) return call(output, format, flags, count, modes);
            Buffer.MemoryCopy(all + (real - 1) * size, modes, size, size);
            *(uint*)modes = (uint)w; *(uint*)(modes + 4) = (uint)h;
            *count = 1;
            Say($"{name} {Uptime():F1} s in: delivered {w}x{h} as the only resolution (format {format})");
            return 0;
        }
        finally { NativeMemory.Free(all); }
    }

    // RA01: M12-2 - the size in RA01_Resolution.txt beside this DLL ("WIDTHxHEIGHT"), read fresh each time so a slider change reaches the next list request.
    // Cost - CPU: extremely low (one tiny file read, only when the game builds its list); GPU: none.
    static bool Wanted(out int width, out int height)
    {
        width = height = 0;
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", SizeFile);
            if (!File.Exists(path)) return false;
            string[] parts = File.ReadAllText(path).Trim().Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && width >= 320 && height >= 240 && width <= 8192 && height <= 8192;
        }
        catch { return false; }
    }

    // RA01: seconds since the game process started.
    // Cost - CPU: extremely low (one process-time read); GPU: none.
    static double Uptime()
    {
        try { return (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds; } catch { return -1; }
    }

    // RA01: writes one function pointer into a function table (made writable for that one write).
    // Cost - CPU: extremely low (two protection changes and one write); GPU: none.
    static void Patch(nint table, int slot, nint detour)
    {
        nint* at = (nint*)table + slot;
        VirtualProtect(at, 8, 0x04, out uint old); // RA01: PAGE_READWRITE
        *at = detour;
        VirtualProtect(at, 8, old, out _);
    }

    // RA01: the function at 'slot' in a COM object's function table, and IUnknown::Release.
    // Cost - CPU: extremely low (two reads / one call); GPU: none.
    static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
    static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged<nint, uint>)Slot(obj, SlotRelease))(obj); }

    // RA01: one "RA01 doorman:" line in RA01.dll's own log (%LOCALAPPDATA%\RA01\RA01_log.txt), at most MaxLines per session. Never throws.
    // Cost - CPU: low (an append to a file, under a millisecond, a handful of times per session); GPU: none.
    static void Say(string text)
    {
        try
        {
            if (Interlocked.Increment(ref lines) > MaxLines) return;
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RA01", "RA01_log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [thread {Environment.CurrentManagedThreadId}] RA01 doorman: {text}{Environment.NewLine}");
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern nint GetProcAddress(nint module, string name);
    [DllImport("kernel32.dll")] static extern bool VirtualProtect(void* address, nuint size, uint protect, out uint old);
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(Guid* riid, nint* factory);
}
