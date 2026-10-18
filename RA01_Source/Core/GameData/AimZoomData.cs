using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M35 - NO AIM ZOOM for every gun and every scope, done in the game's own database (TweakDB) so TweakXL and "Aim Zoom Disabled" are no longer needed.
// WHY: the game zooms the whole picture (and drags the gun) when you aim, and that zoom is what made LT shots miss in VR. Three stats drive it, all set
// by small "stat modifier" cards: BaseStats.ZoomLevel (the zoom), BaseStats.ScopeFOV (a scope's view) and BaseStats.AimFOV (each gun's aim view).
// Rob's tests: with those cards' numbers at 0 there is no zoom and LT aims true; the scope's own magnified picture inside the lens is untouched.
// HOW (like a warehouse): each card has an ARROW to a shelf that holds its number, and the game keeps each number on ONE shelf shared by every card
// that uses it - so the shelf holding 26 is never repainted (other stats use it too). Instead each zoom card's arrow is turned to the shelf that
// already holds 0.0. Each finished record also keeps its own copy of that arrow (its "note"), so the note is turned too - only on an exact 8-byte
// match (the card's full ID), never a guess; no match = that card is left alone and logged.
// WHICH CARDS: found by what they DO, not by name: every card whose stat is one of the three - so modded guns (added by TweakXL before the main
// menu) are covered too. Runs once the database has stopped growing (TweakXL finished). Weapons tab "No aim zoom" puts every arrow back and forth.
// Safe: memory reads and 8-byte writes only, each write checked against the value it expects first; no game function called, nothing hooked.
// M35-1 - zoom-level cards only where a gun or scope owns them, so Kiroshi zoom works with or without a gun (see GunOrScopeZooms).
// M35-3 - while it waits and scans, the banner says "PLEASE WAIT - LOADING GUNS", then "GUNS READY" (Rob: so no save is loaded too soon).
// Cost - CPU: low (one background scan of about 1.5 million cards at the main menu, one to two seconds; afterwards one settings check every half second); GPU: none.
internal static unsafe class AimZoomData
{
    const int ValueEndAt = 0x158, RecordsAt = 0x58, RecordCapacityAt = 0x64, RecordNodesAt = 0x68, RecordStrideAt = 0x74, NoteScanFrom = 0x48, NoteScanBytes = 0x100;
    const ulong Key = 0xFFFFFFFFFF;                                     // RA01: the low 5 bytes of a card: its name fingerprint and length (the top 3 = its arrow)
    static readonly nint Self = GetCurrentProcess();
    static readonly List<(nint at, ulong original, ulong changed)> edits = new(); // RA01: every arrow and note turned, with both positions
    static bool applied;

    // RA01: Call once at start-up, after WeaponEventProbe (ExeScan loaded). Starts the background worker.
    // Cost - CPU: extremely low (starts one thread); GPU: none.
    public static void Install() => new Thread(Worker) { IsBackground = true, Name = "RA01 AimZoomData" }.Start();

    // RA01: M52 - ROB'S SIGNATURE (2026-10-12): his own voice says "guns ready" with the GUNS READY banner. Sounds\RA01_GunsReady.wav beside RA01.dll (build.ps1
    // copies it there from assets\audio_guns_ready.wav); played in the background on Windows' default sound output (the headset with Link / Virtual Desktop).
    // No file = silence, exactly as before. Never throws.
    // Cost - CPU: extremely low (one existence check and one Windows call, once per game start); GPU: none.
    static void GunsReadyVoice()
    {
        try
        {
            string file = Path.Combine(Path.GetDirectoryName(ModFolders.Settings) ?? "", "Sounds", "RA01_GunsReady.wav");
            if (!File.Exists(file)) { Log.Write($"RA01 guns ready voice: {file} not found - no voice"); return; }
            bool played = Win32.PlaySoundW(file, 0, Win32.SndFilename | Win32.SndAsync | Win32.SndNoDefault);
            Log.Write($"RA01 guns ready voice: {(played ? "playing" : "Windows could not play")} {file} (M52)");
        }
        catch { }
    }

    // RA01: waits for the database to finish loading, scans once, then follows the "No aim zoom" setting.
    // Cost - CPU: low (one scan, then a bool check every half second); GPU: none.
    static void Worker()
    {
        try
        {
            if (Tuning.NoAimZoom) Banner.Show("PLEASE WAIT - LOADING GUNS", 600000); // RA01: M35-3 - on screen until the scan is done, so no save is loaded too soon
            if (!WaitForDatabase(out nint tdb)) { Log.Write("RA01 M35 no aim zoom: game database not found - left as the game has it"); HookRegistry.Off(HookRegistry.TweakDb, "no database there after 5 minutes"); if (Tuning.NoAimZoom) Banner.Show("GUNS NOT LOADED - AIM ZOOM STAYS", 5000); return; }
            Scan(tdb);
            if (Tuning.NoAimZoom) { if (edits.Count > 0) Turn(true); Banner.Show(edits.Count > 0 ? "GUNS READY" : "GUNS NOT LOADED - AIM ZOOM STAYS", 4000); if (edits.Count > 0) GunsReadyVoice(); }
            while (true)
            {
                if (Tuning.NoAimZoom != applied && edits.Count > 0) Turn(Tuning.NoAimZoom);
                Thread.Sleep(500);
            }
        }
        catch (Exception e) { Log.Write("RA01 M35 no aim zoom: stopped - " + e.Message); }
    }

    // RA01: the database is ready when it has cards and records and its card count has not changed for 3 checks in a row (TweakXL has added its cards).
    // Cost - CPU: extremely low (three reads a second, for at most 5 minutes); GPU: none.
    static bool WaitForDatabase(out nint tdb)
    {
        tdb = 0;
        uint last = 0; int steady = 0;
        for (int i = 0; i < 300; i++)
        {
            Thread.Sleep(1000);
            uint count = 0, records = 0; nint db = 0;
            if (WeaponEventProbe.TdbAt == 0 || !Read(ExeScan.Base + WeaponEventProbe.TdbAt, &db, 8) || (tdb = db) == 0 || !Read(tdb + WeaponEventProbe.FlatCountAt, &count, 4) || !Read(tdb + RecordsAt + 8, &records, 4)) continue;
            steady = count > 0 && records > 0 && count == last ? steady + 1 : 0;
            last = count;
            if (steady >= 3) return true;
        }
        return false;
    }

    // RA01: the one scan: copies the card list and the shelves, finds every zoom card, and lines up its arrow (and its record's note) for the 0.0 shelf.
    // Cost - CPU: low (two big copies, one pass over the cards, a few hundred small look-ups; once); GPU: none.
    static void Scan(nint tdb)
    {
        nint shelves = 0, cards = 0, shelvesEnd = 0; uint count = 0;
        if (!Read(tdb, &shelves, 8) || !Read(tdb + WeaponEventProbe.FlatsAt, &cards, 8) || !Read(tdb + WeaponEventProbe.FlatCountAt, &count, 4) || !Read(tdb + ValueEndAt, &shelvesEnd, 8)) return;
        long size = shelvesEnd - shelves;
        if (count == 0 || size <= 0 || size > 512L << 20) { Log.Write($"RA01 M35 no aim zoom: database looks wrong (cards {count}, shelves {size} bytes) - left alone"); return; }
        var flat = new ulong[count];
        var shelf = new byte[size];
        fixed (ulong* f = flat) if (!Read(cards, f, (int)count * 8)) return;
        fixed (byte* s = shelf) if (!Read(shelves, s, (int)size)) return;

        // RA01: the two shelf kinds, learned from one known card (Items.Base_Short_Scope_inline0): each shelf starts with an 8-byte kind mark
        ulong scopeCard = WeaponEventProbe.TweakDbId("Items.Base_Short_Scope_inline0");
        int idShelf = ShelfOf(flat, WeaponEventProbe.Extend(scopeCard, ".statType")), floatShelf = ShelfOf(flat, WeaponEventProbe.Extend(scopeCard, ".value"));
        if (idShelf < 0 || floatShelf < 0) { Log.Write("RA01 M35 no aim zoom: reference card not found - left alone"); return; }
        ulong idKind = U64(shelf, idShelf), floatKind = U64(shelf, floatShelf);

        ulong zoom = WeaponEventProbe.TweakDbId("BaseStats.ZoomLevel") & Key, scope = WeaponEventProbe.TweakDbId("BaseStats.ScopeFOV") & Key, aim = WeaponEventProbe.TweakDbId("BaseStats.AimFOV") & Key;
        int zeroShelf = -1, zooms = 0, scopes = 0, aims = 0, notes = 0, missed = 0;
        var found = new List<(int card, ulong record)>();
        var zoomRecords = new HashSet<ulong>();
        for (int i = 0; i < flat.Length; i++)
        {
            int at = Arrow(flat[i]);
            if (at < 0 || at + 16 > shelf.Length) continue;
            ulong kind = U64(shelf, at);
            if (kind == floatKind) { if (zeroShelf < 0 && BitConverter.ToSingle(shelf, at + 8) == 0f) zeroShelf = at; continue; }
            if (kind != idKind) continue;
            ulong stat = U64(shelf, at + 8) & Key;
            if (stat != zoom && stat != scope && stat != aim) continue;
            ulong record = Unextend(flat[i] & Key, ".statType");
            if (ShelfOf(flat, WeaponEventProbe.Extend(record, ".modifierType")) < 0) continue; // RA01: not a stat modifier card - skip
            if (stat == zoom) { zooms++; zoomRecords.Add(record & Key); } else if (stat == scope) scopes++; else aims++;
            found.Add((i, record));
        }
        if (zeroShelf < 0) { Log.Write("RA01 M35 no aim zoom: no 0.0 shelf found - left alone"); return; }

        // RA01: M35-1 - Kiroshi zoom (Rob 21:46: broken): zoom-level cards are kept only where a gun or scope owns them (see GunOrScopeZooms)
        if (!Lists(flat, shelf, out var keysAt, out var items)) { Log.Write("RA01 M35 no aim zoom: owner list not found - left alone"); return; }
        var gunZooms = GunOrScopeZooms(flat, shelf, idKind, zoomRecords, keysAt, items);
        found.RemoveAll(f => zoomRecords.Contains(f.record & Key) && !gunZooms.Contains(f.record & Key));
        zooms = gunZooms.Count;

        foreach (var (_, record) in found)
        {
            ulong valueKey = WeaponEventProbe.Extend(record, ".value") & Key;
            int v = IndexOf(flat, valueKey);
            if (v < 0) continue;
            int from = Arrow(flat[v]);
            if (from < 0 || U64(shelf, from) != floatKind || from == zeroShelf) continue;      // RA01: not a number, or already 0.0
            if (BitConverter.ToSingle(shelf, from + 8) == 0f) continue;
            edits.Add((cards + v * 8, flat[v], (flat[v] & Key) | Encode(zeroShelf)));
            if (Note(tdb, record, valueKey, from, zeroShelf)) notes++; else missed++;
        }
        Log.Write($"RA01 M35 no aim zoom: {zooms} zoom-level (guns and scopes; {zoomRecords.Count - zooms} left for Kiroshi, fists and cyberware), {scopes} scope-view and {aims} aim-view cards found; {edits.Count - notes} arrows to turn, {notes} record notes found, {missed} records without a note");
    }

    // RA01: M35-1 - which zoom-level cards belong to a gun or a scope. Each card is listed by its owner: an item's ".statModifiers" list, or a stat group's,
    // whose name is in turn listed in an item's ".statModifierGroups" (like a family tree, up to 4 steps). A card is kept when ANY owner is a gun
    // (equip area Weapon, WeaponHeavy or VDefaultHandgun) or a scope (item type Prt_ShortScope, Prt_LongScope, Prt_TechSniperScope, Prt_PowerSniperScope);
    // fists, arm cyberware and Kiroshi's own cards are left alone. Modded guns and scopes use the same areas and types. Null if the lists can't be read.
    // Cost - CPU: low (one pass over the cards, about 60,000 list reads, up to 4 passes over the lists; once); GPU: none.
    static HashSet<ulong> GunOrScopeZooms(ulong[] flat, byte[] shelf, ulong idKind, HashSet<ulong> zoomRecords, Dictionary<int, List<ulong>> keysAt, Dictionary<int, ulong[]> items)
    {
        bool Has(ulong id, string suffix) => IndexOf(flat, WeaponEventProbe.Extend(id, suffix) & Key) >= 0;
        ulong IdValue(ulong id, string suffix) { int at = ShelfOf(flat, WeaponEventProbe.Extend(id, suffix)); return at >= 0 && at + 16 <= shelf.Length && U64(shelf, at) == idKind ? U64(shelf, at + 8) & Key : 0; }
        var areas = new HashSet<ulong> { Id("EquipmentArea.Weapon"), Id("EquipmentArea.WeaponHeavy"), Id("EquipmentArea.VDefaultHandgun") };
        var types = new HashSet<ulong> { Id("ItemType.Prt_ShortScope"), Id("ItemType.Prt_LongScope"), Id("ItemType.Prt_TechSniperScope"), Id("ItemType.Prt_PowerSniperScope") };

        var gun = new HashSet<ulong>();                                     // RA01: everything known to belong to a gun or scope (cards and groups)
        var frontier = new HashSet<ulong>(zoomRecords);
        var parentsOf = new Dictionary<ulong, List<ulong>>();
        for (int step = 0; step < 4 && frontier.Count > 0; step++)
        {
            var next = new HashSet<ulong>();
            foreach (var (at, list) in items)
                foreach (ulong entry in list)
                {
                    if (!frontier.Contains(entry)) continue;
                    foreach (ulong key in keysAt[at])
                        foreach (string suffix in new[] { ".statModifiers", ".statModifierGroups" })
                        {
                            ulong owner = Unextend(key, suffix) & Key;
                            bool item = Has(owner, ".itemType") || Has(owner, ".equipArea");
                            if (item && (areas.Contains(IdValue(owner, ".equipArea")) || types.Contains(IdValue(owner, ".itemType")))) gun.Add(entry);
                            else if (!item && Has(owner, ".statModifiers")) { next.Add(owner); if (!parentsOf.TryGetValue(owner, out var kids)) parentsOf[owner] = kids = new List<ulong>(); kids.Add(entry); }
                        }
                }
            frontier = next;
        }
        for (int step = 0; step < 4; step++)                                // RA01: a group owned by a gun makes its own cards the gun's too
            foreach (var (group, kids) in parentsOf) if (gun.Contains(group)) foreach (ulong k in kids) gun.Add(k);
        gun.IntersectWith(zoomRecords);
        return gun;
    }
    static ulong Id(string name) => WeaponEventProbe.TweakDbId(name) & Key;

    // RA01: M35-1 - the game's id lists: every list shelf with the cards pointing at it (keysAt), and each list's entries (items: a game
    // list is pointer, capacity, size). False if the reference list (Items.Base_Handgun.statModifiers) isn't there.
    // Cost - CPU: low (one pass over the cards and about 60,000 list reads; once, at the main menu); GPU: none.
    static bool Lists(ulong[] flat, byte[] shelf, out Dictionary<int, List<ulong>> keysAt, out Dictionary<int, ulong[]> items)
    {
        keysAt = new Dictionary<int, List<ulong>>();                        // RA01: list shelf -> the cards pointing at it
        items = new Dictionary<int, ulong[]>();                             // RA01: list shelf -> its entries
        int listShelf = ShelfOf(flat, WeaponEventProbe.TweakDbId("Items.Base_Handgun.statModifiers"));
        if (listShelf < 0) return false;
        ulong listKind = U64(shelf, listShelf);
        foreach (ulong card in flat)
        {
            int at = Arrow(card);
            if (at < 0 || at + 24 > shelf.Length || U64(shelf, at) != listKind) continue;
            if (!keysAt.TryGetValue(at, out var keys)) keysAt[at] = keys = new List<ulong>();
            keys.Add(card & Key);
        }
        foreach (int at in keysAt.Keys)
        {
            nint entries = (nint)(long)U64(shelf, at + 8);
            uint size = BitConverter.ToUInt32(shelf, at + 20);
            var list = new ulong[Math.Min(size, 4096u)];
            if (list.Length > 0) fixed (ulong* l = list) if (!Read(entries, l, list.Length * 8)) continue;
            for (int i = 0; i < list.Length; i++) list[i] &= Key;
            items[at] = list;
        }
        return true;
    }

    // RA01: the record's own note of where its number is: the record is found in the game's record list (a hash table: bucket -> chain), then its first
    // 0x100 bytes are searched for the exact card ID; the note keeps its distance from the shelf start (0 or 8) on the new shelf.
    // Cost - CPU: extremely low (a few reads per record); GPU: none.
    static bool Note(nint tdb, ulong record, ulong valueKey, int from, int to)
    {
        nint table = 0, nodes = 0; uint capacity = 0, stride = 0;
        if (!Read(tdb + RecordsAt, &table, 8) || !Read(tdb + RecordCapacityAt, &capacity, 4) || !Read(tdb + RecordNodesAt, &nodes, 8) || !Read(tdb + RecordStrideAt, &stride, 4) || capacity == 0 || stride < 0x18) return false;
        uint hash = (uint)record, idx = 0;
        if (!Read(table + (nint)(hash % capacity) * 4, &idx, 4)) return false;
        for (int step = 0; step < 64 && idx != uint.MaxValue; step++)
        {
            nint node = nodes + (nint)idx * (nint)stride;
            uint next = 0, hashed = 0; ulong key = 0; nint obj = 0;
            if (!Read(node, &next, 4) || !Read(node + 4, &hashed, 4) || !Read(node + 8, &key, 8) || !Read(node + 0x10, &obj, 8)) return false;
            if (hashed == hash && (key & Key) == (record & Key))
            {
                if (obj == 0) return false;
                for (int o = NoteScanFrom; o < NoteScanFrom + NoteScanBytes; o += 8)
                {
                    ulong q = 0;
                    if (!Read(obj + o, &q, 8) || (q & Key) != valueKey) continue;
                    int gap = Arrow(q) - from;
                    if (gap != 0 && gap != 8) return false;
                    edits.Add((obj + o, q, valueKey | Encode(to + gap)));
                    return true;
                }
                return false;
            }
            idx = next;
        }
        return false;
    }

    // RA01: turns every arrow and note to 0.0 (on) or back (off); each one only if it still holds the value expected, so nothing else is overwritten.
    // Cost - CPU: extremely low (a few hundred 8-byte read-and-writes, only when the setting changes); GPU: none.
    static void Turn(bool on)
    {
        int done = 0, skipped = 0;
        foreach (var (at, original, changed) in edits)
        {
            ulong now = 0, want = on ? changed : original, expect = on ? original : changed;
            if (!Read(at, &now, 8) || now != expect) { skipped++; continue; }
            nuint wrote;
            if (WriteProcessMemory(Self, at, &want, 8, &wrote) && wrote == 8) done++; else skipped++;
        }
        applied = on;
        Log.Write($"RA01 M35 no aim zoom {(on ? "ON" : "OFF")}: {done} turned, {skipped} skipped (guns already in hand change after loading a save)");
    }

    // RA01: small helpers - a card's arrow (bytes 5-7, big-endian) and back, an 8-byte read from the copied shelves, a card's index by its key
    // (binary search of the sorted list by its low 4 bytes, as the game does) and the shelf of a card.
    // Cost - CPU: extremely low; GPU: none.
    static int Arrow(ulong card) => (int)(((card >> 40) & 0xFF) << 16 | ((card >> 48) & 0xFF) << 8 | (card >> 56));
    static ulong Encode(int at) => ((ulong)(at >> 16) & 0xFF) << 40 | ((ulong)(at >> 8) & 0xFF) << 48 | ((ulong)at & 0xFF) << 56;
    static ulong U64(byte[] b, int at) => BitConverter.ToUInt64(b, at);
    static int ShelfOf(ulong[] flat, ulong key) { int i = IndexOf(flat, key & Key); return i < 0 ? -1 : Arrow(flat[i]); }
    static int IndexOf(ulong[] flat, ulong key)
    {
        int lo = 0, hi = flat.Length;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if ((uint)flat[mid] < (uint)key) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < flat.Length && (uint)flat[i] == (uint)key; i++) if ((flat[i] & Key) == key) return i;
        return -1;
    }

    // RA01: the record's ID from its card's ID: the CRC32 run backwards through the suffix (".statType"), lengths taken off - Extend in reverse.
    // Cost - CPU: extremely low (a CRC over a short suffix); GPU: none.
    static ulong Unextend(ulong id, string suffix)
    {
        uint c = ~(uint)id;
        for (int n = suffix.Length - 1; n >= 0; n--)
        {
            for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? ((c ^ 0xEDB88320) << 1) | 1 : c << 1;
            c ^= (byte)suffix[n];
        }
        return ~c | ((((id >> 32) & 0xFF) - (ulong)suffix.Length) << 32);
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
