using System.Numerics;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M14-0 to M14-0c - THE GAME ANCHOR: who V is, and whether a loading screen is up, straight from the game's own records. One fixed point leads to everything:
// the game ENGINE object. RED4ext.SDK (reference only, nothing copied, never required) names it by the fingerprint 0x97F209D6, and the user's address list
// gives its home for 2.31: a pointer at exe+0x342AC00 (the game's own code reads it from 486 places). From there, by the SDK's field names:
//   engine +0x308 the framework -> +0x10 the GAME INSTANCE -> +0x38 its list of game systems (entries at +0x0, count at +0xC, 16-byte handles, object first).
// Every game object says what it is: object +0x30 its class, class +0x18 the class's name code (FNV-1a 64, as WeaponEventProbe uses), class +0x10 its parent.
// Like walking in the front door and reading the building directory, instead of searching each floor for the right office. Found in M14-0/0b (user tests):
//  - V: the player system (cpPlayerSystem) +0x88 is V herself (PlayerPuppet) - the same body as the camera's owner, every load, and kept through every
//    cyberware rebuild. Her SKELETONS: her list of components (entity +0xA0, same list shape) -> each of class entAnimatedComponent -> +0x178 -> +0x8 = a
//    skeleton value PoseHook hands over with a bone list. M14-0c's first test showed V has more than one animated component (her body is #2, an earlier one
//    reads empty), so ALL of hers are kept (IsVSkeleton) and PlayerSkeleton picks, among them, the one with the most bones it has seen animated - her body;
//  - LOADING: the UI system (gameuiGameSystemUI) +0x98 reads 0 while a loading screen is up (3 in the world) - Loading;
//  - MENUS: not settled yet. M14-0c's watcher found the UI system's +0xAC reads 8 while a hub menu (inventory, map) is open and 7 in play - but the pause
//    menu moved none of its candidate fields. M14-0e also watches the TIME SYSTEM (gameTimeSystem, its first TimeBytes): every menu should stop game time.
//    Its fields that change often (clocks) are dropped by themselves after NoisyAfter changes; the rare ones are logged beside the old signals.
// The old ways stay as the backup (the user's wish): the Dev tab's "V and screens: old method" (Tuning.OldVAndScreens) switches back to them, and they take
// over by themselves whenever the anchor can't be read (a game update that moves the engine pointer). Nothing here writes to the game or calls it; every read
// is a safe read - a bad pointer fails one read, never the game.
// Cost - CPU: extremely low (asleep between ticks; each tick, 10 a second, about 20 eight-byte safe reads; the systems list is walked only when its count
// changes); GPU: none.
internal static unsafe class GameAnchor
{
    // RA01: 2.31: 0x342AC00 - from the address list (hash 2549221846 = 0x97F209D6, "CGameEngine"). M43 - from the Rehooker: the same number on 2.31, the
    // RA01: address list's on another build, 0 if unknown (then the anchor stays off and the old ways carry on).
    static int EnginePointerAt => HookRegistry.Rva(HookRegistry.Engine);
    const int FrameworkAt = 0x308, InstanceAt = 0x10, SystemsAt = 0x38, EngineStateAt = 0x108;
    const int ClassAt = 0x30, NameAt = 0x18, ParentAt = 0x10, OwnerAt = 0x50;
    const int PlayerAt = 0x88, ComponentsAt = 0xA0, StatusAt = 0x156, AnimationAt = 0x178, SkeletonAt = 0x8;
    const int LoadingAt = 0x98;                                     // RA01: UI system: 0 while loading, 3 in the world
    static readonly int[] MenuCandidates = { 0xA8, 0xAC, 0xD0, 0x138 }; // RA01: M14-0c watcher - UI system fields that moved with menus in M14-0b
    const int TickMs = 100, MaxSystems = 512, MaxComponents = 256, MaxLines = 400, MaxSkeletons = 8;
    static readonly ulong PlayerSystemName = NameCode("cpPlayerSystem"), UiSystemName = NameCode("gameuiGameSystemUI"), TimeSystemName = NameCode("gameTimeSystem");
    static readonly ulong ScriptableSystemsName = NameCode("gameScriptableSystemsContainer"); // RA01: M20-0 - holds the game's script systems (BraindanceSystem...)
    const int TimeBytes = 0x400, NoisyAfter = 8;
    static readonly int[] lastTime = new int[TimeBytes / 4], timeChanges = new int[TimeBytes / 4];
    static bool timeKnown;
    static readonly ulong PlayerPuppetName = NameCode("PlayerPuppet"), GamePlayerPuppetName = NameCode("gamePlayerPuppet"), AnimatedName = NameCode("entAnimatedComponent");
    static readonly nint Self = GetCurrentProcess();
    static int started, lines;
    static volatile bool working, loading;
    static readonly nint[] vSkeletons = new nint[MaxSkeletons]; // RA01: V's animated components' skeleton values this tick (0 = empty slot)
    static nint playerSystem, uiSystem, timeSystem;
    static nint currentV;                                           // RA01: M14-5 - V's object this tick (0 = none)
    // RA01: M14-5 - V's script field m_equippedRightHandWeapon, found by name in her class's field list (RED4ext.SDK, reference only: class +0x28 the
    // RA01: field list (pointer, count at +0xC); a field: name +0x08, position +0x20, flags +0x28 - bit 21 = kept in the object's value holder, object +0x38)
    const int FieldsAt = 0x28, FieldNameAt = 0x08, FieldPlaceAt = 0x20, FieldFlagsAt = 0x28, ValueHolderAt = 0x38, MaxFields = 1024;
    const ulong InValueHolder = 1UL << 21;
    // RA01: M14-5c - the game stores script field names WITHOUT the "m_" the decompiled scripts show (proven: V's first four fields read quickSlotsManager,
    // RA01: inspectionComponent, enviroDamageRcvComponent, mountedVehicle)
    static readonly ulong RightWeaponField = NameCode("equippedRightHandWeapon"), MeleeStateField = NameCode("meleeWeaponState");
    static readonly ulong VehicleField = NameCode("mountedVehicle"), VehicleStateField = NameCode("vehicleState"); // RA01: M19-1 - found by M19-0's probe (value holder +0x450 / +0x770 on 2.31)
    static nint fieldClass;                                         // RA01: the class the fields were looked up in (looked up again if V's class changes)
    static int weaponPlace = -1, meleePlace = -1;                   // RA01: where each sits, -1 = not found
    static bool weaponInHolder, meleeInHolder;
    static int vehiclePlace = -1, vehicleStatePlace = -1;            // RA01: M19-1
    static bool vehicleInHolder, vehicleStateInHolder;
    static readonly ulong GadgetField = NameCode("combatGadgetState"); // RA01: M21-4 - V's m_combatGadgetState (gamePSMCombatGadget), value holder +0x7BC on 2.31 (M20-0 state probe)
    static int gadgetPlace = -1;
    static bool gadgetInHolder;
    static readonly ulong SceneTierField = NameCode("sceneTier");       // RA01: M20-10 - V's m_sceneTier (GameplayTier), value holder +0x7C0 on 2.31 (M20-0 state probe)
    static int scenePlace = -1;
    static bool sceneInHolder;
    static long retryAt;                                            // RA01: M14-5c - a search that missed a field is tried again after RetryMs (V may not be complete yet)
    static int searches;
    const long RetryMs = 10_000;
    const int MaxDiagnoses = 3;
    // RA01: M14-5b - the game's own melee state names (enum gamePSMMeleeWeapon, from its scripts), for the log
    static readonly string[] MeleeStates = { "NotReady", "Equipping", "Idle", "Safe", "PublicSafe", "Parried", "Hold", "ChargedHold", "Block", "Targeting",
        "Deflect", "ComboAttack", "FinalAttack", "StrongAttack", "SafeAttack", "BlockAttack", "SprintAttack", "CrouchAttack", "JumpAttack", "ThrowAttack",
        "DeflectAttack", "EquipAttack", "Default" };

    // RA01: true while the anchor is being read correctly (engine found, its systems listed). False = the old ways decide everything.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static bool Working => working && !Tuning.OldVAndScreens;

    // RA01: is this skeleton value one of V's own (from her entity's animated components)? False when there is no V right now or the anchor isn't working.
    // Cost - CPU: extremely low (up to 8 compares); GPU: none.
    public static bool IsVSkeleton(nint skeleton)
    {
        if (!Working || skeleton == 0) return false;
        foreach (nint s in vSkeletons) if (s == skeleton) return true;
        return false;
    }

    // RA01: true while V exists in the game's records with at least one skeleton (false on loading and start screens).
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static bool HasV => Working && vSkeletons[0] != 0;

    // RA01: true while the game shows a loading screen (by the UI system's own field), false otherwise or when the anchor isn't working.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static bool Loading => Working && loading;

    // RA01: M49-0 - the game engine's own state as last read (3 = running; 0 until read), for CrashWatch's quit watch. Cost - CPU: extremely low (one read); GPU: none.
    public static int EngineState => Volatile.Read(ref engineState);
    static int engineState;

    // RA01: M20-0 - the game's container of SCRIPT systems (gameScriptableSystemsContainer: BraindanceSystem and friends live inside it), 0 if not found.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static nint ScriptableSystems => Working ? scriptableSystems : 0;
    static nint scriptableSystems;

    // RA01: M14-5 - V's own object (PlayerPuppet), 0 when there is none or the anchor isn't working.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static nint V => Working ? Volatile.Read(ref currentV) : 0;
    internal static nint PlayerSystem => Working ? playerSystem : 0;   // RA01: M44 - for FieldNames' probe (read-only)
    internal static nint UiSystem => Working ? uiSystem : 0;

    // RA01: M14-5 - THE WEAPON IN V'S RIGHT-HAND SLOT, from the game's own record of it: her script field m_equippedRightHandWeapon, which the game's own scripts
    // set whenever something goes into AttachmentSlots.WeaponRight (OnItemEquipped) and clear when it leaves (OnItemUnequipped) - it only ever describes V,
    // so an enemy drawing a gun can't change it. 'weapon' = the weapon object (0 = nothing there). False if V or the field can't be read (then the
    // older readings decide). The field is found by name once per class (logged), then each call is two safe reads.
    // Cost - CPU: extremely low (two or three 8-byte safe reads; the one-off field search reads up to a few hundred field entries); GPU: none.
    public static bool TryRightHandWeapon(out nint weapon)
    {
        weapon = 0;
        if (!ReadField(weaponPlace, weaponInHolder, out long value, acting: true)) return false; // RA01: M54-2 - Johnny's hand while you're in his body
        weapon = value > 0x10000 && value < 0x7FFFFFFFFFFF ? (nint)value : 0;
        return true;
    }

    // RA01: M15-1 - the fitted scope of the gun in V's right-hand slot, from the weapon object itself (read from the game's own natives: HasScope 0x18B2950 returns
    // the byte at weapon +0x4E2; GetScopeOffset 0x2554AB0 returns the 16 bytes at weapon +0x500 - the offset the game lines the scope up with when aiming).
    // False if there is no weapon or it can't be read.
    // Cost - CPU: extremely low (three small safe reads); GPU: none.
    public static bool TryScope(out bool hasScope, out Vector4 offset)
    {
        hasScope = false; offset = default;
        if (!TryRightHandWeapon(out nint weapon) || weapon == 0) return false;
        byte has = 0;
        Vector4 o;
        if (!Read(weapon + 0x4E2, &has, 1) || !Read(weapon + 0x500, &o, 16)) return false;
        hasScope = has != 0; offset = o;
        return true;
    }

    // RA01: M15-2 - is this skeleton one of the weapon's in V's right-hand slot? 'known' = false when V's weapon or its skeletons can't be read (then the caller
    // decides the old way). The weapon object is an entity too: its animated components' skeletons, found the same way as V's, looked up again when the
    // weapon changes or every RefreshMs (a drawn weapon's skeleton can appear a moment after the slot is filled).
    // M24-5 - while none are found it looks again every CheckMs (not RefreshMs), and each new weapon logs once how many skeletons it has - Rob's
    // 2026-10-07 18:35 test drew a pistol among NPC rifles and the muzzle came from a rifle, so this says next time whether this step was the one that failed.
    // Cost - CPU: extremely low (a few compares; the slot read ten times a second; the look-up a few hundred small safe reads, at most twice a second); GPU: none.
    public static bool IsVWeaponSkeleton(nint skeleton, out bool known)
    {
        known = false;
        long now = Environment.TickCount64;
        if (now >= weaponCheckDue)                                  // RA01: the slot is read at most every CheckMs (this runs for every skeleton the game animates)
        {
            weaponCheckDue = now + CheckMs;
            heldWeapon = TryRightHandWeapon(out nint w) ? w : 0;
        }
        nint weapon = heldWeapon;
        if (weapon == 0) return false;
        nint[] list = weaponSkeletons;
        if (weapon != weaponOfSkeletons || now >= weaponSkeletonsDue)
        {
            var fresh = new nint[MaxSkeletons];
            int found = SkeletonsOf(weapon, fresh);
            weaponSkeletons = list = fresh; weaponOfSkeletons = weapon; weaponSkeletonsDue = now + (found > 0 ? RefreshMs : CheckMs);
            if ((weapon != loggedWeapon || (found > 0) != loggedFound) && weaponLines < MaxWeaponLines) // RA01: M24-7 - capped, in case a weapon's skeleton flickers
            {
                loggedWeapon = weapon; loggedFound = found > 0; weaponLines++;
                Log.Write(found > 0 ? $"RA01 game anchor: V's weapon 0x{weapon:X} - {found} skeleton(s), first 0x{fresh[0]:X} (its muzzle is the one used)"
                                    : $"RA01 game anchor: V's weapon 0x{weapon:X} - no skeleton found yet (the muzzle falls back to a gun of the right size)");
            }
            if (found > 0) PartsLook(weapon); // RA01: M61 - log only
        }
        if (list[0] == 0) return false;
        known = true;
        foreach (nint s in list) if (s != 0 && s == skeleton) return true;
        return false;
    }
    static volatile nint[] weaponSkeletons = new nint[MaxSkeletons];
    static nint weaponOfSkeletons;
    static nint loggedWeapon;                                       // RA01: M24-5 - the weapon (and whether it had skeletons) last logged
    static bool loggedFound;
    static int weaponLines;
    const int MaxWeaponLines = 200;
    static long weaponSkeletonsDue;
    const long RefreshMs = 500, CheckMs = 100;
    static nint heldWeapon;
    static long weaponCheckDue;

    // RA01: M61 - LOOK ONLY (no behaviour change): the Widow Maker hands RA01 just its magazine's skeleton, so no barrel is ever found (Rob's log 17:00:44).
    // Like opening a crate and writing down every part inside before deciding where the barrel is: once per weapon V draws, one line names every part
    // (component) of the weapon by its class name code, and for each, any skeleton reached the way an animated part's is (+0x178 then +0x8) - "anim"
    // marks the parts RA01 already uses. Every gun gets its line, so a working gun can be compared with the Widow Maker. At most MaxPartsLines a session.
    // Cost - CPU: extremely low (up to a few hundred small safe reads, once per weapon per session); GPU: none.
    static void PartsLook(nint weapon)
    {
        lock (partsGate)
        {
            if (partsSeen.Count >= MaxPartsLines || partsSeen.Contains(weapon)) return;
            partsSeen.Add(weapon);
        }
        var text = new System.Text.StringBuilder($"RA01 game anchor: V's weapon 0x{weapon:X} parts (M61 look):");
        nint list = Ptr(weapon + ComponentsAt);
        uint count = 0;
        if (list == 0 || !Read(weapon + ComponentsAt + 0xC, &count, 4) || count > MaxComponents) { Log.Write(text.Append(" components unreadable").ToString()); return; }
        text.Append($" {count} components -");
        for (int i = 0; i < count; i++)
        {
            nint component = Ptr(list + i * 16);
            if (component == 0) { text.Append($" [{i}] empty;"); continue; }
            ulong name = ClassName(component);
            nint skeleton = Ptr(Ptr(component + AnimationAt) + SkeletonAt);
            text.Append($" [{i}] {name:X16}{(name == AnimatedName ? " anim" : "")}{(skeleton != 0 ? $" skel 0x{skeleton:X}" : "")};");
        }
        Log.Write(text.ToString());
    }
    static readonly object partsGate = new();
    static readonly List<nint> partsSeen = new();
    const int MaxPartsLines = 24;

    // RA01: M19-1 - THE VEHICLE V IS IN, from her own script fields (found by name, like the weapon slot): m_mountedVehicle - the vehicle object, set the moment
    // the get-in starts and cleared when the get-out ends (0 = on foot) - and m_vehicleState - 0 on foot, 4 getting in or out, 1 seated (M19-0's test).
    // False if V or the fields can't be read.
    // Cost - CPU: extremely low (four or five 8-byte safe reads); GPU: none.
    public static bool TryVehicle(out nint vehicle, out int state)
    {
        vehicle = 0; state = 0;
        if (!ReadField(vehiclePlace, vehicleInHolder, out long v) || !ReadField(vehicleStatePlace, vehicleStateInHolder, out long st)) return false;
        vehicle = v > 0x10000 && v < 0x7FFFFFFFFFFF ? (nint)v : 0;
        state = (int)st;                                            // RA01: the low 4 bytes: the bytes after it are other fields
        return true;
    }

    // RA01: M75 look - the same two fields read from the body you're in (Acting: Johnny's in his flashbacks, else V): is HE seated in a vehicle?
    // Cost - CPU: extremely low (four or five 8-byte safe reads); GPU: none.
    public static bool TryActingVehicle(out nint vehicle, out int state)
    {
        vehicle = 0; state = 0;
        if (!ReadField(vehiclePlace, vehicleInHolder, out long v, acting: true) || !ReadField(vehicleStatePlace, vehicleStateInHolder, out long st, acting: true)) return false;
        vehicle = v > 0x10000 && v < 0x7FFFFFFFFFFF ? (nint)v : 0;
        state = (int)st;
        return true;
    }

    // RA01: M19-1 - the first of 'names' (name codes) that an object's class or one of its parents (up to 12) is called, as its index; -1 if none.
    // Cost - CPU: extremely low (up to 13 pairs of safe reads); GPU: none.
    public static int ClassIs(nint obj, ulong[] names)
    {
        nint cls = obj == 0 ? 0 : Ptr(obj + ClassAt);
        for (int depth = 0; cls != 0 && depth < 12; depth++, cls = Ptr(cls + ParentAt))
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return -1;
            int i = Array.IndexOf(names, name);
            if (i >= 0) return i;
        }
        return -1;
    }

    // RA01: M14-5b - V's melee state as the game's own state machine sets it (her script field m_meleeWeaponState, written by OnMeleeWeaponStateChange):
    // Idle, Block, ComboAttack, StrongAttack ... (MeleeStateName). False if it can't be read. LOG-ONLY test for now: shows when the game is ready for the
    // next swing.
    // Cost - CPU: extremely low (two or three small safe reads); GPU: none.
    public static bool TryMeleeState(out int state)
    {
        state = 0;
        if (!ReadField(meleePlace, meleeInHolder, out long value, acting: true)) return false; // RA01: M54-2
        state = (int)value;
        return true;
    }

    // RA01: M21-4 - V's grenade (combat gadget) state as the game's state machine sets it (gamePSMCombatGadget): 0 none, 1 equip request, 2 in hand,
    // 3 charging (aiming, arc shown), 4 throwing, 5 putting the hand away, 6 quick throw. False if it can't be read. The field is 4 bytes; ReadField
    // reads 8, so only the low 4 are kept (the next 4 are sceneTier). Cost - CPU: extremely low (two or three small safe reads); GPU: none.
    public static bool TryGadgetState(out int state)
    {
        state = 0;
        if (!ReadField(gadgetPlace, gadgetInHolder, out long value, acting: true)) return false; // RA01: M54-2
        state = (int)(value & 0xFFFFFFFF);
        return true;
    }

    // RA01: M14-5b - a melee state's name (the game's own), or its number if unknown.
    // Cost - CPU: extremely low (an array read); GPU: none.
    public static string MeleeStateName(int state) => state >= 0 && state < MeleeStates.Length ? MeleeStates[state] : state.ToString();

    // RA01: M14-5/5b - one of V's script fields (8 bytes from where it sits; the caller keeps the part it needs). Looks the fields up again if V's class changed.
    // M54-2 - 'acting' true: read it from the body you're in (Acting - Johnny's in his flashbacks), else from V. The fields are still looked up on V's class.
    // Cost - CPU: extremely low (two or three 8-byte safe reads); GPU: none.
    static bool ReadField(int place, bool inHolder, out long value, bool acting = false)
    {
        value = 0;
        nint v = V;
        if (v == 0) return false;
        nint cls = Ptr(v + ClassAt);
        if (cls == 0) return false;
        bool missing = weaponPlace < 0 || meleePlace < 0 || vehiclePlace < 0 || vehicleStatePlace < 0;
        if (cls != fieldClass || (missing && Environment.TickCount64 >= retryAt)) { FindFields(cls); return false; } // RA01: found this time, read from the next call
        if (place < 0) return false;
        nint from = acting ? Acting(v) : v;
        nint holder = inHolder ? Ptr(from + ValueHolderAt) : from;
        long read = 0;
        if (holder == 0 || !Read(holder + place, &read, 8)) return false;
        value = read;
        return true;
    }

    // RA01: M14-5/5b, M19-1 - looks V's four fields up by name code in her class and its parents (at most 12). Logs where each is, or that it wasn't found.
    // Cost - CPU: low, once per V class (up to a few hundred pairs of small safe reads); GPU: none.
    static void FindFields(nint cls)
    {
        weaponPlace = meleePlace = vehiclePlace = vehicleStatePlace = scenePlace = gadgetPlace = -1;
        retryAt = Environment.TickCount64 + RetryMs;
        bool diagnose = ++searches <= MaxDiagnoses;
        string chain = "";
        int depth = 0;
        for (nint c = cls; c != 0 && depth < 12 && (weaponPlace < 0 || meleePlace < 0 || vehiclePlace < 0 || vehicleStatePlace < 0); c = Ptr(c + ParentAt), depth++)
        {
            nint list = Ptr(c + FieldsAt);
            uint count = 0;
            ulong className = 0;
            Read(c + NameAt, &className, 8);
            bool readable = list != 0 && Read(c + FieldsAt + 0xC, &count, 4) && count <= MaxFields;
            if (diagnose) chain += $" {className:X16}:{(readable ? count.ToString() : "?")}";
            if (!readable) continue;
            for (int i = 0; i < count; i++)
            {
                nint field = Ptr(list + i * 8);
                ulong name = 0, flags = 0;
                uint place = 0;
                if (field == 0 || !Read(field + FieldNameAt, &name, 8)) continue;
                if (diagnose && depth == 0 && i < 4) chain += $"[{name:X16}]";
                if (name != RightWeaponField && name != MeleeStateField && name != VehicleField && name != VehicleStateField && name != SceneTierField && name != GadgetField) continue;
                if (!Read(field + FieldPlaceAt, &place, 4) || !Read(field + FieldFlagsAt, &flags, 8) || place > 0x10000) continue;
                bool inHolder = (flags & InValueHolder) != 0;
                if (name == RightWeaponField) { weaponPlace = (int)place; weaponInHolder = inHolder; }
                else if (name == MeleeStateField) { meleePlace = (int)place; meleeInHolder = inHolder; }
                else if (name == VehicleField) { vehiclePlace = (int)place; vehicleInHolder = inHolder; }   // RA01: M19-1
                else if (name == SceneTierField) { scenePlace = (int)place; sceneInHolder = inHolder; }     // RA01: M20-10
                else if (name == GadgetField) { gadgetPlace = (int)place; gadgetInHolder = inHolder; }      // RA01: M21-4
                else { vehicleStatePlace = (int)place; vehicleStateInHolder = inHolder; }
            }
        }
        fieldClass = cls;
        if (diagnose && (weaponPlace < 0 || meleePlace < 0)) Say($"RA01 game anchor: V's field search (class name code:field count, first fields of the first class):{chain}");
        Say($"RA01 game anchor: V's fields - right-hand weapon {(weaponPlace >= 0 ? $"at +0x{weaponPlace:X}{(weaponInHolder ? " (value holder)" : "")}" : "NOT found")}, " +
            $"melee state {(meleePlace >= 0 ? $"at +0x{meleePlace:X}{(meleeInHolder ? " (value holder)" : "")}" : "NOT found")}, " +
            $"vehicle {(vehiclePlace >= 0 ? $"at +0x{vehiclePlace:X}" : "NOT found")}, vehicle state {(vehicleStatePlace >= 0 ? $"at +0x{vehicleStatePlace:X}" : "NOT found")}, scene tier {(scenePlace >= 0 ? $"at +0x{scenePlace:X}" : "NOT found")}, grenade state {(gadgetPlace >= 0 ? $"at +0x{gadgetPlace:X}" : "NOT found")}");
    }


    // RA01: starts the anchor's thread. Call once at start-up, after CameraHook (which loads ExeScan).
    // Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        if (EnginePointerAt == 0) { Say("RA01 game anchor: off - this game build's engine pointer isn't known (Dev_Rehook tab)"); return; }   // RA01: M43
        HookRegistry.Found(HookRegistry.Engine, ExeScan.Base + EnginePointerAt, HookRegistry.SameBuild ? "2.31 place" : "address list; its object is checked once the game is up");
        new Thread(Loop) { IsBackground = true, Name = "RA01 game anchor" }.Start();
    }

    // RA01: M43 - on a build other than 2.31 the engine pointer came from the address list, so its object is checked once: its first 8 bytes (the C++ function
    // RA01: table) must point inside the game's exe. Wrong: the anchor stops (logged, reported OFF) rather than read the wrong object. On 2.31: no check, as before.
    // RA01: Cost - CPU: extremely low (one safe read, once); GPU: none.
    static bool engineChecked;
    static bool EngineLooksRight(nint engine)
    {
        engineChecked = true;
        if (HookRegistry.SameBuild) return true;
        nint table = Ptr(engine);
        if (table >= ExeScan.Base && table < ExeScan.Base + (nint)ExeScan.ImageSize) return true;
        Say("RA01 game anchor: off - the engine pointer from the address list doesn't lead to the engine (Dev_Rehook tab)");
        HookRegistry.Off(HookRegistry.Engine, "its object's function table isn't in the game's exe");
        return false;
    }

    // RA01: the anchor's loop: each tick, the engine -> the systems (re-listed when their count changes) -> V and her skeleton, the loading field and the menu
    // watcher. Logs when the engine state, V, her skeleton or loading change. Any error is logged once; the thread then stops and the old ways take over.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    static void Loop()
    {
        int lastState = -1, lastCount = -1;
        int[] lastMenu = new int[MenuCandidates.Length];
        bool lastLoading = false;
        nint lastV = -1, lastSkeleton = -1;
        try
        {
            while (true)
            {
                Thread.Sleep(TickMs);
                nint engine = Ptr(ExeScan.Base + EnginePointerAt);
                if (engine != 0 && !engineChecked && !EngineLooksRight(engine)) return;   // RA01: M43 - on a new build only: the object must have its function table in the game's exe
                nint instance = engine != 0 ? Ptr(Ptr(engine + FrameworkAt) + InstanceAt) : 0;
                if (instance == 0) { working = false; continue; }
                int state = 0;
                Read(engine + EngineStateAt, &state, 4);
                Volatile.Write(ref engineState, state);                     // RA01: M49-0 - for the quit watch
                if (state != lastState) { lastState = state; Say($"RA01 game anchor: engine 0x{engine:X}, state {state} (3 = running)"); }

                uint count = 0;
                Read(instance + SystemsAt + 0xC, &count, 4);
                if (count != lastCount) { lastCount = (int)count; FindSystems(instance, count); }
                working = playerSystem != 0 && uiSystem != 0;
                if (!working) continue;

                nint v = Ptr(playerSystem + PlayerAt);
                if (!IsPlayer(v)) v = 0;
                Volatile.Write(ref currentV, v);                            // RA01: M14-5
                nint camOwner = CameraHook.CameraObject != 0 ? Ptr(CameraHook.CameraObject + OwnerAt) : 0;
                nint body = StandIn(v, camOwner);                           // RA01: M20-6 - V, or the stand-in body that has the camera
                cameraNotV = v != 0 && camOwner != 0 && camOwner != v && !loading;  // RA01: M20-9 - a training body or a taken-over security camera
                int vTier = v != 0 && ReadField(scenePlace, sceneInHolder, out long tier) ? (int)(tier & 0xFF) : 0; // RA01: M20-10
                sceneTier = StandInTier(body, v, vTier);                    // RA01: M54 - Johnny: the tier of the body you're in, not parked V's
                int found = body != 0 ? SkeletonsOf(body) : 0;
                if (body == 0) vSkeletons[0] = 0;
                nint skeleton = vSkeletons[0];
                if (v != lastV || skeleton != lastSkeleton)
                {
                    lastV = v; lastSkeleton = skeleton;
                    nint owner = CameraHook.CameraObject != 0 ? Ptr(CameraHook.CameraObject + OwnerAt) : 0;
                    byte status = 0;
                    if (v != 0) Read(v + StatusAt, &status, 1);
                    Say(v == 0 ? "RA01 game anchor: no V right now (loading or start screen)"
                               : $"RA01 game anchor: V 0x{v:X} (status {status}, 4 = in the world), {found} skeleton(s), first 0x{skeleton:X}{(owner == v ? ", the camera's owner" : "")}");
                }

                int load = -1;
                Read(uiSystem + LoadingAt, &load, 4);
                loading = load == 0;
                if (loading != lastLoading) { lastLoading = loading; Say($"RA01 game anchor: loading screen {(loading ? "UP" : "gone")}"); }
                UpdateTrainingSim(v);                                       // RA01: M74

                string changed = "";
                for (int i = 0; i < MenuCandidates.Length; i++)
                {
                    int value = 0;
                    Read(uiSystem + MenuCandidates[i], &value, 4);
                    if (value != lastMenu[i]) { changed += $" UI+0x{MenuCandidates[i]:X} {lastMenu[i]}->{value}"; lastMenu[i] = value; }
                }
                changed += TimeChanges();
                if (changed.Length > 0) Say($"RA01 game anchor (menu watcher):{changed} - old signals: mouse says {(MenuSense.InMenu ? "MENU" : "play")}, camera {(CameraHook.Building(System.Diagnostics.Stopwatch.GetTimestamp()) ? "built" : "not built")}");
            }
        }
        catch (Exception e) { working = false; Log.Write($"RA01 game anchor stopped: {e.Message} - the old ways decide from now on"); }
    }

    // RA01: walks the systems list once and keeps the two we read (player system, UI system). One log line.
    // Cost - CPU: extremely low, rarely (two 8-byte safe reads per system, about 150 systems, only when the list's count changes); GPU: none.
    static void FindSystems(nint instance, uint count)
    {
        playerSystem = uiSystem = timeSystem = scriptableSystems = 0;
        timeKnown = false;
        nint entries = Ptr(instance + SystemsAt);
        if (entries == 0 || count > MaxSystems) return;
        for (int i = 0; i < count; i++)
        {
            nint system = Ptr(entries + i * 16);
            ulong name = ClassName(system);
            if (name == PlayerSystemName) playerSystem = system;
            else if (name == UiSystemName) uiSystem = system;
            else if (name == TimeSystemName) timeSystem = system;
            else if (name == ScriptableSystemsName) scriptableSystems = system;
        }
        Say($"RA01 game anchor: {count} game systems - player system {(playerSystem != 0 ? "found" : "MISSING")}, UI system {(uiSystem != 0 ? "found" : "MISSING")}");
    }

    // RA01: M14-0e menu watcher, time system part: this tick's first TimeBytes of the time system against last tick's; each 4-byte field that changed is
    // counted, and listed only while it has changed at most NoisyAfter times (a clock changes every tick and soon drops out; a pause switch flips rarely).
    // Values that look like pointer halves are skipped. Returns the text to add to the watcher line.
    // Cost - CPU: extremely low (one 1 KB safe read and 256 compares, 10 times a second); GPU: none.
    static string TimeChanges()
    {
        if (timeSystem == 0) return "";
        int* now = stackalloc int[TimeBytes / 4];
        if (!Read(timeSystem, now, TimeBytes)) return "";
        string text = "";
        for (int i = 2; i < TimeBytes / 4; i++)
        {
            int was = lastTime[i];
            lastTime[i] = now[i];
            if (!timeKnown || now[i] == was) continue;
            long pair = ((long)(uint)now[i | 1] << 32) | (uint)now[i & ~1];
            if (pair > 0x10000000000 && pair < 0x7FFFFFFFFFFF) continue;
            if (++timeChanges[i] <= NoisyAfter) text += $" TIME+0x{i * 4:X} {was}->{now[i]}{(timeChanges[i] == NoisyAfter ? " (changes often - no longer listed)" : "")}";
        }
        timeKnown = true;
        return text;
    }

    // RA01: fills vSkeletons with V's skeleton values: her components -> each of class entAnimatedComponent -> +0x178 -> +0x8 (empty ones skipped), the rest
    // of the slots cleared. Returns how many were found. A slot may be read by another thread mid-update - it only ever holds a whole pointer or 0.
    // Cost - CPU: extremely low (two or three 8-byte safe reads per component, a few dozen components); GPU: none.
    static int SkeletonsOf(nint v) => SkeletonsOf(v, vSkeletons);

    // RA01: M20-6 - STAND-IN BODIES: in some places the game hands the first-person camera to a different body than V's - "Practice Makes Perfect"'s
    // Militech VR training (M20-5's log: from 20:20:32 the camera's owner was a training avatar while V's real body sat in the chair outside the
    // simulation, so VRIK kept moving arms nobody could see and the training body played the flat game's animation). With VRIK tab "Follow stand-in
    // bodies" on, a camera owner that is not V but IS a person-type body (its class built on gamePuppet) and has skeletons is used as V's body for
    // the skeleton list (VRIK then checks its arm chains like any of V's bodies). V herself (currentV: weapon slot, vehicle fields...) is never changed.
    // Logged when it switches either way. Cost - CPU: extremely low (a compare per tick; a class walk of up to 9 pairs of safe reads when the owner differs); GPU: none.
    static nint StandIn(nint v, nint owner)
    {
        bool use = Tuning.FollowStandIn && v != 0 && owner != 0 && owner != v && IsPuppet(owner);
        nint body = use ? owner : v;
        if (body != lastBody)
        {
            if (use) Say($"RA01 game anchor: stand-in body - the camera belongs to 0x{owner:X} ({ClassName(owner):X16}), not V; VRIK follows it");
            else if (lastBody != 0 && lastBody != v && v != 0) Say("RA01 game anchor: back to V's own body");
            lastBody = body;
        }
        standInV = v;
        return body;
    }
    static nint lastBody, standInV;

    // RA01: M20-7 - true while the camera's owner is a stand-in body (the Militech training avatar), not V. Lets the HUD panel count a smaller
    // RA01: body as "in the game". Cost - CPU: extremely low (two pointer reads and compares); GPU: none.
    public static bool OnStandIn => lastBody != 0 && lastBody != standInV;

    // RA01: M74 - "PRACTICE MAKES PERFECT" (Militech's VR combat training): true while the game's own camera is inside the training simulation's pocket below the
    // RA01: map (TrainMin/Max: Rob's runs 2026-10-13 saw x -422..-314, y 264..427, z -220..-212; nothing else in the story goes below z -103 there). The avatar is
    // RA01: the same body class as Johnny's and the braindance's and its fields carry no stable difference (M74 look), but the place is fixed level data - like
    // RA01: knowing which room you're in by its address instead of by the furniture. Stays set through menus (no camera built then), cleared by a loading screen
    // RA01: or no V. Covers the training avatar looking through a camera too. Logged on a change. Cost - CPU: extremely low (a 36-byte copy and six compares, 10 times a second); GPU: none.
    public static bool InTrainingSim => Working && inTrainingSim;
    static volatile bool inTrainingSim;
    const float TrainMinX = -600f, TrainMaxX = -150f, TrainMinY = 100f, TrainMaxY = 600f, TrainMaxZ = -180f;

    // RA01: M74 - the anchor's tick: is the game's camera inside the training's pocket? Cost - CPU: extremely low (see InTrainingSim); GPU: none.
    static void UpdateTrainingSim(nint v)
    {
        bool inside = inTrainingSim;
        if (v == 0 || loading) inside = false;
        else
        {
            CameraHook.ReadGameCamera(out float x, out float y, out float z);
            if (x != 0 || y != 0 || z != 0) inside = x >= TrainMinX && x <= TrainMaxX && y >= TrainMinY && y <= TrainMaxY && z <= TrainMaxZ;
        }
        if (inside == inTrainingSim) return;
        inTrainingSim = inside;
        Say(inside ? "RA01 game anchor: inside the Practice Makes Perfect training - training rules on (crosshair, levelling) (M74)" : "RA01 game anchor: out of the training - training rules off (M74)");
    }

    // RA01: M74 look - the stand-in body you're in (0 = none): VehicleProbe compares its fields with parked V's once per body, to tell the training avatar
    // RA01: from Johnny's body (same class). Cost - CPU: extremely low (two reads and a compare); GPU: none.
    public static nint StandInBody => OnStandIn ? lastBody : 0;

    // RA01: M20-9 - true while V is in the world (no loading screen) but the camera belongs to something else: a stand-in body (the Militech training) or a
    // RA01: security camera V has taken over. The game stops animating V while you look through a camera, so the HUD panel can't go by her body there, and
    // RA01: "Keep view level" must leave a camera that isn't hers alone. Cost - CPU: extremely low (one stored bool, set by the anchor's tick); GPU: none.
    public static bool CameraNotV => Working && cameraNotV;
    static volatile bool cameraNotV;

    // RA01: M20-10 - V's scene tier as the game sets it (its enum GameplayTier): 1 full gameplay, 2 staged (walking along with someone), 3 limited (dialogue scenes),
    // RA01: 4 first-person cinematic, 5 cinematic; 0 = none or unreadable. Read by the anchor's tick 10 times a second.
    // RA01: Cost - CPU: extremely low (one stored int); GPU: none.
    public static int SceneTier => Working ? sceneTier : 0;
    static volatile int sceneTier;
    static int lastStandInTier = -1;    // RA01: M54 - the stand-in's tier last logged; -1 = not on a stand-in
    static volatile nint actingBody;    // RA01: M54-2 - the same-class stand-in you're in (Johnny's body), 0 = you're V

    // RA01: M54 - JOHNNY: in Johnny Silverhand's flashbacks the camera goes to a second PlayerPuppet (Johnny's body, 370 bones) while the real V is parked
    // off stage with her scene tier at 4 (cinematic) the whole time - so reading V's tier kept VRIK handed to the game's animation and Johnny's arms
    // never followed the controllers. Think of two workers on a shift board: Johnny is on the floor, so read HIS card, not V's "on break" one.
    // Only when the stand-in is the SAME class as V (fieldClass - the class V's fields were looked up in), the sceneTier field sits at the same place,
    // so it's read from the stand-in. Anything else (V herself, a stand-in of another class, the field not found or unreadable) returns V's tier,
    // exactly the old path. Logs the stand-in's tier, with V's beside it, each time it changes.
    // M54-2 - it also sets actingBody, so the weapon, melee and grenade reads (ReadField with 'acting') come from Johnny's hand too: with parked V's
    // empty hand RA01 thought nobody held a gun and left the game's crosshair up. Logged when it switches either way.
    // Cost - CPU: extremely low (one class pointer compare; on a stand-in two small safe reads, 10 times a second); GPU: none.
    static int StandInTier(nint body, nint v, int vTier)
    {
        bool same = body != 0 && body != v && fieldClass != 0 && Ptr(body + ClassAt) == fieldClass;
        if ((same ? body : 0) != actingBody)
        {
            actingBody = same ? body : 0;
            Say(same ? "RA01 game anchor: weapon, melee and grenade now read from the stand-in body you're in (M54-2)" : "RA01 game anchor: weapon, melee and grenade read from V again (M54-2)");
        }
        if (!same || scenePlace < 0) { lastStandInTier = -1; return vTier; }
        if (!ReadField(scenePlace, sceneInHolder, out long read, acting: true)) return vTier;
        int tier = (int)(read & 0xFF);
        if (tier != lastStandInTier)
        {
            lastStandInTier = tier;
            Say($"RA01 game anchor: stand-in body's own scene tier {tier} (parked V's is {vTier}) - VRIK goes by the stand-in's (M54)");
        }
        return tier;
    }

    // RA01: M58 - true while you look through a camera that isn't anyone's body: a taken-over security camera (CameraNotV) - but never a body you control (OnStandIn:
    // RA01: Johnny, the "Practice Makes Perfect" training avatar). Actions lets LB/RB and LT reach the game there (the scanner is LB).
    // RA01: M71 - also true for the whole of a braindance (BlackboardProbe.InBraindance): a braindance hands the camera to a stand-in body too, which switched this
    // RA01: off a few seconds in, and physical reload then kept LB and LT (the braindance editor's controls) from the game (Rob, The Information, 2026-10-13).
    // RA01: Cost - CPU: extremely low (three reads); GPU: none.
    public static bool LookingThroughCamera => (CameraNotV && !OnStandIn) || BlackboardProbe.InBraindance;

    // RA01: M54-3 - true while you're in a same-class stand-in body (Johnny Silverhand's, in his flashbacks): Actions' crosshair rule hides the crosshair then.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static bool InJohnnysBody => Working && actingBody != 0;

    // RA01: M54-2 - the body whose hand RA01 reads: the same-class stand-in you're in (Johnny), else V. Cost - CPU: extremely low (one read and a compare); GPU: none.
    static nint Acting(nint v)
    {
        nint body = actingBody;
        return body != 0 && body != v ? body : v;
    }
    static readonly ulong PuppetName = NameCode("gamePuppet");

    // RA01: M20-6 - is this a person-type body (its class or a parent, up to 8, is gamePuppet)? Cost - CPU: extremely low (up to 9 pairs of safe reads); GPU: none.
    static bool IsPuppet(nint obj)
    {
        nint cls = Ptr(obj + ClassAt);
        for (int depth = 0; cls != 0 && depth < 9; depth++, cls = Ptr(cls + ParentAt))
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return false;
            if (name == PuppetName) return true;
        }
        return false;
    }

    // RA01: M15-2 - the same search into any array: an entity's animated components' skeletons.
    // Cost - CPU: extremely low (up to a few hundred small safe reads); GPU: none.
    static int SkeletonsOf(nint v, nint[] into)
    {
        int found = 0;
        nint list = Ptr(v + ComponentsAt);
        uint count = 0;
        if (list != 0 && Read(v + ComponentsAt + 0xC, &count, 4) && count <= MaxComponents)
            for (int i = 0; i < count && found < into.Length; i++)
            {
                nint component = Ptr(list + i * 16);
                if (component == 0 || ClassName(component) != AnimatedName) continue;
                nint skeleton = Ptr(Ptr(component + AnimationAt) + SkeletonAt);
                if (skeleton != 0) into[found++] = skeleton;
            }
        for (int i = found; i < into.Length; i++) into[i] = 0;
        return found;
    }

    // RA01: is this an object of class PlayerPuppet (or a class built on gamePlayerPuppet, checked up to 8 parents)?
    // Cost - CPU: extremely low (up to 9 pairs of 8-byte safe reads); GPU: none.
    static bool IsPlayer(nint obj)
    {
        nint cls = obj != 0 ? Ptr(obj + ClassAt) : 0;
        for (int depth = 0; cls != 0 && depth < 9; depth++, cls = Ptr(cls + ParentAt))
        {
            ulong name = 0;
            if (!Read(cls + NameAt, &name, 8)) return false;
            if (name == PlayerPuppetName || name == GamePlayerPuppetName) return true;
        }
        return false;
    }

    // RA01: an object's class name code, 0 if unreadable.
    // Cost - CPU: extremely low (two 8-byte safe reads); GPU: none.
    static ulong ClassName(nint obj)
    {
        nint cls = obj != 0 ? Ptr(obj + ClassAt) : 0;
        ulong name = 0;
        if (cls != 0) Read(cls + NameAt, &name, 8);
        return name;
    }

    // RA01: a pointer stored at 'at', 0 if unreadable or not plausibly a pointer.
    // Cost - CPU: extremely low (one 8-byte safe read); GPU: none.
    static nint Ptr(nint at)
    {
        nint value = 0;
        if (at < 0x10000 || !Read(at, &value, 8)) return 0;
        return value > 0x10000 && value < 0x7FFFFFFFFFFF ? value : 0;
    }

    // RA01: the game's name code: FNV-1a 64-bit over the name's bytes (as WeaponProbe.NameCode).
    // Cost - CPU: extremely low (a few letters, once at start-up); GPU: none.
    public static ulong NameCode(string name)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) { h ^= b; h *= 0x100000001B3; }
        return h;
    }

    // RA01: one log line, at most MaxLines this session (the anchor itself keeps working after).
    // Cost - CPU: low (one log line); GPU: none.
    static void Say(string line)
    {
        if (lines >= MaxLines) return;
        if (++lines == MaxLines) line += " (game anchor log line limit reached - it keeps working, quietly)";
        Log.Write(line);
    }

    // RA01: safe read - a bad address fails this one call instead of crashing the game.
    // Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got = 0;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }
}
