namespace RA01.Core;

// RA01: M56 - LOAD SAFETY: the crash on loading a save that starts INSIDE Johnny's body (black box: Cyberpunk2077.exe +0x4D7EFE, reading 0x155).
// M55's call trail named it: when the HUD comes up after the load, the hold-button indicator (gameuiHoldIndicatorGameController, the ring that fills while
// you hold a button; its function 0x6E12D8, vtable 0x2B07F18 slot 34) asks the game's player system (gameIPlayerSystem) who the player is, gets NOBODY
// (Johnny's body isn't signed in as the player yet), and hands that empty answer through 0x6E1490 to a small helper, 0x4D7EF8, which reads
// [player+0x155] and [player+0x228] - reading from address 0 crashes the game. Only with "No aim zoom" on (any of its edits, M35-4a/4b tests), so the
// edits seem to make the sign-in a moment slower and the HUD gets there first; playing INTO the flashback never hit it (the player is already in).
// THE CATCH: a 5-byte jump at the helper's start into a tiny machine-code stub (no C# in the game's path): handed nobody, it gives back the helper's own
// ordinary "nothing found" answer (0 - the helper returns that itself whenever its look-up finds nothing) and returns; handed anyone, it runs the
// helper's own first two instructions and goes on into the game's code exactly as before. Like a safety catch on a nail gun: it only stops a shot
// fired at thin air, every real nail goes in as always. Each catch is counted, and a watcher thread logs the count when it changes.
// Cost - CPU: extremely low (one test and one jump per call of the helper; the watcher wakes once a second to compare one number); GPU: none.
internal static unsafe class NullPlayerGuard
{
    // RA01: the helper's first 16 bytes on 2.31 (push rbx; sub rsp,30h; mov r8b,[rdx+155h]; mov r9b,1) - found exactly once. The first 6 (two instructions) are replaced.
    static ReadOnlySpan<byte> HelperHead => new byte[] { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x30, 0x44, 0x8A, 0x82, 0x55, 0x01, 0x00, 0x00, 0x41, 0xB1, 0x01 };
    const int Replaced = 6;

    // RA01: the stub (checked with a disassembler - every jump lands where it should):
    // RA01:    0 test rdx,rdx / je +11       - handed nobody? go to the catch
    // RA01:    5 push rbx / sub rsp,30h      - the helper's own first two instructions
    // RA01:   11 jmp <helper + 6>            - on into the game's code (the 4-byte distance is filled in)
    // RA01:   16 lock inc dword [rip+29h]    - the catch: count it (the counter sits at block +0x40)
    // RA01:   23 and qword [rcx],0 / mov rax,rcx / ret - the helper's "nothing found": its answer (rcx) set to 0, handed back
    static ReadOnlySpan<byte> Stub => new byte[]
    {
        0x48, 0x85, 0xD2, 0x74, 0x0B, 0x40, 0x53, 0x48, 0x83, 0xEC, 0x30, 0xE9, 0, 0, 0, 0,
        0xF0, 0xFF, 0x05, 0x29, 0x00, 0x00, 0x00, 0x48, 0x83, 0x21, 0x00, 0x48, 0x89, 0xC8, 0xC3,
    };
    const int JumpBackAt = 12, JumpBackEnd = 16, CounterAt = 0x40;
    static int* catches;
    static int installed;

    // RA01: call once at start-up, after CameraHook (which loads ExeScan). Finds the helper (Rehooker row "Hold indicator safety"), builds the stub next to it and
    // writes the jump. Any failure leaves the game untouched, logs one line and marks the row OFF.
    // Cost - CPU: low, once (one exe search, one small memory block, CameraHook's brief pause to write 5 bytes); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = HookRegistry.FindAll(HookRegistry.HoldIndicatorSafety, HelperHead);
            if (found.Count != 1) { Log.Write($"RA01 load safety: the helper was found {found.Count} times (needs 1) - not fitted; the game runs as normal"); return; }
            nint site = found[0];
            nint block = CameraHook.AllocateNear(site);
            if (block == 0) { HookRegistry.Off(HookRegistry.HoldIndicatorSafety, "no free memory within reach"); Log.Write("RA01 load safety: no free memory within reach - not fitted"); return; }
            byte* code = (byte*)block;
            Stub.CopyTo(new Span<byte>(code, Stub.Length));
            *(int*)(code + JumpBackAt) = (int)((long)(site + Replaced) - ((long)block + JumpBackEnd));
            catches = (int*)(code + CounterAt);
            if (!CameraHook.WriteJump(site, block)) { HookRegistry.Off(HookRegistry.HoldIndicatorSafety, "the jump could not be written"); Log.Write("RA01 load safety: the jump could not be written - not fitted (nothing was changed)"); return; }
            Log.Write($"RA01 load safety: hold indicator guard fitted at RVA 0x{site - ExeScan.Base:X} (M56)");
            new Thread(Watch) { IsBackground = true, Name = "RA01 load safety" }.Start();
        }
        catch (Exception e) { Log.Write($"RA01 load safety: could not be fitted ({e.Message}) - the game runs as normal"); }
    }

    // RA01: logs each new catch: the game asked for the player and got nobody, and the helper answered "nothing found" instead of crashing.
    // Cost - CPU: extremely low (wakes once a second, compares one number); GPU: none.
    static void Watch()
    {
        int seen = 0;
        while (true)
        {
            Thread.Sleep(1000);
            int now = Volatile.Read(ref *catches);
            if (now == seen) continue;
            Log.Write($"RA01 load safety: hold indicator was handed no player {now - seen} time(s) - answered \"nothing found\" instead of crashing (M56; {now} so far)");
            seen = now;
        }
    }
}
