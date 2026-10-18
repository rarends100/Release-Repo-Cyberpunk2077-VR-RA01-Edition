using System.Text;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M21-8 - THE ROOM PROBE (LOG ONLY, Dev row "Room probe (log)"). The All Foods lab's walls vanish at a wide view because one big world mesh -
// loc_meat_factory_lab_01.mesh (node in interior_-7_17_0_2.streamingsector) - is skipped when drawing (RedHotTools' "Toggle node visibility" on that node
// looks exactly like the bug; bullets still hit it, so it is loaded and solid). This probe finds that one node's live objects so Cheat Engine can watch them:
//  - the game's "a world node is being attached" function (RedHotTools / CDPR address list: WorldNodeInstance_Attach, 2.31 exe+0x3C7C14) gets a detour
//    6 bytes in (found by the 29 bytes there, exactly once - RedHotTools may hold the first 6); every node attached passes through it, instance in rcx;
//  - an instance's node definition is at +0x60, and a mesh node's mesh path hash at node +0x38 - matched against the lab mesh's hash (WolvenKit:
//    14350028518106481841);
//  - the instance's render proxy is at +0xC8 (its flags byte at +0x23: drawn only if bits 0x02 and 0x04 are both set) and the proxy's link to the
//    renderer's own object at proxy +0x10 (RedHotTools' layout).
// It logs those addresses once, then - while the Dev row is on - twice a second compares the instance (0x100 bytes), the proxy (0x80) and the renderer's
// object (0x100) with the previous look and logs which 4-byte values changed. Toggling the view wide/narrow then shows whether anything there follows the
// walls; the logged addresses go straight into Cheat Engine ("find what accesses / writes"). Every read goes through ReadProcessMemory, so a gone object
// fails a read instead of crashing. Changes nothing in the game.
// Builder picture: a tag on one specific wall panel, so we can stand by it and watch who comes to take it down.
// To catch the attach, load a save made OUTSIDE the factory and walk in.
// Cost - CPU: low while the factory loads (one short check per attached node, a few safe reads each); extremely low otherwise (one int test per attach; while
// on, three small safe reads and compares twice a second); GPU: none.
internal static unsafe class NodeProbe
{
    // RA01: the attach function from its 7th byte on (2.31: exe+0x3C7C1A), found exactly once. Its first 6 bytes (push rbx; sub rsp, 20h) are skipped on
    // RA01: purpose: RedHotTools puts its own detour there, so the probe sits on the next two instructions instead - rcx still holds the instance.
    static ReadOnlySpan<byte> AttachBody => new byte[] { 0x48, 0x8B, 0x01, 0x48, 0x8B, 0xD9, 0x80, 0x89, 0x88, 0x00, 0x00, 0x00, 0x84, 0xFF, 0x90, 0x68, 0x01, 0x00, 0x00, 0x8A, 0x83, 0x88, 0x00, 0x00, 0x00, 0x24, 0xFB, 0x0C, 0x30 };
    static ReadOnlySpan<byte> AttachReplaced => new byte[] { 0x48, 0x8B, 0x01, 0x48, 0x8B, 0xD9 }; // RA01: mov rax, [rcx]; mov rbx, rcx
    const ulong LabMeshHash = 14350028518106481841UL;
    const int NodeAt = 0x60, MeshHashAt = 0x38, ProxyAt = 0xC8, ProxyFlagsAt = 0x23, RenderObjectAt = 0x10;
    const int InstanceBytes = 0x100, ProxyBytes = 0x80, RenderBytes = 0x100, MaxChangesPerLine = 16;
    static readonly nint Self = GetCurrentProcess();
    static nint instance;                      // RA01: the lab node's live instance (0 = not seen yet)
    static int installed, started;
    public static bool On;                     // RA01: the Dev row; off at every start

    // RA01: finds the attach function by its bytes and puts the detour on it; starts the watch thread. Call once at start-up, before any world loads.
    // Cost - CPU: low, once (one 25-byte search of the code and the detour's brief pause); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = HookRegistry.FindAll(HookRegistry.NodeAttach, AttachBody);   // RA01: M43 - reported to the Rehooker
            if (found.Count != 1) { Log.Write($"RA01 room probe NOT installed: the node attach was found {found.Count} times (needs exactly 1)"); return; }
            if (!CrosshairProbe.Hook(found[0], AttachReplaced, (nint)(delegate* unmanaged<nint, void>)&OnAttach, "world node attach (room probe)")) { HookRegistry.Off(HookRegistry.NodeAttach, "the hook could not be put in"); return; }
            if (Interlocked.Exchange(ref started, 1) == 0) new Thread(Watch) { IsBackground = true, Name = "RA01 room probe" }.Start();
        }
        catch (Exception e) { Log.Write($"RA01 room probe failed: {e.Message}"); }
    }

    // RA01: the detour, on the game's streaming threads, for every world node attached: is it the lab mesh? Never throws back into the game.
    // Cost - CPU: extremely low per node (two safe reads and a compare); GPU: none.
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void OnAttach(nint inst)
    {
        try
        {
            if (inst < 0x10000) return;
            nint node; ulong hash;
            if (!Read(inst + NodeAt, &node, 8) || node < 0x10000 || !Read(node + MeshHashAt, &hash, 8) || hash != LabMeshHash) return;
            Volatile.Write(ref instance, inst);
            nint proxy = 0, render = 0; byte flags = 0;
            if (Read(inst + ProxyAt, &proxy, 8) && proxy >= 0x10000) { Read(proxy + ProxyFlagsAt, &flags, 1); Read(proxy + RenderObjectAt, &render, 8); }
            Log.Write($"RA01 room probe: lab mesh node attached - instance 0x{inst:X}, node 0x{node:X}, render proxy 0x{proxy:X} (flags at 0x{proxy + ProxyFlagsAt:X} = 0x{flags:X2}), renderer's object 0x{render:X}. The proxy is filled in a moment later - the watch logs it again.");
        }
        catch { }
    }

    // RA01: twice a second while the Dev row is on: read the three objects and log which 4-byte values changed since the last look.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Watch()
    {
        byte[] lastInst = new byte[InstanceBytes], lastProxy = new byte[ProxyBytes], lastRender = new byte[RenderBytes];
        nint seenProxy = 0, seenRender = 0;
        bool have = false;
        long nextBeat = 0;
        while (true)
        {
            Thread.Sleep(500);
            try
            {
                nint inst = Volatile.Read(ref instance);
                if (!On || inst == 0) { have = false; continue; }
                nint proxy = 0, render = 0;
                if (!Read(inst + ProxyAt, &proxy, 8)) { Log.Write("RA01 room probe: the lab node's instance can't be read any more (unloaded) - walk back in"); Volatile.Write(ref instance, 0); continue; }
                if (proxy >= 0x10000) Read(proxy + RenderObjectAt, &render, 8);
                if (proxy != seenProxy || render != seenRender)
                {
                    Log.Write($"RA01 room probe: watching instance 0x{inst:X}, render proxy 0x{proxy:X} (flags byte at 0x{proxy + ProxyFlagsAt:X}), renderer's object 0x{render:X}");
                    seenProxy = proxy; seenRender = render; have = false;
                }
                byte[] i2 = Snapshot(inst, InstanceBytes), p2 = Snapshot(proxy, ProxyBytes), r2 = Snapshot(render, RenderBytes);
                long now = Environment.TickCount64;
                if (have)
                {
                    string changes = Diff("instance", lastInst, i2) + Diff("proxy", lastProxy, p2) + Diff("renderer", lastRender, r2);
                    if (changes.Length > 0) Log.Write($"RA01 room probe: changed -{changes}");
                }
                if (now >= nextBeat)
                {
                    nextBeat = now + 5000;
                    byte flags = p2[ProxyFlagsAt];
                    Log.Write($"RA01 room probe: proxy flags 0x{flags:X2} ({((flags & 6) == 6 ? "drawn" : "NOT drawn")} by the game's own flags), instance state 0x{BitConverter.ToUInt16(i2, 0x88):X4}, picture fov {CameraHook.Fov:F1}");
                }
                lastInst = i2; lastProxy = p2; lastRender = r2; have = true;
            }
            catch (Exception e) { Log.Write($"RA01 room probe: skipped a look ({e.Message})"); }
        }
    }

    // RA01: a copy of 'bytes' bytes at 'address' (all zero if it can't be read). Cost - CPU: extremely low (one safe read); GPU: none.
    static byte[] Snapshot(nint address, int bytes)
    {
        byte[] copy = new byte[bytes];
        if (address >= 0x10000) fixed (byte* p = copy) Read(address, p, bytes);
        return copy;
    }

    // RA01: " where +offset old->new" for each 4-byte value that changed (up to MaxChangesPerLine), as hex with the float beside it. Cost - CPU: extremely low; GPU: none.
    static string Diff(string where, byte[] before, byte[] after)
    {
        StringBuilder text = new();
        int shown = 0;
        for (int at = 0; at + 4 <= after.Length && shown < MaxChangesPerLine; at += 4)
        {
            uint a = BitConverter.ToUInt32(before, at), b = BitConverter.ToUInt32(after, at);
            if (a == b) continue;
            text.Append($" {where}+0x{at:X2} {a:X8}->{b:X8} ({BitConverter.UInt32BitsToSingle(b):G4})");
            shown++;
        }
        return text.ToString();
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game. Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
