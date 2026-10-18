using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M22-4 - THE THROW LEVER (always installed at start-up; moved here from the throw probe once both levers were confirmed, Rob 2026-10-07 14:29-14:49).
// Every projectile's flight path is handed to it at one place (2.31: exe+0x84F9AC, "set trajectory"). A thrown grenade (and knife) gets a CURVED path:
// a 0xD0-byte object (built at exe+0x473508) holding a copy of its arc settings - +0x90 the launch speed (grenades 25 held / 19 quick), +0xA0 gravity
// (0, 0, -10.5), +0xB0 a landing target (unset), +0xC0 an angle, +0xC8 the start mode. The path's start routine (exe+0xAD633C) reads the mode:
//   0 (the game's own throws) = the launch speed along the way V looks;  1 (exe+0xAD6500) = from the start point to the TARGET, leaving at the ANGLE (radians -
//   0.6 gave a clear lob in Rob's test), the speed worked out by the game itself.
// The lever is a small machine-code step at that place with an ORDER CARD in its ring's header. Actions (PhysicalThrow) puts an order on the card the moment
// V's own grenade leaves her hand; the next curved path takes it (and clears it in one locked step, so one order changes one path); an order not taken
// within OrderLifeMs is withdrawn, so it can never land on a later projectile. It also keeps copying each curved path's settings into its ring for the
// throw probe's log (ThrowProbe reads Ring).
// Builder picture: a clipboard hung where every grenade picks up its launch card - when V throws, we clip our own card on top; the grenade takes it and flies by it.
// M22-5 - knives use it too: a knife's curved path takes the same order (a point along your swing and the angle that sends a 110 m/s knife through it).
// Any mismatch on a new game version: logged NOT installed, nothing changed - throws stay the game's own.
// Cost - CPU: extremely low (about 40 machine instructions each time any projectile gets a flight path; nothing else runs here); GPU: none.
internal static unsafe class ThrowLever
{
    // RA01: AddParabolic's worker (exe+0x47323C): its first bytes, told apart from a look-alike by the copy routine it calls at +0x28 (whose bytes at +0x1F copy the
    // RA01: arc's +0x40, +0x50 and +0x60). Not hooked here - it is only where the curved path's builder, and so the curved type, is read from (and the throw probe's arc tap).
    static ReadOnlySpan<byte> ArcHead => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x48, 0x81, 0xEC, 0xB0, 0x00, 0x00, 0x00, 0x48, 0x8B, 0xDA, 0x48, 0x8B, 0xF1, 0xE8 };
    static ReadOnlySpan<byte> ArcCopy => new byte[] { 0x8B, 0x43, 0x40, 0x89, 0x47, 0x40, 0x0F, 0x10, 0x43, 0x50, 0x0F, 0x11, 0x47, 0x50, 0x0F, 0x10, 0x4B, 0x60, 0x0F, 0x11 };
    const int CopyCallAt = 0x28, CopyBytesAt = 0x1F, CreateCallAt = 0x33, CreateTableLeaAt = 0x0D;

    // RA01: the "set trajectory" routine's first bytes: mov [rsp+8],rbx / push rdi / sub rsp,20h / lea rbx,[rcx+470h] / mov rdi,rdx / mov rcx,rbx / call (one place in 2.31).
    static ReadOnlySpan<byte> PathHead => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8D, 0x99, 0x70, 0x04, 0x00, 0x00, 0x48, 0x8B, 0xFA, 0x48, 0x8B, 0xCB, 0xE8 };

    // RA01: the lever's machine code, checked with a disassembler (capstone) before delivery:
    // RA01:  00 mov r11, <ring> (8 bytes at +0x02) / mov eax,1 / lock xadd [r11],eax / and eax,3FFh / shl eax,8 / add rax,r11 / add rax,50h  - the next 256-byte slot
    // RA01:  23 mov [rax-10h],rcx (path holder) / mov [rax-8],rdx / test rdx,rdx / je FE / mov r10,[rdx] (the path) / test r10,r10 / je FE
    // RA01:  40 mov r11,[r10] / mov [rax],r11 (its type table) / cmp r11,[rip+0D7h] (the curved type, 8 bytes at +0x124) / jne 111  - only curved paths go on
    // RA01:  53 eight times: mov r11,[r10+90h+8k] / mov [rax+8+8k],r11                                                - the arc settings, for the probe's log
    // RA01:  AB mov r11,<ring> (8 bytes at +0xAD) / xor eax,eax / xchg [r11+8],eax                                     - take the order and clear it, one locked step
    // RA01:     bit 1: movss xmm5,[r10+90h] / mulss xmm5,[r11+10h] / movss [r10+90h],xmm5                               - the launch speed times a factor (tests)
    // RA01:     bit 4: movss xmm5,[r11+14h] / movss [r10+0A8h],xmm5                                                     - M22-5c: the path's gravity (its z, down)
    // RA01:     bit 2: movups xmm5,[r11+20h] / movups [r10+0B0h],xmm5 (target) / mov eax,[r11+30h] / mov [r10+0C0h],eax (angle) / mov eax,[r11+38h] / mov [r10+0C8h],eax (mode)
    // RA01:            (bit 2 last: it reuses eax, which holds the order bits)
    // RA01:  111 mov [rsp+8],rbx (the routine's own first instruction) / jmp [rip+0] -> the 8 bytes at +0x11C (the routine, 5 bytes in)
    // RA01: Only rax, r10, r11, xmm5 and the flags change - all free at a function's first instruction on Windows; rcx and rdx (its inputs) are untouched.
    static ReadOnlySpan<byte> PathDetour => new byte[] { 0x49, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x41, 0x0F, 0xC1, 0x03, 0x25, 0xFF, 0x03, 0x00, 0x00, 0xC1, 0xE0, 0x08, 0x4C, 0x01, 0xD8, 0x48, 0x83, 0xC0, 0x50, 0x48, 0x89, 0x48, 0xF0, 0x48, 0x89, 0x50, 0xF8, 0x48, 0x85, 0xD2, 0x0F, 0x84, 0xDD, 0x00, 0x00, 0x00, 0x4C, 0x8B, 0x12, 0x4D, 0x85, 0xD2, 0x0F, 0x84, 0xD1, 0x00, 0x00, 0x00, 0x4D, 0x8B, 0x1A, 0x4C, 0x89, 0x18, 0x4C, 0x3B, 0x1D, 0xD7, 0x00, 0x00, 0x00, 0x0F, 0x85, 0xBE, 0x00, 0x00, 0x00, 0x4D, 0x8B, 0x9A, 0x90, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x08, 0x4D, 0x8B, 0x9A, 0x98, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x10, 0x4D, 0x8B, 0x9A, 0xA0, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x18, 0x4D, 0x8B, 0x9A, 0xA8, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x20, 0x4D, 0x8B, 0x9A, 0xB0, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x28, 0x4D, 0x8B, 0x9A, 0xB8, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x30, 0x4D, 0x8B, 0x9A, 0xC0, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x38, 0x4D, 0x8B, 0x9A, 0xC8, 0x00, 0x00, 0x00, 0x4C, 0x89, 0x58, 0x40, 0x49, 0xBB, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x31, 0xC0, 0x41, 0x87, 0x43, 0x08, 0xA8, 0x01, 0x74, 0x18, 0xF3, 0x41, 0x0F, 0x10, 0xAA, 0x90, 0x00, 0x00, 0x00, 0xF3, 0x41, 0x0F, 0x59, 0x6B, 0x10, 0xF3, 0x41, 0x0F, 0x11, 0xAA, 0x90, 0x00, 0x00, 0x00, 0xA8, 0x04, 0x74, 0x0F, 0xF3, 0x41, 0x0F, 0x10, 0x6B, 0x14, 0xF3, 0x41, 0x0F, 0x11, 0xAA, 0xA8, 0x00, 0x00, 0x00, 0xA8, 0x02, 0x74, 0x23, 0x41, 0x0F, 0x10, 0x6B, 0x20, 0x41, 0x0F, 0x11, 0xAA, 0xB0, 0x00, 0x00, 0x00, 0x41, 0x8B, 0x43, 0x30, 0x41, 0x89, 0x82, 0xC0, 0x00, 0x00, 0x00, 0x41, 0x8B, 0x43, 0x38, 0x41, 0x89, 0x82, 0xC8, 0x00, 0x00, 0x00, 0x48, 0x89, 0x5C, 0x24, 0x08, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    const int RingAt = 0x02, Ring2At = 0xAD, BackAt = 0x11C, TypeAt = 0x124, Replaced = 5;
    public const int HeaderBytes = 0x40, Slots = 1024, SlotBytes = 256;          // RA01: the ring the throw probe reads: slot n's copier base is Ring + 0x50 + n x 256
    const int OrderAt = 0x08, SpeedScaleAt = 0x10, GravityAt = 0x14, TargetAt = 0x20, AngleAt = 0x30, ModeAt = 0x38;   // RA01: the order card, in the ring's header
    const int OrderSpeed = 1, OrderTarget = 2, OrderGravity = 4, AimAtTarget = 1;
    const long OrderLifeMs = 1500;

    static nint ring;
    static long orderedAt;
    static string orderText = "";
    static int installed;

    // RA01: the ring (0 = not installed) and the curved path's type table, for the throw probe. Cost - CPU: extremely low; GPU: none.
    public static nint Ring => ring;
    public static long CurvedType { get; private set; }
    public static nint ArcWorker { get; private set; }                         // RA01: for the throw probe's arc tap (0 = not found)

    // RA01: true once the lever is in. Cost - CPU: extremely low (one read); GPU: none.
    public static bool Ready => ring != 0;

    // RA01: finds the two places, reads the curved type, makes the ring and puts the lever in. Call once at start-up (after CameraHook, which loads ExeScan).
    // Cost - CPU: low, once (two byte searches of the exe and a 5-byte write); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> arcs = new();
            foreach (nint hit in ExeScan.FindAll(ArcHead))
                if (IsArcWorker(hit)) arcs.Add(hit);
            nint listed = arcs.Count != 1 ? HookRegistry.InFunction(HookRegistry.ThrowArc, ArcHead) : 0;   // RA01: M43 - not exactly one: try the function the Rehooker names
            if (listed != 0 && IsArcWorker(listed)) arcs = new() { listed };
            if (arcs.Count != 1) { Log.Write($"RA01 throw lever NOT installed: AddParabolic's worker found {arcs.Count} times (needs exactly 1) - throws stay the game's own"); HookRegistry.Off(HookRegistry.ThrowArc, $"found {arcs.Count} times (needs 1)"); HookRegistry.Off(HookRegistry.ThrowPath, "needs the arc worker"); return; }
            HookRegistry.Found(HookRegistry.ThrowArc, arcs[0], listed != 0 ? "by its part number, bytes checked" : "by its pattern and its copy call");
            ArcWorker = arcs[0];
            nint create = ArcWorker + CreateCallAt + 5 + *(int*)(ArcWorker + CreateCallAt + 1);
            byte* lea = (byte*)(create + CreateTableLeaAt);
            if (*(byte*)(ArcWorker + CreateCallAt) != 0xE8 || lea[0] != 0x48 || lea[1] != 0x8D || lea[2] != 0x05) { Log.Write("RA01 throw lever NOT installed: the curved flight path's type could not be read - throws stay the game's own"); HookRegistry.Off(HookRegistry.ThrowPath, "the curved flight path's type could not be read"); return; }
            CurvedType = create + CreateTableLeaAt + 7 + *(int*)(lea + 3);
            ring = Hook("RA01 throw lever", HookRegistry.FindAll(HookRegistry.ThrowPath, PathHead), PathDetour, RingAt, BackAt, Replaced, HeaderBytes + Slots * SlotBytes, TypeAt, CurvedType, Ring2At);
            if (ring != 0) Log.Write("RA01 throw lever: ready - V's own throws can be given a landing point and angle (Actions\\PhysicalThrow.cs)");
            else HookRegistry.Off(HookRegistry.ThrowPath, "the flight-path hook could not be put in");
        }
        catch (Exception e) { Log.Write($"RA01 throw lever failed: {e.Message}"); }
    }

    // RA01: M43 - true if 'hit' is AddParabolic's worker: its copy call leads to the 2.31 copy bytes. Cost - CPU: extremely low (one short compare); GPU: none.
    static bool IsArcWorker(nint hit)
    {
        nint copy = hit + CopyCallAt + 5 + *(int*)(hit + CopyCallAt + 1);
        return new ReadOnlySpan<byte>((byte*)(copy + CopyBytesAt), ArcCopy.Length).SequenceEqual(ArcCopy);
    }

    // RA01: one machine-code hook: exactly one site, a ring, the code with its ring(s), way back and an extra 8-byte value filled in, and the jump. Shared with the
    // throw probe's arc tap. Returns the ring (0 = not installed, logged with 'name').
    // Cost - CPU: low, once (two small allocations and a 5-byte write); GPU: none.
    public static nint Hook(string name, List<nint> found, ReadOnlySpan<byte> code, int ringAt, int backAt, int replaced, int ringBytes, int extraAt = 0, long extra = 0, int ring2At = 0)
    {
        if (found.Count != 1) { Log.Write($"{name} NOT installed - its place was found {found.Count} times (needs exactly 1)"); return 0; }
        nint site = found[0];
        nint mem = VirtualAlloc(0, (nuint)ringBytes, 0x3000, 0x04);              // RA01: MEM_COMMIT|MEM_RESERVE, read/write
        nint detour = mem != 0 ? CameraHook.AllocateNear(site) : 0;
        if (detour == 0) { Log.Write($"{name} NOT installed - no memory for it"); return 0; }
        code.CopyTo(new Span<byte>((byte*)detour, code.Length));
        *(long*)(detour + ringAt) = mem;
        if (ring2At != 0) *(long*)(detour + ring2At) = mem;
        *(long*)(detour + backAt) = site + replaced;
        if (extraAt != 0) *(long*)(detour + extraAt) = extra;
        if (!CameraHook.WriteJump(site, detour)) { Log.Write($"{name} NOT installed - the jump could not be written (nothing was changed)"); return 0; }
        Log.Write($"{name}: hooked at RVA 0x{site - ExeScan.Base:X}");
        return mem;
    }

    // RA01: orders the next curved path (V's grenade, about to fly) to land on (x, y, z) - game world metres, z up - leaving at 'angle' radians. 'why' goes in the log
    // when the order is used. M22-5c - 'gravity' (m/s2, > 0) also replaces the path's own pull, so the game both aims and flies by it (0 = keep the game's).
    // The flag is written last, so the lever never sees half an order. False if the lever is not in.
    // Cost - CPU: extremely low (a few stores); GPU: none.
    public static bool OrderTargetLanding(float x, float y, float z, float angle, string why, float gravity = 0f)
    {
        if (ring == 0) return false;
        byte* card = (byte*)ring;
        float* t = (float*)(card + TargetAt);
        t[0] = x; t[1] = y; t[2] = z; t[3] = 1f;
        *(float*)(card + AngleAt) = angle;
        *(int*)(card + ModeAt) = AimAtTarget;
        *(float*)(card + SpeedScaleAt) = 1f;
        orderText = why;
        orderedAt = Environment.TickCount64;
        *(float*)(card + GravityAt) = -gravity;
        Volatile.Write(ref *(int*)(card + OrderAt), gravity > 0f ? OrderTarget | OrderGravity : OrderTarget);
        return true;
    }

    // RA01: M36-1 - NO GRENADE AIM LINES (Weapons tab "Physical grenade throw" on; Rob approved 2026-10-10). A grenade gets its curved flight path the moment it
    // appears in V's hand, well before "charging" asks for the arc. The path's holder is the path slot 0x130 into the grenade's projectile part
    // (gameprojectileComponent; exe+0x24EC72B: lea rcx,[rdi+130h]), so the part = holder - 0x130, checked by its class name.
    // M36-1c - how the arc starts: the game's preview handler (exe+0x24EC978) calls a start routine (exe+0x84FF7C) that signs the part up with the
    // arc-drawing system ONLY IF its "preview is showing" byte (+0x120) is still 0, then sets it to 1. So that byte is set to 1 here first (1-byte safe
    // write, only if it was 0): the game believes the arc is already up and never draws it. (M36-1/1b cleared the three effect slots +0x878/+0x880/+0x888
    // - the right part, but too late: the effects are loaded when the grenade is built.) The flight itself is untouched.
    // Builder picture: hanging an "already painted" tag on the crate, so the painter walks past it.
    // Cost - CPU: extremely low (usually nothing new; a few safe reads and one 1-byte write per grenade drawn); GPU: none (slightly less - the arc isn't drawn).
    public static void HidePreviews(bool vHoldsGrenade)
    {
        if (ring == 0) return;
        uint count = (uint)Volatile.Read(ref *(int*)ring);
        if (!vHoldsGrenade || count - previewNext > Slots) { previewNext = count; return; }
        for (; previewNext != count; previewNext++)
        {
            byte* slot = (byte*)ring + HeaderBytes + (previewNext & (Slots - 1)) * SlotBytes + 0x10;   // RA01: holder at -0x10, path type at 0 (see the lever's code)
            if (*(long*)slot != CurvedType) continue;
            nint holder = (nint)(*(long*)(slot - 0x10));
            nint part = holder - PathInPart;                                    // RA01: M36-1b - the path sits 0x130 into the projectile part (exe+0x24EC72B: lea rcx,[rdi+130h])
            nint cls = 0; ulong name = 0;
            if (holder < 0x10000 || !SafeRead(part + 0x30, &cls, 8) || cls < 0x10000 || !SafeRead(cls + 0x18, &name, 8)) continue;
            bool projectile = name == ProjectileComponentName;
            if (!previewLogged) { previewLogged = true; Log.Write(projectile ? "RA01 M36 no grenade aim lines: the grenade's flight path belongs to its projectile part - its arc and landing circle will be blocked" : $"RA01 M36 no grenade aim lines: the flight path's owner is NOT a projectile part (class code 0x{name:X16}; its +0x878/+0x880/+0x888 = {Peek(part + PreviewEffectsAt)}/{Peek(part + PreviewEffectsAt + 8)}/{Peek(part + PreviewEffectsAt + 16)}) - nothing cleared"); }
            if (!projectile) continue;
            LastPart = part;   // RA01: M44 - for FieldNames' probe
            byte flag = 0, one = 1; nuint wrote;
            if (SafeRead(part + PreviewShownAt, &flag, 1) && flag == 0 && WriteProcessMemory(Self, part + PreviewShownAt, &one, 1, &wrote) && wrote == 1 && previewLines++ < 3)
                Log.Write("RA01 M36 no grenade aim lines: the grenade in V's hand is marked 'preview already showing' - the game will not start its arc");
        }
    }
    static readonly ulong ProjectileComponentName = GameAnchor.NameCode("gameprojectileComponent");
    internal static nint LastPart;   // RA01: M44 - the newest grenade projectile part seen in V's hand (0 = none yet); FieldNames' probe reads it
    const int PathInPart = 0x130;                                              // RA01: M36-1b - the flight-path slot's place inside the projectile part
    const int PreviewEffectsAt = 0x878;                                        // RA01: M36-1 - previewEffect, bouncePreviewEffect (+8), explosionPreviewEffect (+0x10) - for the log only (M36-1c)
    const int PreviewShownAt = 0x120;                                          // RA01: M36-1c - the part's "preview is showing" byte (exe+0x84FF8B / +0x84FFD4)
    static uint previewNext;
    static bool previewLogged;
    static readonly nint Self = GetCurrentProcess();
    static int previewLines;

    // RA01: M36-1b - 8 bytes as hex for the log ("?" if unreadable). Cost - CPU: extremely low (one safe read); GPU: none.
    static string Peek(nint at) { long v = 0; return SafeRead(at, &v, 8) ? $"0x{v:X}" : "?"; }

    // RA01: M36-1 - safe read: a bad address fails this one call instead of crashing the game. Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool SafeRead(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }

    // RA01: call often while an order may be out (PhysicalThrow does, every controller read): logs when the order was used, or withdraws it after OrderLifeMs.
    // Cost - CPU: extremely low (one read while an order is out, nothing otherwise); GPU: none.
    public static void Tick()
    {
        if (orderedAt == 0 || ring == 0) return;
        long now = Environment.TickCount64;
        int* flag = (int*)((byte*)ring + OrderAt);
        if (Volatile.Read(ref *flag) == 0) { Log.Write($"RA01 throw lever: order USED {now - orderedAt} ms after release - {orderText}"); orderedAt = 0; return; }
        if (now - orderedAt < OrderLifeMs) return;
        bool unused = Interlocked.Exchange(ref *flag, 0) != 0;
        Log.Write(unused ? "RA01 throw lever: order NOT used within 1.5 s - withdrawn (the grenade got no curved flight path)" : $"RA01 throw lever: order USED - {orderText}");
        orderedAt = 0;
    }
}
