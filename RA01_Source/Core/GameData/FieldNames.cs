using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M44 - FIELDS BY THEIR LABEL, not by a tape measure. Every game object points at its class (object +0x30), and every class keeps a list of its named
// fields (the game's own catalogue, RTTI): each entry has the field's name code (+0x08, the same FNV-1a 64 fingerprint GameAnchor.NameCode makes) and
// where it sits in the object (+0x20). So "where is inkWidget.renderTransformPivot on this object?" is answered by the game's own records, and a game
// update that moves the field moves the answer with it. Only data is read - no game function is called - so this works even when the game-call
// addresses are lost. Answers are kept per field (the declaring class lays the field out the same for every object built on it).
// The catalogue's own shape (class +0x10 parent, +0x18 name, +0x28 / +0x118 field lists; field +0x00 type, +0x08 name, +0x20 place, +0x28 flags) is the
// engine's reflection system itself - RED4ext relies on the same numbers - so it is the one fixed layout everything else is now measured from.
// Rule (At): the label found = use it (MOVED in the Rehooker if it isn't the 2.31 place). Not found = on 2.31 the old number (nothing changes); on any
// other build the feature is off (-1), never a guess.
// Probe (Dev row "Field name probe (log)"): for 30 s, logs which named field - if any - lives at each fixed place RA01 still uses (M44-2's input).
// Builder picture: the brackets are fitted by the label on them ("PIVOT"), not by "40 cm from the left wall".
// Cost - CPU: extremely low (a field is looked up once - a walk of a few short lists - then it is one dictionary read); GPU: none.
internal static unsafe class FieldNames
{
    const int ClassAt = 0x30, ParentAt = 0x10, NameAt = 0x18, OwnFields = 0x28, AllFields = 0x118, CountAt = 0xC;
    const int FieldTypeAt = 0x00, FieldNameAt = 0x08, FieldPlaceAt = 0x20, FieldFlagsAt = 0x28, MaxFields = 4096, MaxDepth = 16;
    const ulong InValueHolder = 1UL << 21;   // RA01: a script field kept in the object's value holder, not in the object itself (as GameAnchor) - not what these look-ups want
    static readonly Dictionary<(ulong, ulong, ulong), int> known = new();
    static readonly bool[] reported = new bool[HookRegistry.Rows.Length];
    static readonly object gate = new();
    static readonly nint Self = GetCurrentProcess();

    // RA01: where 'owner'.'field' (and, if 'part' is given, that field's own sub-field, e.g. renderTransform.scale) sits on 'obj', by the rule in the class comment.
    // Reports to the Rehooker row 'row' once. -1 = not usable (no object yet, or not found on a new build).
    // Cost - CPU: extremely low after the first call for a field (one dictionary read under a short lock); GPU: none.
    public static int At(nint obj, int row, string owner, string field, string? part, int place231)
    {
        if (obj < 0x10000) return -1;
        ulong ownerCode = GameAnchor.NameCode(owner), fieldCode = GameAnchor.NameCode(field), partCode = part == null ? 0 : GameAnchor.NameCode(part);
        int at;
        lock (gate)
        {
            if (!known.TryGetValue((ownerCode, fieldCode, partCode), out at))
            {
                at = Find(obj, ownerCode, fieldCode, partCode);
                if (at >= 0) known[(ownerCode, fieldCode, partCode)] = at;   // RA01: a miss is asked again next time (the object may not be the right kind yet)
            }
        }
        string label = $"{owner}.{field}{(part != null ? "." + part : "")}";
        if (at >= 0)
        {
            Report(row, () => { HookRegistry.Found(row, ExeScan.Base + at, "by its name"); if (at != place231) Log.Write($"RA01 field names: {label} is at +0x{at:X} (2.31: +0x{place231:X}) - the name wins"); });
            return at;
        }
        if (!IsA(obj, ownerCode)) return -1;   // RA01: not that kind of object - nothing to say yet
        if (HookRegistry.SameBuild) { Report(row, () => { HookRegistry.Found(row, ExeScan.Base + place231, "2.31 place (its name wasn't in the catalogue)"); Log.Write($"RA01 field names: {label} not found by name - the 2.31 place +0x{place231:X} is used"); }); return place231; }
        Report(row, () => { HookRegistry.Off(row, "its name isn't in this build's catalogue"); Log.Write($"RA01 field names: {label} not found by name on this game build - that feature is off"); });
        return -1;
    }

    // RA01: M44-2 - the same rule, for a field whose OWNER differs between objects (the crosshair's two switches live on more than one kind of HUD controller):
    // 'field' is looked for in EVERY class of obj's chain, nearest first. The caller keeps the answer per class (this walks the lists every call).
    // Cost - CPU: low per call (up to 16 classes' field lists of small safe reads) - call it once per class, not per frame; GPU: none.
    public static int AtAny(nint obj, int row, string field, int place231)
    {
        if (obj < 0x10000) return -1;
        ulong code = GameAnchor.NameCode(field);
        int at = -1;
        for (nint cls = Ptr(obj + ClassAt), depth = 0; cls != 0 && depth < MaxDepth && at < 0; cls = Ptr(cls + ParentAt), depth++)
        {
            nint entry = FieldOf(cls, code);
            int place = 0;
            if (entry != 0 && Read(entry + FieldPlaceAt, &place, 4) && place >= 0 && place <= 0x10000) at = place;
        }
        string cls0 = $"0x{Ptr(Ptr(obj + ClassAt) + NameAt):X16}";
        if (at >= 0)
        {
            int found = at;
            Report(row, () => { HookRegistry.Found(row, ExeScan.Base + found, "by its name"); if (found != place231) Log.Write($"RA01 field names: {field} is at +0x{found:X} on class {cls0} (2.31: +0x{place231:X}) - the name wins"); });
            return at;
        }
        if (HookRegistry.SameBuild) { Report(row, () => { HookRegistry.Found(row, ExeScan.Base + place231, "2.31 place (its name wasn't in the catalogue)"); Log.Write($"RA01 field names: {field} not found by name on class {cls0} - the 2.31 place +0x{place231:X} is used"); }); return place231; }
        Report(row, () => { HookRegistry.Off(row, "its name isn't in this build's catalogue"); Log.Write($"RA01 field names: {field} not found by name on class {cls0} on this game build - not written there"); });
        return -1;
    }

    // RA01: runs 'say' the first time a row reports. Cost - CPU: extremely low; GPU: none.
    static void Report(int row, Action say)
    {
        lock (gate) { if (reported[row]) return; reported[row] = true; }
        say();
    }

    // RA01: walks obj's class and its parents to 'owner', finds 'field' in its lists, and (with 'part') 'part' inside that field's own type. -1 = not found.
    // Cost - CPU: low, once per field (up to 16 classes, a few hundred list entries, each a couple of 8-byte safe reads); GPU: none.
    static int Find(nint obj, ulong owner, ulong field, ulong part)
    {
        for (nint cls = Ptr(obj + ClassAt), depth = 0; cls != 0 && depth < MaxDepth; cls = Ptr(cls + ParentAt), depth++)
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return -1;
            if (name != owner) continue;
            nint entry = FieldOf(cls, field);
            int at = 0;
            if (entry == 0 || !Read(entry + FieldPlaceAt, &at, 4) || at < 0 || at > 0x10000) return -1;
            if (part == 0) return at;
            nint inner = FieldOf(Ptr(entry + FieldTypeAt), part);   // RA01: the field's type is itself a class (a struct) with its own list
            int innerAt = 0;
            return inner != 0 && Read(inner + FieldPlaceAt, &innerAt, 4) && innerAt >= 0 && innerAt < 0x1000 ? at + innerAt : -1;
        }
        return -1;
    }

    // RA01: the field entry named 'code' in class 'cls' itself (its own list, then its fuller list); 0 if none or kept in the value holder.
    // Cost - CPU: low (up to two short list walks of small safe reads); GPU: none.
    static nint FieldOf(nint cls, ulong code)
    {
        if (cls == 0) return 0;
        foreach (int listAt in new[] { OwnFields, AllFields })
        {
            nint list = Ptr(cls + listAt);
            uint count = 0;
            if (list == 0 || !Read(cls + listAt + CountAt, &count, 4) || count > MaxFields) continue;
            for (int i = 0; i < count; i++)
            {
                nint entry = Ptr(list + i * 8);
                ulong name = 0, flags = 0;
                if (entry == 0 || !Read(entry + FieldNameAt, &name, 8) || name != code) continue;
                if (Read(entry + FieldFlagsAt, &flags, 8) && (flags & InValueHolder) != 0) return 0;
                return entry;
            }
        }
        return 0;
    }

    // RA01: is obj of class 'code', or built on it? Cost - CPU: extremely low (up to 16 pairs of safe reads); GPU: none.
    static bool IsA(nint obj, ulong code)
    {
        for (nint cls = Ptr(obj + ClassAt), depth = 0; cls != 0 && depth < MaxDepth; cls = Ptr(cls + ParentAt), depth++)
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return false;
            if (name == code) return true;
        }
        return false;
    }

    // RA01: ---- M44 probe (Dev row "Field name probe (log)"): which named field, if any, sits at each fixed place RA01 still uses ----
    // RA01: the objects it looks at (each read live, when it exists) and the places RA01 uses on them.
    static readonly (string What, Func<nint> Get, int[] Places)[] Targets =
    {
        ("crosshair manager", () => CrosshairProbe.Manager, new[] { 0x50, 0x168, 0x269, 0x278, 0x280, 0x283 }),
        ("V", () => GameAnchor.V, new[] { 0xA0, 0x156, 0x1C8 }),
        ("player system", () => GameAnchor.PlayerSystem, new[] { 0x88 }),
        ("UI system", () => GameAnchor.UiSystem, new[] { 0x98, 0xAC }),
        ("V's right-hand weapon", () => GameAnchor.TryRightHandWeapon(out nint w) ? w : 0, new[] { 0x200 }),
        ("grenade projectile part", () => ThrowLever.LastPart, new[] { 0x120, 0x130, 0x7AA, 0x878 }),
    };
    static int probing;

    // RA01: starts the probe (one background thread, 30 s; each object is logged once, as soon as it exists - so draw a gun, hold a grenade, during it).
    // Cost - CPU: low, only while it runs (once a second, a few thousand small safe reads per new object, logged once); GPU: none.
    public static void Probe()
    {
        if (Interlocked.Exchange(ref probing, 1) == 1) return;
        Log.Write("RA01 field names probe: running for 30 s - draw a gun and hold a grenade meanwhile, so every object exists once");
        new Thread(() =>
        {
            bool[] done = new bool[Targets.Length];
            try
            {
                for (int tick = 0; tick < 30; tick++)
                {
                    for (int t = 0; t < Targets.Length; t++)
                    {
                        if (done[t]) continue;
                        nint obj = Targets[t].Get();
                        if (obj < 0x10000) continue;
                        done[t] = true;
                        LogObject(Targets[t].What, obj, Targets[t].Places);
                    }
                    Thread.Sleep(1000);
                }
                for (int t = 0; t < Targets.Length; t++) if (!done[t]) Log.Write($"RA01 field names probe: {Targets[t].What} - didn't exist during the probe");
            }
            catch (Exception e) { Log.Write($"RA01 field names probe stopped: {e.Message}"); }
            Log.Write("RA01 field names probe: done");
            Volatile.Write(ref probing, 0);
        }) { IsBackground = true, Name = "RA01 field names probe" }.Start();
    }

    // RA01: one object: its class chain, then for each place the named field there ("= name"), the one it falls inside ("inside name +n"), or none (engine-only).
    // Cost - CPU: low, once per object (all fields of up to 16 classes); GPU: none.
    static void LogObject(string what, nint obj, int[] places)
    {
        List<(int At, string Name, string Owner)> fields = new();
        string chain = "";
        for (nint cls = Ptr(obj + ClassAt), depth = 0; cls != 0 && depth < MaxDepth; cls = Ptr(cls + ParentAt), depth++)
        {
            ulong code = 0;
            Read(cls + NameAt, &code, 8);
            string owner = Text(code);
            chain += (chain.Length == 0 ? "" : " < ") + owner;
            foreach (int listAt in new[] { OwnFields, AllFields })
            {
                nint list = Ptr(cls + listAt);
                uint count = 0;
                if (list == 0 || !Read(cls + listAt + CountAt, &count, 4) || count > MaxFields) continue;
                for (int i = 0; i < count; i++)
                {
                    nint entry = Ptr(list + i * 8);
                    ulong name = 0, flags = 0; int at = 0;
                    if (entry == 0 || !Read(entry + FieldNameAt, &name, 8) || !Read(entry + FieldPlaceAt, &at, 4) || !Read(entry + FieldFlagsAt, &flags, 8)) continue;
                    int place = at;   // RA01: (a copy - 'at' itself was read through its address)
                    if ((flags & InValueHolder) != 0 || fields.Exists(f => f.At == place && f.Owner == owner)) continue;
                    fields.Add((place, Text(name), owner));
                }
            }
        }
        fields.Sort((a, b) => a.At.CompareTo(b.At));
        Log.Write($"RA01 field names probe: {what} 0x{obj:X} - classes {chain}; {fields.Count} named fields in the object");
        foreach (int place in places)
        {
            int exact = fields.FindIndex(f => f.At == place);
            int below = fields.FindLastIndex(f => f.At < place);
            string answer = exact >= 0 ? $"= {fields[exact].Owner}.{fields[exact].Name}"
                : below >= 0 ? $"no field starts here; {place - fields[below].At} bytes into (or past) {fields[below].Owner}.{fields[below].Name} (which starts at +0x{fields[below].At:X})"
                : "no named field (engine-only)";
            Log.Write($"RA01 field names probe:   +0x{place:X} {answer}");
        }
    }

    // RA01: a name code as text (the game's name pool, when its address is checked), else the code in hex. Cost - CPU: extremely low; GPU: none.
    static string Text(ulong code) => GameCall.NameText(code) is { Length: > 0 } text ? text : $"#{code:X16}";

    // RA01: Cost - CPU: extremely low (one safe read); GPU: none.
    static nint Ptr(nint at) { nint v = 0; return Read(at, &v, 8) ? v : 0; }

    // RA01: a safe read: a bad address fails, never the game. Cost - CPU: extremely low (one ReadProcessMemory); GPU: none.
    static bool Read(nint at, void* into, int bytes)
    {
        nuint got = 0;
        return at >= 0x10000 && ReadProcessMemory(Self, at, into, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
