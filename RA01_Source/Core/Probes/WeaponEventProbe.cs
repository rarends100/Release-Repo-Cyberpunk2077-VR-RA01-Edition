using System.Runtime.InteropServices;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M13-25 to M13-32 - the game's own "a weapon just went active" moment, and the weapon's exact TYPE, read from the game's own data (no guessing).
// THE MOMENT: the game sends a SetActiveWeaponEvent whenever a weapon goes active, and registers one handler for it (2.31 exe: the event's class is kept
// at RVA 0x3431908; 0x1270C50 registers the function at RVA 0x656288 as its handler, found here by its first 28 bytes). The handler is called as
// (rcx = the object listening, rdx = the event); the usual detour (CrosshairProbe.Hook) on its first instruction hands both to OnActiveWeapon first,
// then the game's own code runs exactly as before. Nothing in the game is called or changed - safe reads only.
// WHICH WEAPON: event +0x40 is the weapon's game-database (TweakDB) ID - the CRC32 fingerprint of its record name plus the name's length (checked:
// Items.Preset_Unity_Default, Items.Preset_Knife_Default, ...).
// ITS TYPE: TypeOf reads the weapon's "itemType" value out of the game's database (see TypeOf) - e.g. ItemType.Wea_Katana - and ItemTypes turns it into
// plain English and a WeaponProbe.Kind. Like a library: catalogue number -> index card -> shelf position -> the book.
// WHOSE: the listener's +0x50 is the object it listens for - V herself (class PlayerPuppet), an NPC (NPCPuppet) or the weapon (gameweaponObject), whose
// +0x200 is the character holding it. Class names are read the same safe way (object +0x30 -> its class, class +0x18 -> its name's fingerprint).
// The M13-31 test: every one of V's melee, cyberware, fists and most gun switches produced an event FOR V; a redrawn rifle or pistol sometimes only
// reached a copy held by an NPCPuppet that mirrors V's equipment (likely the inventory preview). So the latest event for V and the latest event from
// anyone are both kept, and WeaponProbe.Held prefers V's (see Pick).
// Cost - CPU: extremely low (about 30 safe reads per event, a handful of events per weapon switch; one log line per event kept, at most 300); GPU: none.
internal static unsafe class WeaponEventProbe
{
    // RA01: the handler's first 28 bytes in 2.31 (found exactly once): mov [rsp+10h],rbx / mov [rsp+18h],rsi / push rbp,rdi,r14 / ... / mov rcx,[rcx+50h]
    static ReadOnlySpan<byte> Handler => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x55, 0x57, 0x41, 0x56, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x60, 0x48, 0x8B, 0xF1, 0x48, 0x8B, 0xDA, 0x48 };
    static ReadOnlySpan<byte> MovRsp10 => new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x10 };
    const int WeaponAt = 0x40, TargetAt = 0x50, OwnerAt = 0x200, ClassAt = 0x30, NameAt = 0x18, MaxLines = 300;
    internal static int TdbAt => HookRegistry.Rva(HookRegistry.TweakDb);   // RA01: M43 - 2.31: 0x37C1490; another build: the address list's (0 = unknown: no database reads)
    internal const int FlatsAt = 0x40, FlatCountAt = 0x4C; // RA01: where the game keeps its database pointer (2.31), and the database's sorted value list (M35: Core\GameData\AimZoomData.cs uses them too)
    const ulong PlayerPuppetName = 0xCEBECAE898E55B86, NpcPuppetName = 0x317316F0865EA816, WeaponObjectName = 0x829834D980C37E84; // RA01: FNV-1a 64 of the class names
    static readonly nint Self = GetCurrentProcess();
    static readonly object gate = new();
    static readonly Dictionary<nint, (WeaponProbe.Kind kind, string name)> weaponObjects = new(); // RA01: M14-5 - each weapon object an event named, and its type
    static readonly Dictionary<nint, ulong> weaponIds = new();      // RA01: M27-6j - each weapon object's database ID (for its audio set, read when first asked)
    static readonly Dictionary<ulong, string> audioSets = new();     // RA01: M27-6j - database ID -> its audio set ("unity", "sor22", ...; "" = none)
    const int MaxWeaponObjects = 128;
    static readonly HashSet<ulong> vWeaponIds = new();                // RA01: M14-5c - weapon types the game has announced for V herself
    static (WeaponProbe.Kind kind, string name, long at) latestForV, latestAny; // RA01: M13-32 - the newest kept event for V, and from anyone (V or the mirror copy)
    static int lines, installed;

    // RA01: Call once at start-up, after CameraHook (which loads ExeScan).
    // Cost - CPU: low, once (one scan of the code section, about 10 ms, and a 5-byte jump written); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            if (TdbAt != 0) HookRegistry.Found(HookRegistry.TweakDb, ExeScan.Base + TdbAt, HookRegistry.SameBuild ? "2.31 place" : "address list; the database is checked when it loads");   // RA01: M43
            List<nint> found = HookRegistry.FindAll(HookRegistry.WeaponEvent, Handler);   // RA01: M43 - reported to the Rehooker
            if (found.Count != 1) { Log.Write($"RA01 weapon events NOT installed: the handler was found {found.Count} times (needs exactly 1) - the skeleton reading alone decides what V holds"); return; }
            if (!CrosshairProbe.Hook(found[0], MovRsp10, (nint)(delegate* unmanaged<nint, nint, void>)&OnActiveWeapon, "active-weapon event handler")) HookRegistry.Off(HookRegistry.WeaponEvent, "the hook could not be put in");
        }
        catch (Exception e) { Log.Write($"RA01 weapon events failed: {e.Message}"); }
    }

    // RA01: M13-32 - for WeaponProbe.Held, given when V's hands last changed ('changedAt', Environment.TickCount64): V's own newest event if it came no more
    // than ForVWindowMs before that change (it usually comes about 0.3 s BEFORE the skeleton notices); otherwise the NPC copy's newest event, but only if
    // it came within CopyWindowMs either side of the change - so an enemy drawing a gun near V at some other moment can never rename V's weapon. False if neither.
    // Cost - CPU: extremely low (a short lock and a few compares); GPU: none.
    public static bool Pick(long changedAt, out WeaponProbe.Kind kind, out string name)
    {
        lock (gate)
        {
            var e = latestForV.at != 0 && latestForV.kind != WeaponProbe.Kind.None && latestForV.at >= changedAt - ForVWindowMs ? latestForV
                  : latestAny.at != 0 && Math.Abs(latestAny.at - changedAt) <= CopyWindowMs ? latestAny : default;
            kind = e.kind; name = e.name ?? "";
            return e.at != 0;
        }
    }
    const long ForVWindowMs = 3000, CopyWindowMs = 1000;

    // RA01: M14-5 - what type a weapon object is, as the game's own event named it when it went active. False if no event has named it yet.
    // Cost - CPU: extremely low (a short lock and a dictionary lookup); GPU: none.
    public static bool KindOf(nint weapon, out WeaponProbe.Kind kind, out string name)
    {
        lock (gate)
        {
            bool known = weaponObjects.TryGetValue(weapon, out var e);
            kind = e.kind; name = e.name ?? "";
            return known;
        }
    }

    // RA01: M27-6j - THE GUN'S AUDIO SET (Rob 00:14 log: the gun's own database value "audioName" reads "wea_set_unity", "wea_set_omaha", "wea_set_sor22",
    // "wea_set_sidewinder" - the model, exactly as the game's sound list names it). Returns the part after "wea_set_" for this weapon object ("" if the
    // weapon wasn't announced, has no audio name, or the name can't be read). Read once per gun and remembered.
    // Cost - CPU: extremely low (a dictionary look-up; the first time per gun one database look-up and one name-pool read); GPU: none.
    public static string AudioSetOf(nint weapon)
    {
        ulong id;
        lock (gate)
        {
            if (!weaponIds.TryGetValue(weapon, out id)) return "";
            if (audioSets.TryGetValue(id, out string? known)) return known;
        }
        byte* raw = stackalloc byte[16];
        string set = "";
        if (StoredRaw(id, ".audioName", raw, 16))
        {
            string name = GameCall.NameText(*(ulong*)(raw + 8));
            set = name.StartsWith("wea_set_", StringComparison.Ordinal) ? name[8..] : "";
            Log.Write($"RA01 weapon audio set: weapon ID 0x{id:X10} - audioName '{name}'{(set == "" ? " (not a wea_set_ name - the gun type's sounds are used)" : "")}");
        }
        lock (gate) { if (audioSets.Count > 256) audioSets.Clear(); audioSets[id] = set; }
        return set;
    }

    // RA01: M13-33 - the newest event the game sent for V herself (kind None with name "empty hands" if it said her hands are empty), and when
    // (Environment.TickCount64). False until the first one arrives this session. WeaponProbe.Held trusts this first.
    // Cost - CPU: extremely low (a short lock and a copy); GPU: none.
    public static bool LatestForV(out WeaponProbe.Kind kind, out string name, out long at)
    {
        lock (gate) { kind = latestForV.kind; name = latestForV.name ?? ""; at = latestForV.at; }
        return at != 0;
    }

    // RA01: the game calls this (through the detour) each time its SetActiveWeaponEvent handler runs, then carries on into its own code. Keeps the event if
    // it is for V or for an NPC (the mirror copy - Pick only uses those close to V's own change) and the type is one we know; an empty weapon ID
    // for V is kept as "empty hands", an unknown type for V is only logged (M13-33). Never throws.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    [UnmanagedCallersOnly]
    static void OnActiveWeapon(nint listener, nint evt)
    {
        try
        {
            ulong id = 0; nint target = 0, owner = 0;
            if (!Read(evt + WeaponAt, &id, 8) || !Read(listener + TargetAt, &target, 8) || target < 0x10000) return;
            ulong cls = ClassName(target), holder = cls;
            if (cls == WeaponObjectName && Read(target + OwnerAt, &owner, 8) && owner > 0x10000) holder = ClassName(owner);
            if (holder != PlayerPuppetName && holder != NpcPuppetName) return;
            bool forV = holder == PlayerPuppetName;
            var (kind, name) = id == 0 ? (WeaponProbe.Kind.None, "empty hands") : TypeOf(id);
            if (kind == WeaponProbe.Kind.None && (id != 0 || !forV))                          // RA01: M13-33 - not a weapon we know (a grenade, a quickhack...), or an NPC's empty hands
            {
                if (forV && Interlocked.Increment(ref lines) <= MaxLines) Log.Write($"RA01 weapon event: {name} for V (weapon ID 0x{id:X10}) - not on our list, ignored");
                return;
            }
            long now = Environment.TickCount64;
            lock (gate)
            {
                if (forV && id != 0) vWeaponIds.Add(id);
                if (kind != WeaponProbe.Kind.None && (forV || vWeaponIds.Contains(id))) latestAny = (kind, name, now); // RA01: M14-5c - a copy event only counts for one of V's own weapon types (never an enemy's gun)
                if (kind != WeaponProbe.Kind.None && cls == WeaponObjectName)                     // RA01: M14-5 - remember what this weapon object is
                {
                    if (weaponObjects.Count >= MaxWeaponObjects) { weaponObjects.Clear(); weaponIds.Clear(); }
                    weaponObjects[target] = (kind, name);
                    weaponIds[target] = id;                                                        // RA01: M27-6j
                }
                if (forV) latestForV = (kind, name, now);                                         // RA01: M13-33 - "empty hands" (ID 0) is kept for V too
            }
            if (Interlocked.Increment(ref lines) <= MaxLines) Log.Write($"RA01 weapon event: {name} {(forV ? "for V" : "for the NPC copy")} (weapon ID 0x{id:X10})");
        }
        catch { }
    }

    // RA01: an object's class name fingerprint by safe reads (object +0x30 -> class, class +0x18 -> name), 0 if it isn't a game object.
    // Cost - CPU: extremely low (two safe reads); GPU: none.
    static ulong ClassName(nint obj)
    {
        nint type = 0; ulong name = 0;
        if (!Read(obj + ClassAt, &type, 8) || type < 0x10000 || (type & 7) != 0 || !Read(type + NameAt, &name, 8)) return 0;
        return name;
    }

    // RA01: the weapon's type, from the game's own database by safe reads. A stored value's key is the fingerprint of its full name, e.g.
    // RA01: "Items.Preset_Unity_Default.itemType" - and a CRC32 can be CONTINUED, so that key comes from the weapon's ID alone (Extend; checked offline against
    // RA01: the user's tweakdb_ep1.bin). The value is the type's own ID (ItemType.<name>), matched in ItemTypes.
    // RA01: M13-47 - a gun's "evolution" (WeaponEvolution.Power / Tech / Smart) is read the same way (checked offline: the Yukimura reads Smart, the Kenshin
    // RA01: and Achilles Tech, the Unity, Lexington and Masamune Power); a Smart gun's name gets SmartTag added, so WeaponProbe.HeldSmart can tell.
    // RA01: (None, reason) if any step fails.
    // RA01: Cost - CPU: extremely low (two binary searches, about 50 safe reads); GPU: none.
    static (WeaponProbe.Kind, string) TypeOf(ulong id)
    {
        ulong type = StoredId(id, ".itemType", out string why);
        if (type == 0) return (WeaponProbe.Kind.None, why);
        if (!ItemTypes.TryGetValue(type, out var known)) return (WeaponProbe.Kind.None, $"(type 0x{type:X10} not on our list)");
        if (known.Item1 == WeaponProbe.Kind.Gun && known.Item2 != "Heavy machine gun" && StoredId(id, ".evolution", out _) == SmartEvolution) known.Item2 += SmartTag;
        // RA01: M27-6o - Rob 03:03: the Mk.31 HMG (torn off a turret) reads "Smart" in the game's data but has no lock-on, so it kept the head-aimed crosshair
        // RA01: and head-aimed shots. It is never treated as Smart: crosshair hidden, the barrel aims, its dropped magazine sounds metal.
        return known;
    }
    public const string SmartTag = ", Smart";
    static readonly ulong SmartEvolution = TweakDbId("WeaponEvolution.Smart");

    // RA01: one database value that is itself a database ID (a "foreign key"), stored under "<the name behind id><suffix>": the game's own lookup (0x33FF88) -
    // binary search of the database's sorted value list (pointer at +0x40, count at +0x4C) by the key; the entry's bytes 5-7 give where the value sits
    // in the value block ([database]+0); the value is an 8-byte type header, then the ID. Like a library: catalogue number -> index card -> shelf -> book.
    // 0 (and why) if any step fails.
    // Cost - CPU: extremely low (about 25 safe reads for the binary search); GPU: none.
    static ulong StoredId(ulong id, string suffix, out string why)
    {
        nint tdb = 0, flats = 0, values = 0; uint flatCount = 0;
        why = "(database not found)";
        if (TdbAt == 0 || !Read(ExeScan.Base + TdbAt, &tdb, 8) || tdb == 0) return 0;
        why = "(value list unreadable)";
        if (!Read(tdb, &values, 8) || !Read(tdb + FlatsAt, &flats, 8) || !Read(tdb + FlatCountAt, &flatCount, 4) || flatCount == 0) return 0;
        ulong key = Extend(id, suffix);
        uint lo = 0, hi = flatCount;
        while (lo < hi)
        {
            uint mid = (lo + hi) / 2, probe = 0;
            if (!Read(flats + (nint)mid * 8, &probe, 4)) return 0;
            if (probe < (uint)key) lo = mid + 1; else hi = mid;
        }
        ulong flat = 0, value = 0;
        why = $"(no {suffix[1..]})";
        if (lo >= flatCount || !Read(flats + (nint)lo * 8, &flat, 8) || (flat & 0xFFFFFFFFFF) != key) return 0;
        nint offset = (nint)(((flat >> 40) & 0xFF) << 16 | ((flat >> 48) & 0xFF) << 8 | (flat >> 56));
        why = $"({suffix[1..]} unreadable)";
        if (!Read(values + offset + 8, &value, 8)) return 0;
        return value & 0xFFFFFFFFFF;
    }

    // RA01: M27-6h - READ-ONLY: the raw bytes of any database value "<the name behind id><suffix>" (the same look-up as StoredId), copied whole into 'into'
    // (its 8-byte type header first, then the value), for the gun model probe to decode (a name, a text or an ID). False if there is no such value.
    // Cost - CPU: extremely low (about 25 safe reads for the binary search, one small copy); GPU: none.
    public static bool StoredRaw(ulong id, string suffix, byte* into, int bytes)
    {
        nint tdb = 0, flats = 0, values = 0; uint flatCount = 0;
        if (TdbAt == 0 || !Read(ExeScan.Base + TdbAt, &tdb, 8) || tdb == 0 || !Read(tdb, &values, 8) || !Read(tdb + FlatsAt, &flats, 8) || !Read(tdb + FlatCountAt, &flatCount, 4) || flatCount == 0) return false;
        ulong key = Extend(id, suffix);
        uint lo = 0, hi = flatCount;
        while (lo < hi)
        {
            uint mid = (lo + hi) / 2, probe = 0;
            if (!Read(flats + (nint)mid * 8, &probe, 4)) return false;
            if (probe < (uint)key) lo = mid + 1; else hi = mid;
        }
        ulong flat = 0;
        if (lo >= flatCount || !Read(flats + (nint)lo * 8, &flat, 8) || (flat & 0xFFFFFFFFFF) != key) return false;
        nint offset = (nint)(((flat >> 40) & 0xFF) << 16 | ((flat >> 48) & 0xFF) << 8 | (flat >> 56));
        return Read(values + offset, into, bytes);
    }

    // RA01: the database ID of "<the name behind id><suffix>", from the ID alone: CRC32 carried on through the suffix, lengths added.
    // Cost - CPU: extremely low (a CRC over a short suffix); GPU: none.
    internal static ulong Extend(ulong id, string suffix)
    {
        uint c = ~(uint)id;                                           // RA01: undo the final flip to get the running CRC back, then continue it
        foreach (char ch in suffix) { c ^= (byte)ch; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320 & (uint)-(int)(c & 1)); }
        return ~c | ((((id >> 32) & 0xFF) + (ulong)suffix.Length) << 32);
    }

    // RA01: the game's item types (user-checked in WolvenKit: all 27 ItemType.Wea_* plus the Cyb_* ones), by the database ID of "ItemType.<name>", with
    // RA01: what they mean for RA01 (WeaponProbe.Kind) and a plain-English name. Fists are their own kind (M13-34): either hand punches.
    static readonly Dictionary<ulong, (WeaponProbe.Kind, string)> ItemTypes = BuildItemTypes();

    // RA01: builds ItemTypes once at start-up.
    // RA01: Cost - CPU: extremely low (about 35 short CRC32s, once); GPU: none.
    static Dictionary<ulong, (WeaponProbe.Kind, string)> BuildItemTypes()
    {
        const WeaponProbe.Kind G = WeaponProbe.Kind.Gun, M = WeaponProbe.Kind.Melee;
        (string id, WeaponProbe.Kind kind, string english)[] list =
        {
            ("Wea_Handgun", G, "Handgun (pistol)"), ("Wea_Revolver", G, "Revolver"), ("Wea_SubmachineGun", G, "SMG"), ("Wea_AssaultRifle", G, "Assault rifle"), ("Wea_Rifle", G, "Rifle"),
            ("Wea_PrecisionRifle", G, "Precision rifle"), ("Wea_SniperRifle", G, "Sniper rifle"), ("Wea_Shotgun", G, "Shotgun"), ("Wea_ShotgunDual", G, "Double-barrel shotgun"),
            ("Wea_LightMachineGun", G, "Light machine gun"), ("Wea_HeavyMachineGun", G, "Heavy machine gun"), ("Wea_GrenadeLauncher", G, "Grenade launcher"),
            ("Wea_Katana", M, "Katana"), ("Wea_Sword", M, "Sword"), ("Wea_LongBlade", M, "Long blade"), ("Wea_ShortBlade", M, "Short blade"), ("Wea_Knife", M, "Knife"),
            ("Wea_Machete", M, "Machete"), ("Wea_Chainsword", M, "Chainsword"), ("Wea_Axe", M, "Axe"), ("Wea_Hammer", M, "Hammer"), ("Wea_OneHandedClub", M, "One-handed club"),
            ("Wea_TwoHandedClub", M, "Two-handed club"), ("Wea_Melee", M, "Melee weapon"), ("Wea_Fists", WeaponProbe.Kind.Fists, "Fists"),
            ("Cyb_MantisBlades", WeaponProbe.Kind.MantisBlades, "Mantis Blades"), ("Cyb_StrongArms", WeaponProbe.Kind.GorillaArms, "Gorilla Arms"),
            ("Cyb_NanoWires", WeaponProbe.Kind.Monowire, "Monowire"), ("Cyb_Launcher", WeaponProbe.Kind.ProjectileLauncher, "Projectile Launch System"),
        };
        var map = new Dictionary<ulong, (WeaponProbe.Kind, string)>();
        foreach (var (id, kind, english) in list) map[TweakDbId("ItemType." + id)] = (kind, english);
        return map;
    }

    // RA01: the game's database ID for a name: CRC32 of the name in the low 4 bytes, the name's length in the 5th (the standard CRC32, as zlib uses).
    // Cost - CPU: extremely low (a byte loop over a short name); GPU: none.
    internal static ulong TweakDbId(string name)
    {
        uint c = 0xFFFFFFFF;
        foreach (char ch in name) { c ^= (byte)ch; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320 & (uint)-(int)(c & 1)); }
        return ~c | ((ulong)name.Length << 32);
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
