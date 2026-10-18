namespace RA01.Core;

// RA01: M21-9 - THE WIDE-VIEW DISTANCE FIX (Dev tab "Wide-view distance fix", on by default, saved - M21-9c). Why the All Foods lab walls vanish at a wide view (M21-8, Rob's
// Cheat Engine hunt 2026-10-07):
//  - every frame the game builds each render camera (2.31: exe+0x4E4030) and, last of all, works out a "distance scale" from that camera's field of view:
//      scale = max(1, 1.6984 * tan(fov / 2)^2)   - that is (tan(fov/2) / tan(37.5 deg))^2: 1 for any view up to 75 degrees tall, 1.70 at 90, 2.41 at 100;
//  - every object's squared distance from the camera is multiplied by that scale before its "hide past this distance" check (exe+0x1E8A54 / 0x1E8B48).
//    The lab wall's tag says 153.8 m, so at 90 degrees it is dropped once its anchor point is 118 m away - and its anchor is far outside the room you
//    stand in. The flat game never draws views this tall, so it never trips there.
// The fix: one instruction - the one that stores the scale - is sent through a 4-instruction detour that first caps it ("minss": the smaller of the
// two). Cap 1 = every camera measures distance exactly as a normal flat view does. Off (Dev row) = the cap is set impossibly high, so the game's own value passes
// through unchanged (no unhooking needed). Pure machine code, no C# in the frame - nothing runs in the mod per frame.
// Builder picture: the foreman's measuring tape stretches when the window is wide; this pins the tape at its normal length.
// Cost - CPU: extremely low (one extra compare per render camera per frame, a few dozen a frame); GPU: low to medium when on - objects the game used to
// drop early at a wide view (far props, small details) are now kept as far out as in flat play, so a little more is drawn.
internal static unsafe class DistanceScaleFix
{
    // RA01: the end of the camera builder, found by its own bytes (2.31: exe+0x4E427B): "mulss xmm0,xmm0 / mov rax,rbx / mulss xmm0,[factor]" ...
    // RA01: "maxss xmm0,[1.0]" at +0x0F ... "movss [rbx+24h],xmm0 / add rsp,20h / pop rbx / ret" at +0x17. The two numbers it reads are checked too
    // RA01: (1.6984 and 1.0), so a game update that changes the formula is noticed and nothing is hooked.
    static ReadOnlySpan<byte> Head => new byte[] { 0xF3, 0x0F, 0x59, 0xC0, 0x48, 0x8B, 0xC3, 0xF3, 0x0F, 0x59, 0x05 };
    static ReadOnlySpan<byte> MaxPart => new byte[] { 0xF3, 0x0F, 0x5F, 0x05 };
    static ReadOnlySpan<byte> StoreAndReturn => new byte[] { 0xF3, 0x0F, 0x11, 0x43, 0x24, 0x48, 0x83, 0xC4, 0x20, 0x5B, 0xC3 };
    const int FactorInstrAt = 0x07, MaxAt = 0x0F, StoreAt = 0x17, InstrLength = 8, StoreLength = 5;
    const float GameFactor = 1.6984007f, CapOn = 1f, CapOff = float.MaxValue;

    // RA01: the detour: minss xmm0,[rip+18h] (the cap, at +0x20) / movss [rbx+24h],xmm0 (the game's own store) / jmp [rip+0] back to after the store.
    static ReadOnlySpan<byte> Detour => new byte[] { 0xF3, 0x0F, 0x5D, 0x05, 0x18, 0x00, 0x00, 0x00, 0xF3, 0x0F, 0x11, 0x43, 0x24, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    // RA01: the jump-back starts at +0x0D (8-byte minss + 5-byte movss) and reads its 8-byte target from the 6 bytes after it, +0x13; the cap sits at +0x20.
    // RA01: M21-9b - this was 0x13 in the first build, which put the target 6 bytes too far (+0x19): the jump read a broken address and the game faulted
    // RA01: the first time a camera was built (black box: 0xC0000005 at detour+0x0D, reading 0xFFFFFFFFFFFFFFFF).
    const int BackAt = 0x0D, CapAt = 0x20;

    static float* cap;                 // RA01: the cap, inside the detour's own memory (null = not installed)
    static int installed;
    static bool applied;               // RA01: M21-9c - what the cap was last set to (on / off), so Sync only writes when Tuning.WideViewDistanceFix changes

    // RA01: true while the cap is installed and switched on - Tuning.MaxGameTallDeg only allows views taller than 90 degrees then.
    // RA01: Cost - CPU: extremely low (two reads); GPU: none.
    public static bool Active => Tuning.WideViewDistanceFix && cap != null;

    // RA01: the Dev tab row: the saved setting (Tuning.WideViewDistanceFix), applied to the cap at once. Takes effect on the next camera build.
    // RA01: Cost - CPU: extremely low (one bool write and Sync); GPU: as the class comment, while on.
    public static bool On
    {
        get => Tuning.WideViewDistanceFix;
        set
        {
            Tuning.WideViewDistanceFix = value;
            Sync();
            if (cap == null) Log.Write("RA01 distance fix: not installed - the switch does nothing (see the start-up line)");
        }
    }

    // RA01: M21-9c - makes the cap match the saved setting. Called by the row above and once per headset frame by SettingsFile.Poll, so a value read from the
    // settings file or set by Reset all takes hold too. Writes and logs only when the setting changed.
    // Cost - CPU: extremely low (one compare per headset frame; one float write and one log line on a change); GPU: none.
    public static void Sync()
    {
        bool want = Tuning.WideViewDistanceFix;
        if (cap == null || want == applied) return;
        applied = want;
        Volatile.Write(ref *cap, want ? CapOn : CapOff);
        Log.Write($"RA01 distance fix: {(want ? "ON - every camera measures distance as a normal flat view (scale capped at 1)" : "off - the game's own distance scale")}");
    }

    // RA01: finds the store by its bytes, checks the game's two numbers, builds the detour (cap set from the saved setting) and puts the jump in. Call once at start-up.
    // Cost - CPU: low, once (one byte search of the exe and the brief pause while 5 bytes are written); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = new();
            foreach (nint hit in ExeScan.FindAll(Head))
                if (Same(hit + MaxAt, MaxPart) && Same(hit + StoreAt, StoreAndReturn)) found.Add(hit);
            nint listed = found.Count != 1 ? HookRegistry.InFunction(HookRegistry.DistanceScale, Head) : 0;   // RA01: M43 - not exactly one: try the function the Rehooker names
            if (listed != 0 && Same(listed + MaxAt, MaxPart) && Same(listed + StoreAt, StoreAndReturn)) found = new() { listed };
            if (found.Count != 1) { Log.Write($"RA01 distance fix NOT installed: the camera builder's end was found {found.Count} times (needs exactly 1)"); HookRegistry.Off(HookRegistry.DistanceScale, $"found {found.Count} times (needs 1)"); return; }
            HookRegistry.Found(HookRegistry.DistanceScale, found[0], listed != 0 ? "by its part number, bytes checked" : "by its pattern");
            nint head = found[0];
            float factor = *(float*)RipTarget(head + FactorInstrAt), floor = *(float*)RipTarget(head + MaxAt);
            if (MathF.Abs(factor - GameFactor) > 0.001f || floor != 1f) { Log.Write($"RA01 distance fix NOT installed: the game's formula changed (factor {factor}, floor {floor}; expected {GameFactor} and 1)"); HookRegistry.Off(HookRegistry.DistanceScale, "a later check failed (see the Core VR log)"); return; }

            nint site = head + StoreAt;
            nint detour = CameraHook.AllocateNear(site);
            if (detour == 0) { Log.Write("RA01 distance fix NOT installed: no free memory within reach"); HookRegistry.Off(HookRegistry.DistanceScale, "a later check failed (see the Core VR log)"); return; }
            Detour.CopyTo(new Span<byte>((byte*)detour, Detour.Length));
            *(long*)(detour + BackAt + 6) = site + StoreLength;
            cap = (float*)(detour + CapAt);
            applied = Tuning.WideViewDistanceFix;
            *cap = applied ? CapOn : CapOff;
            if (!CameraHook.WriteJump(site, detour)) { cap = null; Log.Write("RA01 distance fix NOT installed: the jump could not be written (nothing was changed)"); HookRegistry.Off(HookRegistry.DistanceScale, "a later check failed (see the Core VR log)"); return; }
            Log.Write($"RA01 distance fix: hooked the camera builder's distance scale at RVA 0x{site - ExeScan.Base:X} (game formula checked: max(1, {factor:F4} x tan(fov/2)^2)); starts {(applied ? "ON" : "off")}");
        }
        catch (Exception e) { Log.Write($"RA01 distance fix failed: {e.Message}"); }
    }

    // RA01: true if the bytes at 'at' are 'expected'. Cost - CPU: extremely low; GPU: none.
    static bool Same(nint at, ReadOnlySpan<byte> expected) => new ReadOnlySpan<byte>((byte*)at, expected.Length).SequenceEqual(expected);

    // RA01: where an 8-byte "op xmm0,[rip+disp32]" instruction at 'at' reads from. Cost - CPU: extremely low; GPU: none.
    static nint RipTarget(nint at) => at + InstrLength + *(int*)(at + 4);
}
