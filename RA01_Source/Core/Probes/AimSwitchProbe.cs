using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M17-0 - LOG ONLY, changes nothing. What tells V's GUN that it is being aimed - the switch that opens its scope or lights its reticle - apart from the
// camera's zoom? (The plan: shouldering the gun flips just that switch, no LT, no zoom.) Like chalking every lever in a cab, pulling the aim handle
// once, and seeing which chalk marks moved.
// M17-4 - each part is now read up to 0x280 bytes: a skinned mesh part's chunk mask (+0x248, which pieces of its mesh show) and its two animation-driven
// settings (+0x238 rendering plane, +0x240 visibility - RED4ext reference, entSkinnedMeshComponent) are in reach, to find what lights a sight's aiming dot.
// Every TickMs while V holds a gun:
//  - not aiming (no left trigger reaching the game): a REST snapshot every RestMs of (a) every named field of V's gun (its class and parents' field lists,
//    the same lists GameAnchor finds V's weapon slot in), (b) every named field of V herself, (c) the first ComponentBytes of each of the gun's components.
//    Anything that differs between two rest snapshots is marked NOISY (timers, counters) and never reported.
//  - aiming: snapshots EarlyMs and LateMs after the left trigger went down, each compared with the last rest one; every non-noisy change is logged as
//    "gun field 0x<name code> +0x<place>: rest -> aimed" (name codes are the game's FNV-1a names: they are matched to names from the RED4ext reference later).
// A field that flips once and stays is a switch we can flip ourselves; the next build (M17-1) tests whether the game keeps putting it back.
// Cost - CPU: low while a gun is out (a snapshot of up to a few thousand 8-byte safe reads twice a second at rest, two more per aim, compares and a few log
// lines per aim, on its own thread); extremely low otherwise (one slot read every TickMs); GPU: none.
internal static unsafe class AimSwitchProbe
{
    const int TickMs = 50, RestMs = 500, EarlyMs = 200, LateMs = 1200, ComponentBytes = 0x280, MaxFields = 2048, MaxComponents = 64, MaxListed = 80;
    const int ClassAt = 0x30, ParentAt = 0x10, NameAt = 0x18, FieldsAt = 0x28, FieldNameAt = 0x08, FieldPlaceAt = 0x20, FieldFlagsAt = 0x28, ValueHolderAt = 0x38;
    const int ComponentsAt = 0xA0;
    const ulong InValueHolder = 1UL << 21;
    static readonly HashSet<string> noisy = new();
    static Dictionary<string, long>? rest;
    static nint restWeapon;
    static int started;

    // RA01: starts the probe's thread. Call once at start-up.
    // Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(Loop) { IsBackground = true, Name = "RA01 aim switch probe" }.Start();
    }

    // RA01: the probe's loop (see the class comment). Never throws.
    // Cost - CPU: see the class comment; GPU: none.
    static void Loop()
    {
        long nextRest = 0, aimSince = 0;
        bool earlyDone = false, lateDone = false;
        while (true)
        {
            Thread.Sleep(TickMs);
            try
            {
                if (!Tuning.AimSwitchProbe || GameAnchor.V == 0 || !GameAnchor.TryRightHandWeapon(out nint weapon) || weapon == 0
                    || !WeaponEventProbe.KindOf(weapon, out WeaponProbe.Kind kind, out string gunName) || kind != WeaponProbe.Kind.Gun) // RA01: the slot's own kind (WeaponProbe.Held belongs to the melee thread)
                { aimSince = 0; continue; }
                long now = Environment.TickCount64;
                if (weapon != restWeapon) { restWeapon = weapon; rest = null; noisy.Clear(); }
                if (!ControllerMapping.LeftTriggerSent)
                {
                    if (aimSince != 0) { aimSince = 0; nextRest = now + RestMs; } // RA01: let the gun settle back before the next rest snapshot
                    if (now < nextRest) continue;
                    nextRest = now + RestMs;
                    var fresh = Snapshot(weapon);
                    if (rest != null) foreach (var (key, value) in fresh) if (rest.TryGetValue(key, out long old) && old != value) noisy.Add(key);
                    if (rest == null) Log.Write($"RA01 aim switch probe: watching {gunName} - {fresh.Count} values (gun fields, V's fields, gun components); press and release LT a few times");
                    rest = fresh;
                    continue;
                }
                if (rest == null) continue;
                if (aimSince == 0) { aimSince = now; earlyDone = lateDone = false; }
                if (!earlyDone && now - aimSince >= EarlyMs) { earlyDone = true; Report("0.2 s into the aim", Snapshot(weapon)); }
                if (!lateDone && now - aimSince >= LateMs) { lateDone = true; Report("1.2 s into the aim", Snapshot(weapon)); }
            }
            catch (Exception e) { Log.Write($"RA01 aim switch probe: {e.Message}"); Thread.Sleep(1000); }
        }
    }

    // RA01: one snapshot: every named field of the gun ("gun ...") and of V ("V ..."), and each gun component's first bytes ("gun part N (class) ...").
    // Cost - CPU: low (up to a few thousand 8-byte safe reads); GPU: none.
    static Dictionary<string, long> Snapshot(nint weapon)
    {
        var into = new Dictionary<string, long>();
        Fields("gun", weapon, into);
        Fields("V", GameAnchor.V, into);
        nint list = Ptr(weapon + ComponentsAt);
        uint count = 0;
        if (list != 0 && Read(weapon + ComponentsAt + 0xC, &count, 4) && count <= MaxComponents)
            for (int i = 0; i < count; i++)
            {
                nint part = Ptr(list + i * 16);
                if (part == 0) continue;
                ulong cls = 0;
                Read(Ptr(part + ClassAt) + NameAt, &cls, 8);
                for (int at = 0; at < ComponentBytes; at += 8)
                {
                    long v = 0;
                    if (Read(part + at, &v, 8)) into[$"gun part {i} (class 0x{cls:X16}) +0x{at:X3}"] = v;
                }
            }
        return into;
    }

    // RA01: every named field of an object: its class and up to 12 parents, each field read as 8 bytes from where it sits (the object or its value holder).
    // Cost - CPU: low (a few safe reads per field, up to MaxFields fields); GPU: none.
    static void Fields(string who, nint obj, Dictionary<string, long> into)
    {
        if (obj == 0) return;
        nint holder = Ptr(obj + ValueHolderAt);
        int total = 0, depth = 0;
        for (nint c = Ptr(obj + ClassAt); c != 0 && depth < 12 && total < MaxFields; c = Ptr(c + ParentAt), depth++)
        {
            nint list = Ptr(c + FieldsAt);
            uint count = 0;
            if (list == 0 || !Read(c + FieldsAt + 0xC, &count, 4) || count > 1024) continue;
            for (int i = 0; i < count && total < MaxFields; i++, total++)
            {
                nint field = Ptr(list + i * 8);
                ulong name = 0, flags = 0;
                uint place = 0;
                if (field == 0 || !Read(field + FieldNameAt, &name, 8) || !Read(field + FieldPlaceAt, &place, 4) || !Read(field + FieldFlagsAt, &flags, 8) || place > 0x10000) continue;
                bool inHolder = (flags & InValueHolder) != 0;
                nint from = inHolder ? holder : obj;
                long v = 0;
                if (from != 0 && Read(from + (nint)place, &v, 8)) into[$"{who} field 0x{name:X16} +0x{place:X}{(inHolder ? " (holder)" : "")}"] = v;
            }
        }
    }

    // RA01: logs every value that moved from the rest snapshot (noisy ones skipped): as hex, and its first 4 bytes as a decimal number.
    // Cost - CPU: extremely low (a few thousand dictionary lookups and up to MaxListed log lines, twice per aim); GPU: none.
    static void Report(string when, Dictionary<string, long> aimed)
    {
        int listed = 0;
        foreach (var (key, value) in aimed)
        {
            if (noisy.Contains(key) || !rest!.TryGetValue(key, out long old) || old == value) continue;
            if (++listed > MaxListed) break;
            Log.Write($"RA01 aim switch probe ({when}): {key}: {old:X16} ({AsFloat(old)}) -> {value:X16} ({AsFloat(value)})");
        }
        Log.Write($"RA01 aim switch probe ({when}): {Math.Min(listed, MaxListed)}{(listed > MaxListed ? "+" : "")} value(s) moved ({noisy.Count} noisy ones ignored)");
    }

    // RA01: the low 4 bytes as a decimal number for the log, or "-" when they don't look like one.
    // Cost - CPU: extremely low (a reinterpret and a format, per logged value); GPU: none.
    static string AsFloat(long v) { int low = (int)v; float f = *(float*)&low; return float.IsFinite(f) && MathF.Abs(f) < 1e7f ? f.ToString("G4") : "-"; }

    // RA01: a pointer stored at 'at', 0 if unreadable or not plausibly a pointer.
    // Cost - CPU: extremely low (one 8-byte safe read); GPU: none.
    static nint Ptr(nint at)
    {
        nint value = 0;
        if (at < 0x10000 || !Read(at, &value, 8)) return 0;
        return value > 0x10000 && value < 0x7FFFFFFFFFFF ? value : 0;
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got = 0;
        return ReadProcessMemory(GetCurrentProcess(), address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
