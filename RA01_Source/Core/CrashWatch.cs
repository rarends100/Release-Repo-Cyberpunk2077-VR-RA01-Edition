using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M13-11 - the BLACK BOX, for crashes that leave no record (the M13-9 melee crash: no Windows error, no Cyberpunk crash report, no log line). Like an aircraft's
// flight recorder it is a small box that survives the crash: a 4 KB file, RA01_blackbox.bin beside the log, opened as a "mapped" file - its bytes ARE our memory,
// and Windows itself writes them to disk even if the game is killed on the spot (they live in Windows' own file cache, not in the game). Two things go in it:
//  - the BREADCRUMB: Mark("text") - any part of the mod says where it is (copied straight into the box: no allocation, no file call, safe at any speed);
//  - the LAST SERIOUS FAULT: a 175-byte machine-code watcher (not C#) that Windows calls first for every fault in the process. It notes the fault's code, address,
//    details and thread in the box and steps aside. It is machine code because .NET (NativeAOT) cannot safely run C# for a fault that happened inside C# itself:
//    the M13-10 C# watcher, first in line, would have ended the game silently on exactly such a fault - before it could write anything.
// At the next start, Install logs what the box held when the last session ended (its last breadcrumb, its last fault, named by file and offset), then clears it.
// M55 - the watcher also copies the faulting thread's stack (up to 128 entries above where it stopped, never past the thread's own stack end) and its 17 main
// registers into the box, so the log can show the CALL TRAIL: every stack entry that points into the game or RA01 - the return addresses of the functions
// that led to the fault, newest first. Like footprints in the snow back to whoever walked up to the broken shelf (the crash line alone only names the shelf).
// While the game runs, a watcher thread logs each new fault by name (if the game survives it, it was a harmless one the game handled itself).
// A session that ends with no fault in the box died by a "fast-fail" (Windows' instant stop that skips every watcher) - in a NativeAOT mod that is usually C# reading
// bad game memory, or an error escaping a hook - and the breadcrumb then says where.
// Cost - CPU: extremely low (a few bytes copied per Mark; the machine-code watcher is a dozen instructions per fault; the watcher thread wakes 5 times a second
// and compares one number); GPU: none.
internal static unsafe class CrashWatch
{
    // RA01: the box's layout (byte offsets). The machine code writes the first block; C# writes the rest.
    const int FaultCode = 0x00, FaultCount = 0x04, FaultAddress = 0x08, FaultInfo0 = 0x10, FaultInfo1 = 0x18, FaultThread = 0x20;
    const int ExeBase = 0x40, ExeSize = 0x48, ModBase = 0x50, ModSize = 0x58, StartedAt = 0x60, MarkedAt = 0x68;
    const int Crumb = 0x80, CrumbMax = 127;
    const int StackTop = 0x100, StackAt = 0x108, StackMax = 128, RegsAt = 0x508, StackCount = 0x590, TrailMax = 16; // RA01: M55 - the call trail block (the machine code writes it)
    const int BoxSize = 4096;
    const int WatchMs = 200;

    // RA01: the machine-code watcher, assembled by hand from (rcx = the fault's EXCEPTION_POINTERS):
    // RA01:   mov rax,[rcx] / mov edx,[rax]                  - the fault record, its code
    // RA01:   if (code & 0xF0000000) != 0xC0000000: skip      - only errors (0xC...); C++ exceptions (0xE...) and breakpoints are left alone
    // RA01:   mov r9, <box>                                  - the 8 bytes at StubBoxAt are filled in with the box's address
    // RA01:   box+0 = code, box+8 = address, box+0x10/0x18 = the two detail words, box+0x20 = thread id (gs:[0x48])
    // RA01:   M55: box+0x100 = the stack pointer (CONTEXT+0x98); box+0x108.. = up to 128 stack entries, stopping at the thread's stack end (gs:[8]); box+0x590 = how
    // RA01:   many; box+0x508.. = 17 registers (CONTEXT+0x78: rax rcx rdx rbx rsp rbp rsi rdi r8-r15 rip). Only scratch registers are used, nothing is pushed.
    // RA01:   then box+4 += 1 (atomic) last
    // RA01:   xor eax,eax / ret                              - EXCEPTION_CONTINUE_SEARCH: the fault goes on to the game and .NET exactly as before
    // RA01: (M55: checked with a disassembler - every jump lands where it should.)
    static readonly byte[] Stub =
    {
        0x48, 0x8B, 0x01, 0x8B, 0x10, 0x41, 0x89, 0xD0, 0x41, 0x81, 0xE0, 0x00, 0x00, 0x00, 0xF0, 0x41, 0x81, 0xF8, 0x00, 0x00, 0x00, 0xC0,
        0x74, 0x03, 0x31, 0xC0, 0xC3,                       // RA01: je +3 (an error: record it), else xor eax,eax / ret (M55: the far jump no longer fits in one byte)
        0x49, 0xB9, 0, 0, 0, 0, 0, 0, 0, 0,                  // RA01: movabs r9, <box>
        0x41, 0x89, 0x11,                                   // RA01: mov [r9], edx
        0x4C, 0x8B, 0x40, 0x10, 0x4D, 0x89, 0x41, 0x08,     // RA01: address
        0x4C, 0x8B, 0x40, 0x20, 0x4D, 0x89, 0x41, 0x10,     // RA01: ExceptionInformation[0] (0 read, 1 write, 8 run)
        0x4C, 0x8B, 0x40, 0x28, 0x4D, 0x89, 0x41, 0x18,     // RA01: ExceptionInformation[1] (the memory it touched)
        0x65, 0x4C, 0x8B, 0x04, 0x25, 0x48, 0x00, 0x00, 0x00, 0x45, 0x89, 0x41, 0x20, // RA01: thread id
        0x4C, 0x8B, 0x51, 0x08,                             // RA01: M55 - mov r10,[rcx+8]            the CONTEXT
        0x4D, 0x8B, 0x9A, 0x98, 0x00, 0x00, 0x00,           // RA01: M55 - mov r11,[r10+0x98]         its rsp
        0x4D, 0x89, 0x99, 0x00, 0x01, 0x00, 0x00,           // RA01: M55 - mov [r9+0x100],r11
        0x65, 0x48, 0x8B, 0x14, 0x25, 0x08, 0x00, 0x00, 0x00, // RA01: M55 - mov rdx,gs:[8]           the thread's stack end
        0x31, 0xC0,                                         // RA01: M55 - xor eax,eax
        0x4D, 0x8D, 0x04, 0xC3,                             // RA01: M55 - loop: lea r8,[r11+rax*8]
        0x49, 0x39, 0xD0, 0x73, 0x14,                       // RA01: M55 -   cmp r8,rdx / jae done (never read past the stack's end)
        0x4D, 0x8B, 0x00,                                   // RA01: M55 -   mov r8,[r8]
        0x4D, 0x89, 0x84, 0xC1, 0x08, 0x01, 0x00, 0x00,     // RA01: M55 -   mov [r9+rax*8+0x108],r8
        0xFF, 0xC0, 0x3D, 0x80, 0x00, 0x00, 0x00, 0x72, 0xE3, // RA01: M55 -   inc eax / cmp eax,128 / jb loop
        0x41, 0x89, 0x81, 0x90, 0x05, 0x00, 0x00,           // RA01: M55 - done: mov [r9+0x590],eax   how many entries
        0x31, 0xC0,                                         // RA01: M55 - xor eax,eax
        0x4D, 0x8B, 0x84, 0xC2, 0x78, 0x00, 0x00, 0x00,     // RA01: M55 - regs: mov r8,[r10+rax*8+0x78]
        0x4D, 0x89, 0x84, 0xC1, 0x08, 0x05, 0x00, 0x00,     // RA01: M55 -   mov [r9+rax*8+0x508],r8
        0xFF, 0xC0, 0x83, 0xF8, 0x11, 0x72, 0xE9,           // RA01: M55 -   inc eax / cmp eax,17 / jb regs
        0xF0, 0x41, 0xFF, 0x41, 0x04,                       // RA01: lock inc dword [r9+4] - the count goes up last, so a reader sees a complete record
        0x31, 0xC0, 0xC3,
    };
    const int StubBoxAt = 29;

    static byte* box;
    static MemoryMappedFile? file;
    static MemoryMappedViewAccessor? view;
    static int installed;
    static nint lastFaultAt;                 // RA01: M13-22 - the last session's fault address, named again a minute in
    const int NameAgainMs = 60000;

    [DllImport("kernel32.dll")] static extern nint AddVectoredExceptionHandler(uint first, nint handler);

    // RA01: the breadcrumb: where the mod is now. Copies up to CrumbMax characters into the box (plain text, no allocation). Safe from any thread at any speed.
    // Cost - CPU: extremely low (copies a few dozen bytes); GPU: none.
    public static void Mark(string text)
    {
        byte* b = box;
        if (b == null) return;
        int n = Math.Min(text.Length, CrumbMax);
        for (int i = 0; i < n; i++) b[Crumb + i] = (byte)(text[i] < 128 ? text[i] : '?');
        b[Crumb + n] = 0;
        *(long*)(b + MarkedAt) = Environment.TickCount64;
    }

    // RA01: Call first thing at start-up: opens the box, logs what the last session left in it, clears it and puts the machine-code watcher first in Windows' line.
    // Cost - CPU: extremely low (one small file opened and mapped, one 82-byte copy, one Windows call, once; then the watcher thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            string path = Path.Combine(ModFolders.Logs, "RA01_blackbox.bin"); // RA01: M20-4 - beside the log
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            file = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, BoxSize, MemoryMappedFileAccess.ReadWrite);
            view = file.CreateViewAccessor(0, BoxSize);
            byte* b = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref b);
            b += view.PointerOffset;

            if (*(long*)(b + StartedAt) != 0)
            {
                Log.Write("RA01 black box: the last session ended with - " + Describe(b, true));
                if (*(int*)(b + FaultCount) != 0) lastFaultAt = (nint)(*(long*)(b + FaultAddress)); // RA01: M13-22 - named again once the game has loaded its drivers (Watch)
            }
            new Span<byte>(b, BoxSize).Clear();
            *(long*)(b + StartedAt) = DateTime.Now.ToBinary();
            nint exe = GetModuleHandleW(null!);
            *(long*)(b + ExeBase) = exe; *(long*)(b + ExeSize) = ImageSize(exe);
            if (GetModuleHandleExW(0x6, (nint)(delegate*<string, void>)&Mark, out nint mod)) { *(long*)(b + ModBase) = mod; *(long*)(b + ModSize) = ImageSize(mod); } // RA01: FROM_ADDRESS | UNCHANGED_REFCOUNT
            box = b;
            Mark("start-up");

            byte* code = (byte*)VirtualAlloc(0, 0x1000, 0x3000, 0x04); // RA01: MEM_COMMIT|MEM_RESERVE, read/write while it is filled in
            if (code == null) { Log.Write("RA01 black box: no memory for the fault watcher - breadcrumbs only"); return; }
            Stub.CopyTo(new Span<byte>(code, Stub.Length));
            *(long*)(code + StubBoxAt) = (long)b;
            VirtualProtect((nint)code, 0x1000, 0x20, out _); // RA01: PAGE_EXECUTE_READ - runnable, no longer writable
            bool on = AddVectoredExceptionHandler(1, (nint)code) != 0;
            new Thread(Watch) { IsBackground = true, Name = "RA01 black box" }.Start();
            Log.Write(on ? $"RA01 black box: recording to {path}" : "RA01 black box: Windows refused the fault watcher - breadcrumbs only");
        }
        catch (Exception e) { Log.Write($"RA01 black box: could not start ({e.Message}) - no crash recording this session"); }
    }

    // RA01: the watcher thread: when the fault count moves, log the newest fault by name. The game was still alive to run this, so it is usually a harmless one.
    // M13-22 - also, a minute in, names the file the LAST session's fault was in (the crash on quit), once.
    // Cost - CPU: extremely low (wakes 5 times a second to compare one number); GPU: none.
    static void Watch()
    {
        int seen = 0, logged = 0;
        long nameAt = Environment.TickCount64 + NameAgainMs;
        while (true)
        {
            Thread.Sleep(WatchMs);
            QuitWatch();                                                // RA01: M49-0
            if (lastFaultAt != 0 && Environment.TickCount64 >= nameAt) // RA01: M13-22 - by now the game has loaded its graphics, headset and input files
            {
                Log.Write($"RA01 black box: the last session's fault address 0x{lastFaultAt:X} is, in this session, {Where(box, lastFaultAt, false)} (same file as then unless the PC was restarted)");
                lastFaultAt = 0;
            }
            int count = Volatile.Read(ref *(int*)(box + FaultCount));
            if (count == seen) continue;
            seen = count;
            if (++logged <= 30) Log.Write($"RA01 black box: fault noticed - {Describe(box, false)}");
        }
    }

    // RA01: M49-0 - the quit watch's notes (see QuitWatch).
    const long QuitPresentGapMs = 1000;
    static long lastFrames = -1, framesSameSince;
    static int lastEngineState = -1;
    static bool presentsStoppedSaid, windowGoneSaid;
    // RA01: M49-0 - QUIT WATCH (log only, always on - the Crash log is): the crash on quit happens inside the VR runtime's own thread (Virtual Desktop's
    // LibOVRRT, in Direct3D 11 and the graphics driver - the game's crash dump, 2026-10-11 08:48) while the game tears down, because RA01's OpenXR session is
    // still open then. To close it at the right moment we first need the earliest sign that the game is quitting. Five times a second this notes, once
    // each, with the time: the game stopped presenting frames for QuitPresentGapMs (not on a loading screen), the engine's state left "running" (3), and
    // the game window is gone. Builder picture: three tripwires across the exit - whichever snaps first tells us when the crew starts leaving.
    // Cost - CPU: extremely low (three compares, five times a second); GPU: none.
    static void QuitWatch()
    {
        long now = Environment.TickCount64, frames = PresentHook.Frames;
        if (frames != lastFrames)
        {
            if (presentsStoppedSaid) Log.Write("RA01 quit watch: frames are being presented again");
            lastFrames = frames; framesSameSince = now; presentsStoppedSaid = false;
        }
        else if (frames > 0 && !presentsStoppedSaid && now - framesSameSince > QuitPresentGapMs && !GameAnchor.Loading)
        {
            presentsStoppedSaid = true;
            Log.Write($"RA01 quit watch: the game stopped presenting frames {now - framesSameSince} ms ago (not a loading screen)");
        }
        int state = GameAnchor.EngineState;
        if (state != lastEngineState)
        {
            if (lastEngineState >= 0) Log.Write($"RA01 quit watch: engine state {lastEngineState} -> {state} (3 = running)");
            lastEngineState = state;
        }
        nint window = FrameCopy.GameWindow;
        if (window != 0 && !windowGoneSaid && !IsWindow(window)) { windowGoneSaid = true; Log.Write("RA01 quit watch: the game window is gone"); }
    }

    // RA01: one readable line from the box: the last fault (if any) by file and offset, and the last breadcrumb with how long before the end it was set.
    // Cost - CPU: extremely low (a module lookup and one string, rarely); GPU: none.
    static string Describe(byte* b, bool lastSession)
    {
        uint code = *(uint*)(b + FaultCode);
        int count = *(int*)(b + FaultCount);
        nint at = (nint)(*(long*)(b + FaultAddress));
        long info0 = *(long*)(b + FaultInfo0), info1 = *(long*)(b + FaultInfo1);
        string crumb = Marshal.PtrToStringAnsi((nint)(b + Crumb)) ?? "";
        string fault = count == 0 ? "no serious fault recorded (if the game closed by itself, it was a fast-fail: see CrashWatch.cs)" :
            $"{count} serious fault(s), the last {Kind(code)} (0x{code:X8}) in {Where(b, at, lastSession)}{(code == 0xC0000005 ? $", {(info0 == 0 ? "reading" : info0 == 1 ? "writing" : "running")} 0x{info1:X}" : "")}, thread {*(uint*)(b + FaultThread)}";
        return $"{fault}{(count == 0 ? "" : Trail(b))}; last breadcrumb: \"{crumb}\"";
    }

    // RA01: M55 - the last fault's CALL TRAIL: the stack entries that point into the game or RA01.dll (the game and RA01 ranges noted at this box's start), newest
    // first, at most TrailMax - most are return addresses of the functions that led to the fault (a few can be stored function pointers) - then the registers.
    // Cost - CPU: extremely low (up to 128 compares and one string, only when a fault is described); GPU: none.
    static string Trail(byte* b)
    {
        int n = Math.Clamp(*(int*)(b + StackCount), 0, StackMax);
        long exe = *(long*)(b + ExeBase), exeEnd = exe + *(long*)(b + ExeSize), mod = *(long*)(b + ModBase), modEnd = mod + *(long*)(b + ModSize);
        var text = new System.Text.StringBuilder("; call trail (newest first):");
        int shown = 0;
        for (int i = 0; i < n && shown < TrailMax; i++)
        {
            long v = *(long*)(b + StackAt + i * 8);
            if (v >= exe && v < exeEnd) { text.Append($" exe+0x{v - exe:X}"); shown++; }
            else if (v >= mod && v < modEnd) { text.Append($" RA01+0x{v - mod:X}"); shown++; }
        }
        if (shown == 0) text.Append(n == 0 ? " not recorded" : " none found");
        text.Append($" ({n} stack entries from 0x{*(long*)(b + StackTop):X}); registers:");
        for (int i = 0; i < RegisterNames.Length; i++) text.Append($" {RegisterNames[i]}=0x{*(long*)(b + RegsAt + i * 8):X}");
        return text.ToString();
    }
    static readonly string[] RegisterNames = { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15", "rip" }; // RA01: M55 - CONTEXT order

    // RA01: a fault address as "file +offset": this session asks Windows; for the last session (its files may sit elsewhere now) the game and RA01.dll ranges noted at its start are used.
    // M13-22 - anything else from the last session (the crash-on-quit: a Windows or driver file) is looked up in this session too, since Windows' own files keep
    // their addresses until a reboot - so the log can say WHICH file it was.
    // Cost - CPU: extremely low (a Windows lookup or two compares); GPU: none.
    static string Where(byte* b, nint at, bool lastSession)
    {
        if (!lastSession)
        {
            if (!GetModuleHandleExW(0x6, at, out nint module) || module == 0) return $"0x{at:X}";
            char* name = stackalloc char[260];
            uint n = GetModuleFileNameW(module, name, 260);
            return $"{Path.GetFileName(new string(name, 0, (int)n))} +0x{at - module:X}";
        }
        long exe = *(long*)(b + ExeBase), mod = *(long*)(b + ModBase);
        if (at >= exe && at < exe + *(long*)(b + ExeSize)) return $"Cyberpunk2077.exe +0x{at - exe:X}";
        if (at >= mod && at < mod + *(long*)(b + ModSize)) return $"RA01.dll +0x{at - mod:X}";
        string now = Where(b, at, false); // RA01: M13-22 - Windows keeps its own files at the same address until a reboot, so this session's map can usually name it
        return now.StartsWith("0x") ? $"0x{at:X} (a Windows or driver file not loaded now)" : $"0x{at:X} = {now} (named from this session - right unless the PC was restarted in between)";
    }

    static string Kind(uint code) => code switch
    {
        0xC0000005 => "bad memory access", 0xC00000FD => "stack overflow", 0xC000001D => "illegal instruction", 0xC0000374 => "damaged memory (heap)",
        0xC0000409 => "fast-fail", 0xC0000094 => "divide by zero", _ => "error",
    };

    // RA01: a loaded file's size in memory, from its own header (SizeOfImage).
    // Cost - CPU: extremely low (two reads); GPU: none.
    static long ImageSize(nint module) => module == 0 ? 0 : *(uint*)(module + *(int*)(module + 0x3C) + 80);
}
