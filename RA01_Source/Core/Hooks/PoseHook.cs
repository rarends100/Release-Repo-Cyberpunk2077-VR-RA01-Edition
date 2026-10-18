using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: M7-0 - catches the moment the game turns a character's finished bone list into full-body positions for the skin (FUN_1404b6968 in the 2.31 exe).
// Every animation layer has already been merged into that list, so a change made here is the last word before the body is built.
// Core only provides the doorway: it hands the list to whoever registered with SetHandler (PlayerSkeleton, which passes V's alone on to Visuals) and never decides anything about the body itself.
// Cost - CPU: extremely low (one call per animated character per frame, a few hundred a second, each one pointer check and one call); GPU: none.
internal static unsafe class PoseHook
{
    // RA01: the first 16 bytes of the full-body step - found exactly once in the whole 2.31 exe, so a patch that moves it can still be found
    static ReadOnlySpan<byte> BuildBody => new byte[] { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x40, 0x4C, 0x8B, 0x52, 0x10, 0x4C, 0x8B, 0xC9, 0x44, 0x8B, 0x41 };
    const int ReplacedLength = 6;   // RA01: the 5-byte jump covers two whole instructions: push rbx (2 bytes) and sub rsp,40h (4 bytes)

    // RA01: the detour, in two halves (same shape as CameraHook's). The game's own two arguments - the bone list in rcx and the skeleton info in rdx -
    // RA01: are still in rcx and rdx when our code is called, so they pass straight through untouched.
    static ReadOnlySpan<byte> TrampolineHead => new byte[]
    {
        0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53, // RA01: pushfq, push rax rcx rdx r8 r9 r10 r11 - keep everything the game may still need
        0x55, 0x48, 0x89, 0xE5,                                                  // RA01: push rbp; mov rbp, rsp
        0x48, 0x83, 0xEC, 0x60,                                                  // RA01: sub rsp, 60h - room for xmm0 to xmm5
        0x0F, 0x11, 0x45, 0xA0, 0x0F, 0x11, 0x4D, 0xB0, 0x0F, 0x11, 0x55, 0xC0,  // RA01: save xmm0, xmm1, xmm2
        0x0F, 0x11, 0x5D, 0xD0, 0x0F, 0x11, 0x65, 0xE0, 0x0F, 0x11, 0x6D, 0xF0,  // RA01: save xmm3, xmm4, xmm5
        0x48, 0x83, 0xE4, 0xF0, 0x48, 0x83, 0xEC, 0x20,                          // RA01: line the stack up and reserve the 32 bytes Windows expects
        0x48, 0xB8,                                                              // RA01: mov rax, <8 bytes: OnPoseList>
    };
    static ReadOnlySpan<byte> TrampolineTail => new byte[]
    {
        0xFF, 0xD0,                                                              // RA01: call rax
        0x0F, 0x10, 0x45, 0xA0, 0x0F, 0x10, 0x4D, 0xB0, 0x0F, 0x10, 0x55, 0xC0,  // RA01: put xmm0 to xmm2 back
        0x0F, 0x10, 0x5D, 0xD0, 0x0F, 0x10, 0x65, 0xE0, 0x0F, 0x10, 0x6D, 0xF0,  // RA01: put xmm3 to xmm5 back
        0x48, 0x89, 0xEC, 0x5D,                                                  // RA01: mov rsp, rbp; pop rbp
        0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D,  // RA01: pop r11 r10 r9 r8 rdx rcx rax, popfq
        0x40, 0x53, 0x48, 0x83, 0xEC, 0x40,                                      // RA01: the two instructions the jump replaced: push rbx; sub rsp, 40h
        0xFF, 0x25, 0x00, 0x00, 0x00, 0x00,                                      // RA01: jmp [rip+0] - back into the game, just after them
    };

    static int installed;
    static delegate*<nint, nint, void> handler; // RA01: set once by PlayerSkeleton; null means the doorway does nothing

    // RA01: PlayerSkeleton signs up here to be handed each character's bone list.
    // Cost - CPU: extremely low (stores one pointer, once); GPU: none.
    public static void SetHandler(delegate*<nint, nint, void> h) => handler = h;

    // RA01: Cost - CPU: low, once (one scan of the code section, about 10 ms, and the same brief thread pause CameraHook uses to write 5 bytes); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = HookRegistry.FindAll(HookRegistry.Pose, BuildBody); // RA01: ExeScan was already loaded by CameraHook.Install. M43 - reported to the Rehooker
            if (found.Count != 1)
            {
                Log.Write($"RA01 pose hook NOT installed: the full-body step was found {found.Count} times (needs exactly 1), so this game build may differ");
                return;
            }
            nint site = found[0];
            nint trampoline = CameraHook.AllocateNear(site);
            if (trampoline == 0) { Log.Write("RA01 pose hook NOT installed: no free memory within reach"); HookRegistry.Off(HookRegistry.Pose, "no free memory within reach"); return; }

            byte* code = (byte*)trampoline;
            int at = 0;
            TrampolineHead.CopyTo(new Span<byte>(code + at, TrampolineHead.Length)); at += TrampolineHead.Length;
            *(long*)(code + at) = (nint)(delegate* unmanaged<nint, nint, void>)&OnPoseList; at += 8;
            TrampolineTail.CopyTo(new Span<byte>(code + at, TrampolineTail.Length)); at += TrampolineTail.Length;
            *(long*)(code + at) = site + ReplacedLength;

            if (!CameraHook.WriteJump(site, trampoline)) { Log.Write("RA01 pose hook NOT installed: the jump could not be written (nothing was changed)"); HookRegistry.Off(HookRegistry.Pose, "the jump could not be written"); return; }
            Log.Write($"RA01 pose hook installed at RVA 0x{site - ExeScan.Base:X}");
        }
        catch (Exception e) { Log.Write($"RA01 pose hook failed: {e.Message}"); }
    }

    // RA01: the game calls this (through the detour) for every animated character, every frame, just before building its full body.
    // Cost - CPU: extremely low (a pointer check and one call, a few hundred times a second); GPU: none.
    [UnmanagedCallersOnly]
    static void OnPoseList(nint list, nint skeleton)
    {
        try
        {
            delegate*<nint, nint, void> h = handler;
            if (h != null && list > 0x10000) h(list, skeleton);
        }
        catch { } // RA01: an error must never travel back into the game's own code
    }
}
