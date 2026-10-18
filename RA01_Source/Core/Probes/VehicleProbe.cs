using System.Diagnostics;
using System.Text.RegularExpressions;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M19-0 - LOG ONLY, changes nothing. Where does V keep "I'm in a vehicle, in this seat"? (For physical steering: it must know when you drive, and whether
// it's a car - a wheel - or a motorcycle - handlebars.) Read-only, the same way her weapon slot was found (M14-5); no script is called.
//  1. NAMES: the game's own compiled scripts (r6\cache\final.redscripts, next to the game) hold every script name as plain text; each is turned into the
//     game's name code (FNV-1a 64) once at start, so changes are logged by NAME instead of a number.
//  2. V'S FIELDS: every named field of V (her class and its parents) is read every TickMs. A field that changes is logged with its name, old and new
//     value - and, if the new value points at an object, that object's class. A field that keeps changing (timers, counters) is called noisy after
//     MaxChanges changes and ignored from then on; "in a vehicle" should flip just once on getting in and once on getting out.
//  3. VEHICLE POINTERS: once a second, V's object and value holder are combed for any pointer to a vehicle (vehicleCarBaseObject, vehicleBikeBaseObject,
//     vehicleAVBaseObject, vehicleTankBaseObject - the RED4ext reference's classes), logged when one appears or goes away - in case the link isn't a named field.
// M20-0 - now the STATE PROBE (Dev row "State probe (test)"), for the next physical systems - grenades, knives, swimming, braindance mono:
//  - a few of V's fields are ALWAYS logged, never called noisy (Watched: her grenade state, locomotion state, scene tier, vehicle state, melee state);
//  - THE BRAINDANCE SWITCH: the game's BraindanceSystem (a script system, inside gameScriptableSystemsContainer - GameAnchor.ScriptableSystems) keeps
//    "isInBraindance" (set by the player's OnRewindableSectionEvent when a braindance starts and ends). The container is combed (two pointer steps) for an
//    object of class BraindanceSystem every 2 s until found; then its field is found by name and logged whenever it changes.
// Like marking every switch in a car's cab with tape, then sitting in the driver's seat and seeing which tape moved.
// Cost - CPU: low (on its own thread: a few hundred 8-byte safe reads 4 times a second, a comb of about 900 values once a second; a one-off scan of the
// scripts file, about 16 MB, at start); GPU: none.
internal static unsafe class VehicleProbe
{
    const int TickMs = 250, MaxChanges = 4, CombBytes = 0x1800, MaxFields = 3000;
    const int ClassAt = 0x30, ParentAt = 0x10, NameAt = 0x18, FieldsAt = 0x28, FieldNameAt = 0x08, FieldPlaceAt = 0x20, FieldFlagsAt = 0x28, ValueHolderAt = 0x38;
    const ulong InValueHolder = 1UL << 21;
    struct Field { public ulong Name; public int Place; public bool Holder; }
    static readonly Dictionary<ulong, string> names = new();
    static readonly Dictionary<ulong, string> vehicleClasses = new();
    static Field[] fields = Array.Empty<Field>();
    static long[] last = Array.Empty<long>();
    static int[] changes = Array.Empty<int>();
    static nint fieldsClass;
    static readonly HashSet<ulong> watched = new();                 // RA01: M20-0 - fields never called noisy
    static nint bdSystem;                                           // RA01: M20-0 - the BraindanceSystem object (0 = not found yet)
    static int bdPlace = -1;
    static bool bdInHolder;
    static long bdLast = -1, nextBdSearch;
    static int started;

    // RA01: starts the probe's thread. Call once at start-up.
    // Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(Loop) { IsBackground = true, Name = "RA01 vehicle probe" }.Start();
    }

    // RA01: the probe's loop (see the class comment). Never throws.
    // Cost - CPU: see the class comment; GPU: none.
    static void Loop()
    {
        LoadNames();
        foreach (string v in new[] { "vehicleBaseObject", "vehicleWheeledBaseObject", "vehicleCarBaseObject", "vehicleBikeBaseObject", "vehicleAVBaseObject", "vehicleTankBaseObject" })
            vehicleClasses[NameCode(v)] = v;
        foreach (string f in new[] { "combatGadgetState", "locomotionState", "sceneTier", "vehicleState", "meleeWeaponState" }) watched.Add(NameCode(f));
        var seen = new Dictionary<int, string>();
        long nextComb = 0;
        while (true)
        {
            Thread.Sleep(TickMs);
            try
            {
                nint v = GameAnchor.V;
                if (!Tuning.VehicleProbe || v == 0) continue;
                nint cls = Ptr(v + ClassAt);
                if (cls != fieldsClass) { MapFields(cls); ReadAll(v, last); continue; }
                Compare(v);
                long now = Environment.TickCount64;
                if (now >= nextComb) { nextComb = now + 1000; Comb(v, seen); }
                Braindance(now);
                StandInLook(v);                                     // RA01: M74 look
            }
            catch (Exception e) { Log.Write($"RA01 vehicle probe: {e.Message}"); Thread.Sleep(2000); }
        }
    }

    // RA01: every named field of V's class and its parents (up to 12), once per class.
    // Cost - CPU: low, once per class (a few safe reads per field); GPU: none.
    static void MapFields(nint cls)
    {
        var list = new List<Field>();
        int depth = 0;
        for (nint c = cls; c != 0 && depth < 12 && list.Count < MaxFields; c = Ptr(c + ParentAt), depth++)
        {
            nint table = Ptr(c + FieldsAt);
            uint count = 0;
            if (table == 0 || !Read(c + FieldsAt + 0xC, &count, 4) || count > 1024) continue;
            for (int i = 0; i < count; i++)
            {
                nint f = Ptr(table + i * 8);
                ulong name = 0, flags = 0;
                uint place = 0;
                if (f == 0 || !Read(f + FieldNameAt, &name, 8) || !Read(f + FieldPlaceAt, &place, 4) || !Read(f + FieldFlagsAt, &flags, 8) || place > 0x10000) continue;
                list.Add(new Field { Name = name, Place = (int)place, Holder = (flags & InValueHolder) != 0 });
            }
        }
        fields = list.ToArray();
        last = new long[fields.Length];
        changes = new int[fields.Length];
        fieldsClass = cls;
        int named = 0;
        foreach (Field f in fields) if (names.ContainsKey(f.Name)) named++;
        Log.Write($"RA01 vehicle probe: watching {fields.Length} of V's fields ({named} named from the game's scripts) - get into a car, drive a little, get out; then the same on a motorcycle");
    }

    // RA01: reads every mapped field (8 bytes from where it sits). Cost - CPU: low (one safe read per field); GPU: none.
    static void ReadAll(nint v, long[] into)
    {
        nint holder = Ptr(v + ValueHolderAt);
        for (int i = 0; i < fields.Length; i++)
        {
            nint from = fields[i].Holder ? holder : v;
            long value = 0;
            if (from != 0) Read(from + fields[i].Place, &value, 8);
            into[i] = value;
        }
    }

    // RA01: logs each field that changed (until it proves noisy), with its name and what a new pointer value points at.
    // Cost - CPU: low (as ReadAll, plus a log line per change); GPU: none.
    static void Compare(nint v)
    {
        var now = new long[fields.Length];
        ReadAll(v, now);
        for (int i = 0; i < fields.Length; i++)
        {
            if (now[i] == last[i]) continue;
            int n = watched.Contains(fields[i].Name) ? 1 : ++changes[i];
            if (n <= MaxChanges)
                Log.Write($"RA01 vehicle probe: V.{NameOf(fields[i].Name)} (+0x{fields[i].Place:X}{(fields[i].Holder ? " holder" : "")}) {last[i]:X} -> {now[i]:X}{Points(now[i])}");
            else if (n == MaxChanges + 1)
                Log.Write($"RA01 vehicle probe: V.{NameOf(fields[i].Name)} keeps changing - ignored from now on");
            last[i] = now[i];
        }
    }

    // RA01: once a second: every 8-byte value in V's object and value holder that points at a vehicle; logs ones that appear or go away.
    // Cost - CPU: low (about 900 values, each a cheap pointer check and, for real pointers, two class reads); GPU: none.
    static void Comb(nint v, Dictionary<int, string> seen)
    {
        var found = new Dictionary<int, string>();
        nint holder = Ptr(v + ValueHolderAt);
        foreach ((nint baseAt, int tag) in new[] { (v, 0), (holder, 0x100000) })
        {
            if (baseAt == 0) continue;
            for (int at = 0; at < CombBytes; at += 8)
            {
                nint p = Ptr(baseAt + at);
                if (p == 0) continue;
                string? kind = VehicleClassOf(p);
                if (kind != null) found[tag + at] = kind;
            }
        }
        foreach (var (key, kind) in found)
            if (!seen.ContainsKey(key)) Log.Write($"RA01 vehicle probe: V {(key >= 0x100000 ? "holder" : "object")} +0x{key & 0xFFFFF:X} now points at a {kind}");
        foreach (var key in seen.Keys)
            if (!found.ContainsKey(key)) Log.Write($"RA01 vehicle probe: V {(key >= 0x100000 ? "holder" : "object")} +0x{key & 0xFFFFF:X} no longer points at a vehicle");
        seen.Clear();
        foreach (var (key, kind) in found) seen[key] = kind;
    }

    // RA01: M20-0 - finds the BraindanceSystem (every 2 s until found) and logs its isInBraindance switch whenever it changes.
    // Cost - CPU: low while searching (up to about 64 x 128 pointer checks every 2 s), then extremely low (one or two safe reads a tick); GPU: none.
    static void Braindance(long now)
    {
        if (bdSystem == 0)
        {
            if (now < nextBdSearch) return;
            nextBdSearch = now + 2000;
            nint box = GameAnchor.ScriptableSystems;
            if (box == 0) return;
            ulong want = NameCode("BraindanceSystem");
            for (int a = 0; a < 0x200 && bdSystem == 0; a += 8)
            {
                nint p = Ptr(box + a);
                if (p == 0) continue;
                if (ClassNameOf(p) == want) { bdSystem = p; break; }
                for (int b = 0; b < 0x400 && bdSystem == 0; b += 8)
                {
                    nint q = Ptr(p + b);
                    if (q != 0 && ClassNameOf(q) == want) bdSystem = q;
                }
            }
            if (bdSystem == 0) return;
            FindField(bdSystem, NameCode("isInBraindance"), out bdPlace, out bdInHolder);
            Log.Write($"RA01 state probe: BraindanceSystem found - its isInBraindance switch {(bdPlace >= 0 ? $"at +0x{bdPlace:X}{(bdInHolder ? " (value holder)" : "")}" : "NOT found")}");
            return;
        }
        if (bdPlace < 0) return;
        nint from = bdInHolder ? Ptr(bdSystem + ValueHolderAt) : bdSystem;
        byte on = 0;
        if (from == 0 || !Read(from + bdPlace, &on, 1)) return;
        if (on == bdLast) return;
        bdLast = on;
        Log.Write($"RA01 state probe: IN A BRAINDANCE: {(on != 0 ? "yes" : "no")}");
    }

    // RA01: M74 LOOK (log only) - when the camera moves into a stand-in body (GameAnchor.StandInBody: the "Practice Makes Perfect" training avatar, Johnny's body,
    // a braindance's), every named field of that body is read once and compared with parked V's - the same class, so the same field list. The fields that
    // differ (with names, and what a pointer points at) are the body's fingerprint, so a later rule can pick out the training avatar alone. Once per body,
    // at most MaxLooks a session. Like reading two identical-looking crates' shipping labels side by side to find the line that tells them apart.
    // Cost - CPU: low, once per stand-in body (two reads per field, a few hundred); GPU: none.
    static void StandInLook(nint v)
    {
        nint body = GameAnchor.StandInBody;
        if (body == 0) { lookedAt = 0; return; }
        if (body == lookedAt) return;
        lookedAt = body;
        if (++looks > MaxLooks) return;
        if (Ptr(body + ClassAt) != fieldsClass) { Log.Write($"RA01 stand-in look (M74): body 0x{body:X} is a {NameOf(ClassNameOf(body))}, not V's class - fields not compared"); return; }
        var mine = new long[fields.Length];
        var hers = new long[fields.Length];
        ReadAll(body, mine);
        ReadAll(v, hers);
        var text = new System.Text.StringBuilder();
        int differ = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (mine[i] == hers[i]) continue;
            if (++differ > MaxLookFields) continue;
            text.Append($" | {NameOf(fields[i].Name)}={mine[i]:X}{Points(mine[i])} (V {hers[i]:X})");
        }
        Log.Write($"RA01 stand-in look (M74): body 0x{body:X} vs parked V 0x{v:X} - {differ} of {fields.Length} fields differ{(differ > MaxLookFields ? $" (first {MaxLookFields} listed)" : "")}:{text}");
    }
    const int MaxLooks = 8, MaxLookFields = 120;
    static nint lookedAt;
    static int looks;

    // RA01: M20-0 - an object's class name code (0 if unreadable). Cost - CPU: extremely low (two safe reads); GPU: none.
    static ulong ClassNameOf(nint obj)
    {
        nint cls = Ptr(obj + ClassAt);
        ulong name = 0;
        if (cls != 0) Read(cls + NameAt, &name, 8);
        return name;
    }

    // RA01: M20-0 - where a named field sits in an object's class or its parents (up to 12); place -1 if not found.
    // Cost - CPU: low (a few safe reads per field, once); GPU: none.
    static void FindField(nint obj, ulong want, out int place, out bool inHolder)
    {
        place = -1; inHolder = false;
        int depth = 0;
        for (nint c = Ptr(obj + ClassAt); c != 0 && depth < 12; c = Ptr(c + ParentAt), depth++)
        {
            nint table = Ptr(c + FieldsAt);
            uint count = 0;
            if (table == 0 || !Read(c + FieldsAt + 0xC, &count, 4) || count > 1024) continue;
            for (int i = 0; i < count; i++)
            {
                nint f = Ptr(table + i * 8);
                ulong name = 0, flags = 0;
                uint at = 0;
                if (f == 0 || !Read(f + FieldNameAt, &name, 8) || name != want || !Read(f + FieldPlaceAt, &at, 4) || !Read(f + FieldFlagsAt, &flags, 8)) continue;
                place = (int)at; inHolder = (flags & InValueHolder) != 0;
                return;
            }
        }
    }

    // RA01: " -> points at a <class>" for a value that is a pointer to a game object (and says if it's a vehicle), else "".
    // Cost - CPU: extremely low (up to 13 pairs of safe reads); GPU: none.
    static string Points(long value)
    {
        if (value < 0x10000 || value > 0x7FFFFFFFFFFF) return "";
        nint cls = Ptr((nint)value + ClassAt);
        ulong name = 0;
        if (cls == 0 || !Read(cls + NameAt, &name, 8)) return "";
        string? vehicle = VehicleClassOf((nint)value);
        return $" - points at a {NameOf(name)}{(vehicle != null ? $" (A VEHICLE: {vehicle})" : "")}";
    }

    // RA01: the vehicle class an object is (its own class or a parent, up to 12), or null if it isn't a vehicle.
    // Cost - CPU: extremely low (up to 13 pairs of safe reads); GPU: none.
    static string? VehicleClassOf(nint obj)
    {
        nint cls = Ptr(obj + ClassAt);
        string? first = null;
        for (int depth = 0; cls != 0 && depth < 12; depth++, cls = Ptr(cls + ParentAt))
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return null;
            if (vehicleClasses.TryGetValue(name, out string? v)) { first ??= v; if (v != "vehicleBaseObject" && v != "vehicleWheeledBaseObject") return v; }
        }
        return first;
    }

    // RA01: a name code as its name (from the scripts), or as hex if unknown. Cost - CPU: extremely low (a lookup); GPU: none.
    static string NameOf(ulong code) => names.TryGetValue(code, out string? n) ? n : $"0x{code:X16}";

    // RA01: reads the game's compiled scripts once and keeps every identifier's name code. A missing file only means hex names in the log.
    // Cost - CPU: medium, once at start on this thread (a regex over about 16 MB, well under a second); GPU: none.
    static void LoadNames()
    {
        try
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe) ?? "", "..", "..", "r6", "cache", "final.redscripts"));
            if (!File.Exists(file)) { Log.Write($"RA01 vehicle probe: {file} not found - names will show as codes"); return; }
            string text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
            foreach (Match m in Regex.Matches(text, "[A-Za-z_][A-Za-z0-9_]{2,80}"))
                names.TryAdd(NameCode(m.Value), m.Value);
            Log.Write($"RA01 vehicle probe: {names.Count} names read from the game's scripts");
        }
        catch (Exception e) { Log.Write($"RA01 vehicle probe: the game's scripts could not be read ({e.Message}) - names will show as codes"); }
    }

    // RA01: the game's name code: FNV-1a 64-bit over the name's bytes. Cost - CPU: extremely low; GPU: none.
    static ulong NameCode(string name)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (char ch in name) { h ^= (byte)ch; h *= 0x100000001B3; }
        return h;
    }

    // RA01: a pointer stored at 'at', 0 if unreadable or not plausibly a pointer. Cost - CPU: extremely low (one 8-byte safe read); GPU: none.
    static nint Ptr(nint at)
    {
        nint value = 0;
        if (at < 0x10000 || !Read(at, &value, 8)) return 0;
        return value > 0x10000 && value < 0x7FFFFFFFFFFF ? value : 0;
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game. Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got = 0;
        return ReadProcessMemory(GetCurrentProcess(), address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
