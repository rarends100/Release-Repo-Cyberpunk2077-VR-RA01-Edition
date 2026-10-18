using System.Runtime.InteropServices;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M13-6 - READ-ONLY crosshair probe: the first step toward switching the game's own crosshair off (instead of M13-2's centre wipe, removed in M13-5).
// In the 2.31 exe the game's crosshair manager (class gameuiCrosshairContainerController) has a function the scripts call whenever the crosshair may need to
// show or hide - its script name is UpdateCrosshairVisibility. Its native code starts with bytes found exactly once in the exe (Visibility below), and the
// manager arrives in rcx. Read off its code: the crosshair currently on screen is held at +0x168, a brightness value sits at +0x278 and on/off switches at
// +0x269 and +0x280 to +0x286.
// This probe hooks the start of that function (same detour shape as PoseHook), only to note WHICH manager the game used - nothing is changed and the game's
// own call runs on as normal. A background check then reads those fields once a second through ReadProcessMemory (a gone object fails a read, never crashes)
// and logs a line only when something changed (M13-8 dropped the widget value scan - the switch was found).
// Drawing guns, cyberware and nothing, then matching the log lines to what was on screen, shows which value means "visible" - the next step sets it.
// M13-8 - the SWITCH. The M13-6 log and the function's own code show what it does: if the manager's "reduced" switch (+0x283) is off, the crosshair's
// opacity is set to 1.0; if on, to the reduced opacity stored at +0x278 (0.10 - the game's faded crosshair). So while HudLayer.HideCrosshair is set (Actions'
// rule), just before the game's own function runs, the switch is turned on and the reduced opacity set to 0 - the game then fades its crosshair to nothing with
// its own code: no holes, nothing else on the HUD touched. The game's values are kept and put back before the first update after the rule says "show"
// (if the game flipped the switch itself meanwhile, its new value is kept). Every crosshair type has its own manager; up to MaxManagers are tracked at once.
// It takes hold the next time the game updates its crosshair (each draw, aim, or every few seconds), not the instant the rule changes.
// M13-9 tried re-running the game's update from the mod when the rule changed: it crashed the game on a save load (the call landed while the HUD was being
// rebuilt) and was removed. Only the game ever runs its own crosshair update.
// M13-14 - two more doors into the same manager (found in the exe, see claude/RA01_M13-13 doc): "apply crosshair" (RVA 0x5B8C18) lays the crosshair out
// and sets its opacity from the same two fields BEFORE handing on to the visibility update - so it now gets the same switch first (OnApply), and the opacity it
// sets is already ours (no flash of a fully visible crosshair). "Set crosshair" (RVA 0x5B9EE8, found from apply's place and checked byte for byte) puts a
// new crosshair in - it only LOGS which manager and when, to time the gap after a save load (the 02:44 test: about 6 s visible). Log-only, temporary.
// Cost - CPU: extremely low (the hook: a store, a short table walk and up to three small writes per call, only when the game updates its crosshair; the
// check: a few tiny safe reads once a second; the set-crosshair note: a compare and, for a new manager, one log line); GPU: none.
internal static unsafe class CrosshairProbe
{
    // RA01: the first 24 bytes of the crosshair manager's visibility update (RVA 0x5B8D28 in 2.31) - found exactly once
    static ReadOnlySpan<byte> Visibility => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x7C, 0x24, 0x18, 0x55, 0x48, 0x8B, 0xEC, 0x48, 0x81, 0xEC, 0x80, 0x00, 0x00, 0x00, 0x33, 0xFF, 0x48 };
    // RA01: M13-14 - "apply crosshair" (RVA 0x5B8C18 in 2.31): its first 16 bytes, found exactly once; and "set crosshair" sits ApplyToSet bytes after it
    static ReadOnlySpan<byte> Apply => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x55, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x70, 0x48, 0x8B, 0xD9 };
    static ReadOnlySpan<byte> SetCrosshair => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x48, 0x89, 0x7C, 0x24, 0x18, 0x55 };
    const int ApplyToSet = 0x12D0;
    static ReadOnlySpan<byte> MovRsp10 => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x10 }; // RA01: the instruction each jump replaces: mov [rsp+10h/18h/08h], rbx
    static ReadOnlySpan<byte> MovRsp18 => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x18 };
    static ReadOnlySpan<byte> MovRsp08 => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08 };
    const int WidgetAt = 0x168, SwitchAt = 0x269, BrightnessAt = 0x278, FlagsAt = 0x280;
    const int ReducedAt = 0x283;     // RA01: M13-8 - the "reduced crosshair" switch the game's function reads first
                                     // RA01: M44-2 - Force now finds it and BrightnessAt by NAME on each manager's own class (inSafeState, crosshairReducedOpacity -
                                     // RA01: the field probe's 05:09 result); these two numbers are 2.31's places, kept for the log-only reads and as the 2.31 fallback
    const int FieldSlots = 8;                                              // RA01: M44-2 - manager classes remembered (there are only a few kinds)
    static readonly nint[] fieldClass = new nint[FieldSlots];
    static readonly int[] reducedOf = new int[FieldSlots], opacityOf = new int[FieldSlots];
    static int fieldNext;
    const int CrosshairInfoAt = 0x50; // RA01: M13-21 - a crosshair's info (its +0x48 is handed to the widget spawn)
    const int MaxManagers = 32;
    static readonly nint[] forced = new nint[MaxManagers];       // RA01: M13-8 - managers we switched off (0 = free entry)
    static readonly byte[] savedReduced = new byte[MaxManagers]; // RA01: their own switch and reduced opacity, to put back
    static readonly float[] savedOpacity = new float[MaxManagers];
    static readonly long[] lastCall = new long[MaxManagers];     // RA01: when each was last updated - a full table reuses the longest-silent entry (a gone manager)

    // RA01: the detour, as PoseHook's: keep every register, call OnVisibility with the manager still in rcx, put everything back, run the replaced instruction, go on.
    static ReadOnlySpan<byte> TrampolineHead => new byte[]
    {
        0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, // RA01: pushfq, push rax rcx rdx r8 r9 r10 r11
        0x55, 0x48, 0x89, 0xE5,                                                  // RA01: push rbp; mov rbp, rsp
        0x48, 0x83, 0xEC, 0x60,                                                  // RA01: sub rsp, 60h - room for xmm0 to xmm5
        0x0F, 0x11, 0x45, 0xA0, 0x0F, 0x11, 0x4D, 0xB0, 0x0F, 0x11, 0x55, 0xC0,  // RA01: save xmm0, xmm1, xmm2
        0x0F, 0x11, 0x5D, 0xD0, 0x0F, 0x11, 0x65, 0xE0, 0x0F, 0x11, 0x6D, 0xF0,  // RA01: save xmm3, xmm4, xmm5
        0x48, 0x83, 0xE4, 0xF0, 0x48, 0x83, 0xEC, 0x20,                          // RA01: line the stack up and reserve the 32 bytes Windows expects
        0x48, 0xB8,                                                              // RA01: mov rax, <8 bytes: OnVisibility>
    };
    static ReadOnlySpan<byte> TrampolineTail => new byte[]
    {
        0xFF, 0xD0,                                                              // RA01: call rax
        0x0F, 0x10, 0x45, 0xA0, 0x0F, 0x10, 0x4D, 0xB0, 0x0F, 0x10, 0x55, 0xC0,  // RA01: put xmm0 to xmm2 back
        0x0F, 0x10, 0x5D, 0xD0, 0x0F, 0x10, 0x65, 0xE0, 0x0F, 0x10, 0x6D, 0xF0,  // RA01: put xmm3 to xmm5 back
        0x48, 0x89, 0xEC, 0x5D,                                                  // RA01: mov rsp, rbp; pop rbp
        0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D,  // RA01: pop r11 r10 r9 r8 rdx rcx rax, popfq
    };                                                                           // RA01: then the replaced instruction, then jmp [rip+0] back (Hook adds both)
    static ReadOnlySpan<byte> JumpBack => new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    // RA01: M13-37 - mov r9, [rbp+48h]: after the 8 saves (flags, rax, rcx, rdx, r8, r9, r10, r11) and rbp, the game's return address sits 72 bytes above rbp
    // RA01: (true only for a hook on a function's very first instruction, where nothing else has been pushed yet).
    static ReadOnlySpan<byte> MovR9ReturnAddress => new byte[] { 0x4C, 0x8B, 0x4D, 0x48 };
    static ReadOnlySpan<byte> MovRcxReturnAddress => new byte[] { 0x48, 0x8B, 0x4D, 0x48 }; // RA01: M13-44 - mov rcx, [rbp+48h]: the same, into rcx (put back after)
    static ReadOnlySpan<byte> LeaRcxSiteStack => new byte[] { 0x48, 0x8D, 0x4D, 0x48 };     // RA01: M13-55 - lea rcx, [rbp+48h]: the game's own stack pointer at the hooked spot

    static readonly nint Self = GetCurrentProcess();
    static nint manager;          // RA01: the last manager the game updated (0 = none yet)
    // RA01: M15-0 - the last crosshair manager the game updated (0 = none yet): GameHud climbs from it to the HUD's outermost widget.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    internal static nint Manager => Volatile.Read(ref manager);
    static nint lastSetManager;   // RA01: M13-14 - the manager last seen putting a new crosshair in (log-only)
    static int setLines;
    static int calls;             // RA01: updates since the last log line
    static int installed;

    // RA01: Call once at start-up, after CameraHook (which loads ExeScan).
    // Cost - CPU: low, once (one scan of the code section, about 10 ms, and CameraHook's brief pause to write 5 bytes); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = HookRegistry.FindAll(HookRegistry.CrosshairUpdate, Visibility);   // RA01: M43 - every door here reports to the Rehooker
            if (found.Count != 1) { Log.Write($"RA01 crosshair probe NOT installed: the crosshair update was found {found.Count} times (needs exactly 1), so this game build may differ"); return; }
            if (!Hook(found[0], MovRsp10, (nint)(delegate* unmanaged<nint, void>)&OnVisibility, "visibility update")) { HookRegistry.Off(HookRegistry.CrosshairUpdate, "the hook could not be put in"); return; }
            visibilityUpdate = found[0]; // RA01: M13-20 - for the per-frame check
            new Thread(Watch) { IsBackground = true, Name = "RA01 crosshair probe" }.Start();

            List<nint> apply = HookRegistry.FindAll(HookRegistry.CrosshairApply, Apply); // RA01: M13-14 - the two extra doors; either missing only costs that door, never the visibility hook
            if (apply.Count != 1) { Log.Write($"RA01 crosshair probe: 'apply crosshair' found {apply.Count} times (needs exactly 1) - not hooked"); return; }
            if (!Hook(apply[0], MovRsp18, (nint)(delegate* unmanaged<nint, void>)&OnApply, "apply crosshair")) HookRegistry.Off(HookRegistry.CrosshairApply, "the hook could not be put in");
            nint set = apply[0] + ApplyToSet;
            if (!new ReadOnlySpan<byte>((byte*)set, SetCrosshair.Length).SequenceEqual(SetCrosshair)) { Log.Write("RA01 crosshair probe: 'set crosshair' isn't where it was in 2.31 - not hooked"); return; }
            Hook(set, MovRsp08, (nint)(delegate* unmanaged<nint, nint, void>)&OnSet, "set crosshair (log only)");
            HookFrameTick(); // RA01: M13-20
        }
        catch (Exception e) { Log.Write($"RA01 crosshair probe failed: {e.Message}"); }
    }

    // RA01: M13-14 - one detour (the PoseHook shape): keep every register, call 'handler' with the manager still in rcx, put everything back, run the instruction the
    // jump replaced, go on into the game. False (and a log line) if it could not be put in; nothing is changed then.
    // M13-25 - shared: WeaponEventProbe uses it too (the handler gets rcx and rdx, both kept intact by the detour).
    // Cost - CPU: low, once per site (a small memory block and CameraHook's brief pause to write 5 bytes); GPU: none.
    // M13-37 - two options for AimProbe: 'replaced' may be longer than 5 bytes (whole instructions only; the jump covers the first 5 and the game resumes
    // after all of them), and returnAddressInR9 hands the handler, as its 4th input (r9), where the game will return to - which caller it is. Both are
    // off for every older hook, so they build exactly as before. r9 itself is put back before the game's own code runs.
    // M13-44 - returnAddressInRcx does the same through the 1st input (rcx) instead, for a function whose 4th input (r9) the handler needs (PhysX's sweep).
    // M13-55 - siteStackInRcx hands the handler, as its 1st input, the game's stack pointer as it was at the hooked spot (72 bytes above rbp, as above) -
    // for a hook in the MIDDLE of a function, so the handler can read and change that function's own local values (its "notebook") before it uses them.
    internal static bool Hook(nint site, ReadOnlySpan<byte> replaced, nint handler, string what, bool returnAddressInR9 = false, bool returnAddressInRcx = false, bool siteStackInRcx = false)
    {
        if (replaced.Length < 5) { Log.Write($"RA01 hook: {what} - fewer than 5 bytes to replace, not hooked"); return false; }
        if (!new ReadOnlySpan<byte>((byte*)site, replaced.Length).SequenceEqual(replaced)) { Log.Write($"RA01 hook: {what} doesn't start as expected - not hooked"); return false; }
        nint trampoline = CameraHook.AllocateNear(site);
        if (trampoline == 0) { Log.Write($"RA01 hook: {what} NOT hooked - no free memory within reach"); return false; }
        byte* code = (byte*)trampoline;
        int at = 0;
        ReadOnlySpan<byte> head = TrampolineHead[..^2];                                  // RA01: everything up to the "mov rax, <handler>"
        head.CopyTo(new Span<byte>(code + at, head.Length)); at += head.Length;
        if (returnAddressInR9) { MovR9ReturnAddress.CopyTo(new Span<byte>(code + at, MovR9ReturnAddress.Length)); at += MovR9ReturnAddress.Length; }
        if (returnAddressInRcx) { MovRcxReturnAddress.CopyTo(new Span<byte>(code + at, MovRcxReturnAddress.Length)); at += MovRcxReturnAddress.Length; }
        if (siteStackInRcx) { LeaRcxSiteStack.CopyTo(new Span<byte>(code + at, LeaRcxSiteStack.Length)); at += LeaRcxSiteStack.Length; }
        TrampolineHead[^2..].CopyTo(new Span<byte>(code + at, 2)); at += 2;
        *(long*)(code + at) = handler; at += 8;
        TrampolineTail.CopyTo(new Span<byte>(code + at, TrampolineTail.Length)); at += TrampolineTail.Length;
        replaced.CopyTo(new Span<byte>(code + at, replaced.Length)); at += replaced.Length;
        JumpBack.CopyTo(new Span<byte>(code + at, JumpBack.Length)); at += JumpBack.Length;
        *(long*)(code + at) = site + replaced.Length;
        if (!CameraHook.WriteJump(site, trampoline)) { Log.Write($"RA01 hook: {what} NOT hooked - the jump could not be written (nothing was changed)"); return false; }
        Log.Write($"RA01 hook: {what} hooked at RVA 0x{site - ExeScan.Base:X}");
        return true;
    }

    // RA01: the game calls this (through the detour) each time it updates its crosshair, on its own UI thread, just before its own code reads the two fields:
    // note which manager, count it, and (M13-8) switch it off or back on as the rule says. Never throws back into the game.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    [UnmanagedCallersOnly]
    static void OnVisibility(nint crosshairManager) => Force(crosshairManager);

    // RA01: M13-14 - "apply crosshair" (it sets the opacity from the same two fields before the visibility update runs): the same switch, first.
    // Cost - CPU: extremely low (as Force); GPU: none.
    [UnmanagedCallersOnly]
    static void OnApply(nint crosshairManager) => Force(crosshairManager);

    // RA01: M13-22 - REMOVED: a test that called the game's own active-crosshair lookup (RVA 0x96ABDC) from inside the per-frame check crashed the game on the
    // RA01: first weapon draw (black box: Cyberpunk2077.exe +0x11D9BA, inside the hash-map search that lookup uses, reading a bad bucket). The per-frame switch
    // RA01: read is also called while the game is in the middle of changing that map, so calling the lookup from there is never safe. Do not bring it back.

    // RA01: M13-24 - TEMPORARY TEST, log-only, one safe read: the game's own database ID of what V holds. The manager's +0x168 is not a pointer: the game code
    // RA01: that uses it (0x581BB0 -> 0x2843E8) hands its first 8 bytes straight to the game database's record lookup (TweakDB). A TweakDB ID is a fingerprint
    // RA01: of a record's name - CRC32 of the name in the low 4 bytes, the name's length in the 5th - like a part number stamped from the catalogue entry's title.
    // RA01: The record names (Crosshairs.* or Items.*, from WolvenKit's Tweak Browser) can be fingerprinted offline and matched to these. M13-23 showed the class
    // RA01: names read right (gameuiHUDGameController, inkCanvasWidget, inkCompoundWidget all matched) but none of the spots it checked change per weapon.
    // RA01: One line whenever the ID changes, beside the skeleton reading; at most 300 lines. A read at a bad moment only gives a wrong value for that moment.
    // RA01: Cost - CPU: extremely low (one safe read 4 times a second; rarely one log line); GPU: none.
    const int RecordIdAt = 0x168;
    static ulong lastRecordId = 1;
    static int idLines;

    static void RecordIdTest()
    {
        try
        {
            nint m = Volatile.Read(ref manager);
            ulong id = 0;
            if (m == 0 || idLines >= 300 || !Read(m + RecordIdAt, &id, 8) || id == lastRecordId) return;
            lastRecordId = id;
            idLines++;
            Log.Write($"RA01 weapon id test: manager +0x168 = 0x{id:X16} (name fingerprint 0x{(uint)id:X8}, name length {(id >> 32) & 0xFF}) - skeleton reading says {WeaponProbe.Held}");
        }
        catch { }
    }

    // RA01: M13-14 - LOG ONLY, temporary: "set crosshair" - a new crosshair goes in. One line when the manager differs from the last one seen here (a new manager after
    // a save load), at most 40 lines, so the log shows how long after a load the game first touches the new crosshair.
    // M13-21c - TEMPORARY TEST: which crosshair is coming in. The M13-21 test read the manager's +0x1E0 slot 4 times a second, but "set crosshair" empties
    // that slot again straight after spawning the picture, so it only ever read empty. Here the crosshair is read as it is handed in: the second input
    // (rdx, kept intact by the detour) points at a typed handle - its first word is the game's own type label for what's inside (lowest bit = "filled in"),
    // then the crosshair itself. One line per hand-over: the type label, a row of numbers inside the crosshair (+0x30..), and inside its info (+0x50 -> +0x28..);
    // the file fingerprints (claude/RA01_M13-21_Crosshair_Ids doc) are looked for in those, beside what the skeleton reading says. Safe reads only, nothing called.
    // Cost - CPU: extremely low (a compare; on each weapon switch a few safe reads and one log line, at most 200); GPU: none.
    [UnmanagedCallersOnly]
    static void OnSet(nint crosshairManager, nint incoming)
    {
        try
        {
            if (crosshairManager != lastSetManager)
            {
                lastSetManager = crosshairManager;
                if (++setLines <= 40) Log.Write($"RA01 crosshair probe: set crosshair on a new manager 0x{crosshairManager:X} (crosshair rule now: {(HudLayer.HideCrosshair ? "hidden" : "shown")})");
            }
            if ((typeLines >= 200 && Environment.TickCount64 > hmgProbeUntil) || incoming <= 0x10000) return; // RA01: M27-8 - no cap while the HMG probe runs
            typeLines++;
            ulong label = 0; nint crosshair = 0, info = 0;
            ulong* near = stackalloc ulong[8];
            ulong* inner = stackalloc ulong[8];
            Read(incoming, &label, 8); Read(incoming + 8, &crosshair, 8);
            nint type = (nint)(label & ~1UL);
            string where = type >= ExeScan.Base && type < ExeScan.Base + (nint)ExeScan.ImageSize ? $"RVA 0x{type - ExeScan.Base:X}" : $"0x{type:X}";
            string row = crosshair != 0 && Read(crosshair + 0x30, near, 64) ? Row(near) : "none";
            string innerRow = crosshair != 0 && Read(crosshair + CrosshairInfoAt, &info, 8) && info != 0 && Read(info + 0x28, inner, 64) ? Row(inner) : "none";
            Log.Write($"RA01 crosshair type test: incoming crosshair 0x{crosshair:X} (type label {where}{((label & 1) != 0 ? "" : ", not filled in")}) - skeleton reading says {WeaponProbe.Held}; crosshair +0x30..: {row}; info +0x28..: {innerRow}");
        }
        catch { }
    }
    static int typeLines;

    // RA01: the switch itself (M13-8), shared by both doors: switch the manager's crosshair off or back on as the rule says. Never throws back into the game.
    // Cost - CPU: extremely low (a store, a short table walk and up to three small writes); GPU: none.
    static void Force(nint crosshairManager)
    {
        try
        {
            if (crosshairManager <= 0x10000) return;
            Volatile.Write(ref manager, crosshairManager);
            Volatile.Write(ref uiThread, Environment.CurrentManagedThreadId); // RA01: M13-20 - the game's own crosshair thread
            Interlocked.Increment(ref calls);
            if (!Places(crosshairManager, out int reducedAt, out int opacityAt)) return;   // RA01: M44-2 - a class without the two fields (on a new build): never written
            byte* reduced = (byte*)(crosshairManager + reducedAt);
            float* opacity = (float*)(crosshairManager + opacityAt);
            int i = Array.IndexOf(forced, crosshairManager);
            if (HudLayer.HideCrosshair)
            {
                if (i < 0)
                {
                    i = Array.IndexOf(forced, (nint)0);
                    if (i < 0) { i = 0; for (int j = 1; j < MaxManagers; j++) if (lastCall[j] < lastCall[i]) i = j; }
                    forced[i] = crosshairManager; savedReduced[i] = *reduced; savedOpacity[i] = *opacity;
                }
                else if (*reduced != 1) savedReduced[i] = *reduced; // RA01: the game flipped its own switch meanwhile - that is now its value to put back
                *reduced = 1; *opacity = 0f;
                lastCall[i] = Environment.TickCount64;
            }
            else if (i >= 0)
            {
                if (*reduced == 1) *reduced = savedReduced[i];
                *opacity = savedOpacity[i];
                forced[i] = 0;
            }
        }
        catch { }
    }

    // RA01: M44-2 - where this manager keeps its two switches, by NAME (FieldNames.AtAny), remembered per class: the field probe showed the manager can be the
    // on-foot crosshair container or the driving-combat HUD, and the game's own crosshair function reads both alike. False = not usable on this class.
    // Builder picture: each kind of cabinet is opened by its labelled handles; a new kind is looked at once, then remembered.
    // Cost - CPU: extremely low per call (one read and up to 8 compares); low once per new class (two field-list walks); GPU: none.
    static bool Places(nint manager, out int reducedAt, out int opacityAt)
    {
        nint cls = *(nint*)(manager + 0x30);   // RA01: the object's class (Force already reads this object directly - it is the game's live manager)
        for (int i = 0; i < FieldSlots; i++)
            if (Volatile.Read(ref fieldClass[i]) == cls) { reducedAt = reducedOf[i]; opacityAt = opacityOf[i]; return reducedAt >= 0 && opacityAt >= 0; }
        reducedAt = FieldNames.AtAny(manager, HookRegistry.CrosshairSafeState, "inSafeState", ReducedAt);
        opacityAt = FieldNames.AtAny(manager, HookRegistry.CrosshairOpacity, "crosshairReducedOpacity", BrightnessAt);
        int slot = Interlocked.Increment(ref fieldNext) & (FieldSlots - 1);
        reducedOf[slot] = reducedAt; opacityOf[slot] = opacityAt;
        Volatile.Write(ref fieldClass[slot], cls);   // RA01: the class last, so a reader never sees it with the old slot's places
        Log.Write($"RA01 crosshair: manager class 0x{cls:X} - safe-state switch {(reducedAt >= 0 ? $"+0x{reducedAt:X}" : "NOT usable")}, reduced opacity {(opacityAt >= 0 ? $"+0x{opacityAt:X}" : "NOT usable")}");
        return reducedAt >= 0 && opacityAt >= 0;
    }

    // RA01: M27-8 - THE HMG CROSSHAIR PROBE (Dev tab "HMG crosshair probe (log)", LOG ONLY; Rob 06:59: the Mk.31 HMG keeps its crosshair although the rule says
    // hidden - and its log showed the crosshair manager never changing when the HMG came out, and no "set crosshair" for it). For HmgProbeMs after the click,
    // once a second: every crosshair manager the per-frame check has seen in the last second - its address, its class (catalogue name), the crosshair record
    // id it holds (+0x168), its switches (+0x269, +0x283 "reduced", +0x278 opacity), whether the mod has switched it off - plus what V holds. Comparing the
    // lines with the HMG out against a normal gun shows whether the HMG's crosshair sits in one of these managers (then why the fade doesn't take) or lives
    // somewhere else entirely (GameCall's half of the probe names the HMG crosshair class, Crosshair_Custom_HMG, and what it is built on). Changes nothing.
    // Cost - CPU: extremely low (a few safe reads and a short log line per live manager, once a second, only for those 20 seconds); GPU: none.
    public static void StartHmgProbe() { hmgProbeUntil = Environment.TickCount64 + HmgProbeMs; hmgProbeNext = 0; Log.Write($"RA01 HMG crosshair probe: on for {HmgProbeMs / 1000} s - hold the HMG, then switch to a normal gun"); }
    const long HmgProbeMs = 20000;
    static long hmgProbeUntil, hmgProbeNext;
    static void HmgProbeTick()
    {
        long now = Environment.TickCount64;
        if (now > hmgProbeUntil || now < hmgProbeNext) return;
        hmgProbeNext = now + 1000;
        try
        {
            int live = 0;
            for (int i = 0; i < MaxManagers; i++)
            {
                nint m = frameManager[i];
                if (m == 0 || now - frameSeen[i] > 1000) continue;
                live++;
                ulong record = 0; byte sw = 0, reduced = 0; float opacity = 0; nint cls = 0; ulong clsName = 0;
                Read(m + WidgetAt, &record, 8); Read(m + SwitchAt, &sw, 1); Read(m + ReducedAt, &reduced, 1); Read(m + BrightnessAt, &opacity, 4);
                string name = Read(m + 0x30, &cls, 8) && cls > 0x10000 && Read(cls + 0x18, &clsName, 8) ? GameCall.NameText(clsName) : "?";
                Log.Write($"RA01 HMG crosshair probe: manager 0x{m:X} ({(name.Length > 0 ? name : "?")}) record 0x{record & 0xFFFFFFFFFF:X10}, switch {sw}, reduced {reduced}, opacity {opacity:F2}, switched off by us: {(Array.IndexOf(forced, m) >= 0 ? "yes" : "no")} - V holds {(WeaponProbe.HeldName.Length > 0 ? WeaponProbe.HeldName : WeaponProbe.Held.ToString())}, rule {(HudLayer.HideCrosshair ? "hidden" : "shown")}");
            }
            if (live == 0) Log.Write("RA01 HMG crosshair probe: no crosshair manager seen in the last second");
        }
        catch (Exception e) { Log.Write($"RA01 HMG crosshair probe: stopped ({e.Message})"); hmgProbeUntil = 0; }
    }

    // RA01: M27-9c - THE HMG CROSSHAIR OFF, ON FOOT ONLY (Rob 07:50: "only if V carries it on foot, we don't want to damage vehicular combat"). The M27-8 probe
    // RA01: showed the mod's fade already on the HMG's manager while its crosshair stayed: the HMG crosshair has its own controller. So, while V holds the HMG
    // RA01: on foot (not in or on a vehicle), the crosshair rule says hidden, and the Dev row "HMG crosshair off" is on (default on):
    // RA01:  1. HmgScan (this background thread): each live manager is looked through for crosshair controllers (GameCall.FindCrosshairControllers, safe reads
    // RA01:     only), once per HMG pick-up; what was found and where is logged. Not found: tried again every HmgRescanMs, logged once.
    // RA01:  2. HmgFrame (the game's own HUD thread, in that manager's per-frame check, every RefreshMs): each found controller is checked to still sit where it
    // RA01:     was found, then its picture is hidden with the game's own built-in SetVisible - again and again, so the game can't bring it back.
    // RA01: When any condition ends (a vehicle, the rule shows, the row off) the pictures are shown again once; when the HMG is gone, all is forgotten. Vehicle HUDs
    // RA01: are never touched: nothing is done while mounted, and only after the HMG was seen in V's hand on foot.
    // RA01: Cost - CPU: low once per HMG pick-up (the scan), then extremely low (a few safe reads and one game call 4 times a second); GPU: none.
    static bool HoldsHmg => WeaponProbe.HeldName.StartsWith("Heavy machine gun", StringComparison.Ordinal);
    static bool HmgOnFoot => Tuning.DevHmgCrosshairOff && HudLayer.HideCrosshair && HoldsHmg && !VehicleState.Mounted;
    const int HmgMax = 4;
    const long HmgRescanMs = 3000;
    static nint hmgManager;                                   // RA01: the manager the controllers were found in (0 = none found / forgotten); written last by HmgScan
    static readonly nint[] hmgCtl = new nint[HmgMax], hmgWidget = new nint[HmgMax];
    static readonly int[] hmgAt1 = new int[HmgMax], hmgAt2 = new int[HmgMax];
    static int hmgCount;
    static bool hmgHidden, hmgMissLogged;
    static long hmgScanNext, hmgFrameNext;

    // RA01: step 1 (see above). Cost - CPU: low, rarely (see above); GPU: none.
    static void HmgScan()
    {
        long now = Environment.TickCount64;
        if (!HoldsHmg) hmgMissLogged = false;
        if (!HmgOnFoot || Volatile.Read(ref hmgManager) != 0 || now < hmgScanNext) return;
        hmgScanNext = now + HmgRescanMs;
        try
        {
            for (int i = 0; i < MaxManagers; i++)
            {
                nint m = frameManager[i];
                if (m == 0 || now - frameSeen[i] > 1000) continue;
                var names = new List<string>();
                int n = GameCall.FindCrosshairControllers(m, hmgCtl, hmgAt1, hmgAt2, names);
                if (n == 0) continue;
                for (int k = 0; k < HmgMax; k++) hmgWidget[k] = 0;
                hmgCount = n;
                Volatile.Write(ref hmgManager, m);
                Log.Write($"RA01 HMG crosshair: found {n} crosshair controller(s) in manager 0x{m:X}: {string.Join("; ", names)} - hiding while V carries the HMG on foot");
                return;
            }
            if (!hmgMissLogged) { hmgMissLogged = true; Log.Write("RA01 HMG crosshair: no crosshair controller found in the live managers - nothing hidden (tried again every 3 s while the HMG is held)"); }
        }
        catch (Exception e) { Log.Write($"RA01 HMG crosshair: search stopped ({e.Message})"); }
    }

    // RA01: step 2 (see above), on the game's own HUD thread. Cost - CPU: extremely low (see above); GPU: none.
    static void HmgFrame(nint m)
    {
        nint hm = Volatile.Read(ref hmgManager);
        if (hm == 0 || m != hm || Environment.CurrentManagedThreadId != Volatile.Read(ref uiThread)) return;
        long now = Environment.TickCount64;
        if (now < hmgFrameNext) return;
        hmgFrameNext = now + RefreshMs;
        bool hide = HmgOnFoot;
        if (hide || hmgHidden)
        {
            int left = 0;
            for (int i = 0; i < hmgCount; i++)
            {
                nint c = hmgCtl[i], at = 0;
                if (c == 0) continue;
                bool there = Read(m + hmgAt1[i], &at, 8) && (hmgAt2[i] < 0 ? at == c : at > 0x10000 && Read(at + hmgAt2[i], &at, 8) && at == c);
                if (!there) { hmgCtl[i] = 0; continue; }                       // RA01: gone or replaced - never touched again (a new search finds the new one)
                if (GameCall.SetCrosshairShown(c, ref hmgWidget[i], !hide)) left++;
                else { hmgCtl[i] = 0; Log.Write($"RA01 HMG crosshair: controller 0x{c:X} can't be {(hide ? "hidden" : "shown")} - left alone"); }
            }
            if (hide != hmgHidden) { hmgHidden = hide; Log.Write($"RA01 HMG crosshair: {(hide ? "hidden" : "shown again")} ({left} controller(s){(hide ? "" : VehicleState.Mounted ? " - in a vehicle" : !HoldsHmg ? " - HMG gone" : "")})"); }
            if (left == 0) { hmgHidden = false; Volatile.Write(ref hmgManager, 0); return; } // RA01: none left - forget; a new search runs if still needed
        }
        if (!hide && !hmgHidden && !HoldsHmg) Volatile.Write(ref hmgManager, 0);
    }

    // RA01: background, 4 times a second: read the last manager's fields; log only when they changed (or the manager did).
    // M13-24 - TEMPORARY: also the weapon id test (RecordIdTest).
    // Cost - CPU: extremely low (a few tiny safe reads 4 times a second); GPU: none.
    static void Watch()
    {
        string last = "";
        while (true)
        {
            Thread.Sleep(250);
            RecordIdTest();
            HmgProbeTick();
            HmgScan();
            try
            {
                nint m = Volatile.Read(ref manager);
                if (m == 0) continue;
                nint widget; byte sw; float bright; ulong flags;
                if (!Read(m + WidgetAt, &widget, 8) || !Read(m + SwitchAt, &sw, 1) || !Read(m + BrightnessAt, &bright, 4) || !Read(m + FlagsAt, &flags, 8)) continue;
                string now = $"manager 0x{m:X}, crosshair widget 0x{widget:X}, switch +0x269 = {sw}, brightness +0x278 = {bright:F2}, flags +0x280..287 = {flags:X16}";
                int n = Interlocked.Exchange(ref calls, 0);
                if (now != last) { Log.Write($"RA01 crosshair probe: {now} ({n} updates since the last line)"); last = now; }
            }
            catch { }
        }
    }

    // RA01: M13-20 - the HUD's PER-FRAME step. The crosshair manager's class has one table of 84 functions (found from its builder at RVA 0x1492600, which
    // RA01: writes the table's address into every new manager). The M13-19 probe counted calls to each: entry 39 runs about once a frame (125-190 a second) on
    // RA01: every live manager - a tiny "read one switch (+0x281)" function the game asks each frame. Its table entry is pointed at OnFrame below, which gives the
    // RA01: game exactly the same answer and, on the game's own HUD thread, re-runs the game's own visibility update when the rule needs it: once when the rule
    // RA01: changes (gun out / put away), at once for a manager it hasn't seen (the very start, a save load), and every RefreshMs while hidden (a shotgun reload that
    // RA01: brings the crosshair back). Like a night watchman walking the same round every few minutes, instead of waiting for someone to ring the bell.
    // RA01: Safety: it only re-runs the update on the thread the game itself last ran it on (M13-9's crash was the same call from our own thread mid-rebuild), and
    // RA01: never inside its own call (the update can come back here).
    // RA01: Cost - CPU: extremely low (per frame: one call through, a few compares; the game's own short update about 4 times a second while hidden); GPU: none.
    static ReadOnlySpan<byte> ManagerBuilder => new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0x07, 0x81, 0xE2, 0xFE, 0x33, 0xD2, 0x48, 0x8D, 0x05, 0x76, 0x76 };
    static ReadOnlySpan<byte> SwitchGetter => new byte[] { 0x0F, 0xB6, 0x81, 0x81, 0x02, 0x00, 0x00, 0xC3 }; // RA01: entry 39 in 2.31: movzx eax, byte [rcx+281h]; ret
    const int TableLeaAt = 0x12, ManagerSlots = 84, FrameSlot = 39, GetterAt = 0x281;
    const long RefreshMs = 250;
    static nint visibilityUpdate;                                    // RA01: the game's own UpdateCrosshairVisibility (hooked above; calling it runs OnVisibility first)
    static int uiThread;                                              // RA01: the thread the game itself last ran that update on
    static int ruleStamp, frameLines;                                 // RA01: goes up each time the rule flips
    static bool lastRule;
    static readonly nint[] frameManager = new nint[MaxManagers];      // RA01: managers seen here, the rule stamp each was last refreshed for, and when it is next due
    static readonly int[] frameStamp = new int[MaxManagers];
    static readonly long[] frameDue = new long[MaxManagers];
    static readonly long[] frameSeen = new long[MaxManagers];         // RA01: M27-8 - when each was last seen by the per-frame check
    [ThreadStatic] static bool refreshing;

    // RA01: finds the class table and points entry 39 at OnFrame - only if the entry is exactly the 8-byte switch read seen in 2.31; otherwise nothing is changed.
    // Cost - CPU: extremely low, once (one byte-pattern scan and one table write); GPU: none.
    static void HookFrameTick()
    {
        List<nint> found = HookRegistry.FindAll(HookRegistry.CrosshairFrame, ManagerBuilder, checkBytes: 4); // RA01: M43 - the pattern holds a call distance (byte 4 on), which changes with every game update; on a new build only the first 4 bytes are checked, at the function the address list names
        if (found.Count != 1) { Log.Write($"RA01 crosshair: manager builder found {found.Count} times - per-frame check off"); return; }
        byte* lea = (byte*)(found[0] + TableLeaAt);
        if (lea[0] != 0x4C || lea[1] != 0x8D || lea[2] != 0x15) { Log.Write("RA01 crosshair: manager builder looks different - per-frame check off"); HookRegistry.Off(HookRegistry.CrosshairFrame, "the builder's table read looks different"); return; }
        nint table = found[0] + TableLeaAt + 7 + *(int*)(lea + 3);
        nint getter = ((nint*)table)[FrameSlot];
        if (getter < ExeScan.Base || getter >= ExeScan.Base + (nint)ExeScan.ImageSize || !new ReadOnlySpan<byte>((byte*)getter, SwitchGetter.Length).SequenceEqual(SwitchGetter))
        { Log.Write($"RA01 crosshair: table entry {FrameSlot} isn't the switch read seen in 2.31 - per-frame check off"); HookRegistry.Off(HookRegistry.CrosshairFrame, $"table entry {FrameSlot} isn't the 2.31 switch read"); return; }
        PresentHook.Patch(table, FrameSlot, (nint)(delegate* unmanaged<nint, int>)&OnFrame);
        Log.Write($"RA01 crosshair: per-frame check on (table at RVA 0x{table - ExeScan.Base:X}, entry {FrameSlot})");
    }

    // RA01: the game's per-frame switch read, through us: the same answer back, plus the rule check (Refresh). Never throws back into the game.
    // Cost - CPU: extremely low (see HookFrameTick); GPU: none.
    [UnmanagedCallersOnly]
    static int OnFrame(nint crosshairManager)
    {
        try { Refresh(crosshairManager); HmgFrame(crosshairManager); } catch { }
        return *(byte*)(crosshairManager + GetterAt);
    }

    // RA01: re-runs the game's own visibility update for this manager when the rule needs it (see HookFrameTick).
    // Cost - CPU: extremely low (a short table walk and a few compares; rarely the game's update and one log line); GPU: none.
    static void Refresh(nint m)
    {
        if (refreshing || visibilityUpdate == 0 || m <= 0x10000) return;
        bool hide = HudLayer.HideCrosshair;
        if (hide != lastRule) { lastRule = hide; ruleStamp++; }
        long now = Environment.TickCount64;
        int i = Array.IndexOf(frameManager, m);
        if (i >= 0) frameSeen[i] = now;                                    // RA01: M27-8 - for the HMG crosshair probe: which managers are alive
        if (i < 0)
        {
            i = Array.IndexOf(frameManager, (nint)0);
            if (i < 0) { i = 0; for (int j = 1; j < MaxManagers; j++) if (frameDue[j] < frameDue[i]) i = j; } // RA01: full: reuse the longest-quiet entry (a gone manager)
            frameManager[i] = m; frameStamp[i] = ruleStamp - 1; frameSeen[i] = now; // RA01: new here: due at once
        }
        if (frameStamp[i] == ruleStamp && !(hide && now >= frameDue[i])) return;
        if (Environment.CurrentManagedThreadId != Volatile.Read(ref uiThread)) return; // RA01: not the game's own crosshair thread: leave it
        bool first = frameStamp[i] != ruleStamp;
        frameStamp[i] = ruleStamp; frameDue[i] = now + RefreshMs;
        refreshing = true;
        try { ((delegate* unmanaged<nint, void>)visibilityUpdate)(m); }
        finally { refreshing = false; }
        if (first && ++frameLines <= 40) Log.Write($"RA01 crosshair: re-applied on manager 0x{m:X} from the per-frame check (rule now {(hide ? "hidden" : "shown")})");
    }

    // RA01: M13-21 - TEMPORARY TEST, log-only: can the crosshair tell us what V holds? "Set crosshair" (0x5B9EE8) puts the new crosshair's handle in the
    // manager at +0x1E0, then spawns its widget from the HUD library by an id read at crosshair +0x50 -> +0x48. Each weapon kind gets its own crosshair
    // (the user's research: power / tech / smart / shotgun brackets, fists and Gorilla Arms brackets, Mantis Blades chevron, Monowire wide, launcher circle),
    // so that id should name the kind. Read here with safe reads only (nothing in the game is called); one line each time it changes, with what the skeleton
    // reading says at that moment - switching through each weapon then shows whether every kind has its own id. If so, the next step makes it the main
    // source for Held, and the skeleton reading stays as the backup (the user's call).
    // Cost - CPU: extremely low (three safe reads, 4 times a second; rarely one log line); GPU: none.
    // eight numbers as hex, for the type test line.
    // Cost - CPU: extremely low (one short string, rarely); GPU: none.
    static string Row(ulong* v)
    {
        var text = new System.Text.StringBuilder();
        for (int k = 0; k < 8; k++) text.Append(k == 0 ? "" : " ").Append(v[k].ToString("X16"));
        return text.ToString();
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
