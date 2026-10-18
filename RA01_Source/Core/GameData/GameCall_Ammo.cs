namespace RA01.Core;

// RA01: M27-7a - THE MAGAZINE AMMO PROBE (Dev tab "Magazine ammo probe (log)", LOG ONLY; Rob 00:37: a dropped magazine leaves 1 round in the chamber, the rest
// lost or returned to the reserve - Weapons tab "Dropped magazine rounds"). Part of GameCall. Before the mod changes any ammo it reads what the game
// really offers, so the next step is built on facts, not guesses:
//  1. the magazine count the HUD shows right now (BlackboardProbe's whiteboard copy of UI_ActiveWeaponData.MagazineAmmoCount),
//  2. the fields of the game's own weapon messages "use up magazine rounds" (ConsumeMagazineAmmo) and "set ammo count" (SetAmmoCountEvent) - each
//     field's name, type and place in the message, read off the game's catalogue,
//  3. the inputs of the built-in functions the next step would use: sending a message to the gun (QueueEvent), and the inventory's give / take / count
//     ammo functions (TransactionSystem) and how to reach it (GameInstance.GetTransactionSystem).
// Nothing is called and nothing is changed - only the catalogue is read.
// Builder picture: before cutting into a wall, read the drawings - which pipes run where, and what size the fittings are.
// Cost - CPU: low, only when the row is clicked (a few dozen catalogue reads and name look-ups on the controller thread); GPU: none.
internal static unsafe partial class GameCall
{
    static volatile int ammoProbeWanted;
    const int ClassProps = 0x28, ClassAllProps = 0x118, ClassSize = 0x68, PropOffset = 0x20; // RA01: CClass: its own fields, the fuller list (with native ones), its size; CProperty: where a field sits (RED4ext SDK RTTITypes.hpp, CProperty.hpp)
    const int TypeGetNameSlot = 0x08;                   // RA01: CBaseRTTIType's vtable: GetName (returns the type's name code)

    // RA01: Dev tab: ask for one probe (it runs on the next controller read). Cost - CPU: extremely low (one store); GPU: none.
    public static void AmmoProbe()
    {
        ammoProbeWanted = 1;
        Log.Write("RA01 magazine ammo probe: asked for - it runs on the next controller read");
    }

    // RA01: from RefillTick, on the game's XInput thread, when asked. Never throws. Cost - CPU: low (see the class comment); GPU: none.
    static void AmmoProbeTick()
    {
        ammoProbeWanted = 0;
        try
        {
            Log.Write($"RA01 magazine ammo probe: the HUD's magazine count is {(BlackboardProbe.MagazineAmmo >= 0 ? BlackboardProbe.MagazineAmmo.ToString() : "not seen yet (fire one shot, then click again)")}");
            if (!CallsChecked()) { Log.Write("RA01 magazine ammo probe: the catalogue can't be read on this game build"); return; }
            nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
            if (!ReadPtr(rtti, out nint vtable) || vtable == 0) return;
            foreach (string[] names in new[] { new[] { "ConsumeMagazineAmmo", "gameweaponeventsConsumeMagazineAmmo" }, new[] { "SetAmmoCountEvent", "gameweaponeventsSetAmmoCountEvent" } })
            {
                nint cls = 0;
                foreach (string n in names) if ((cls = FindClass(rtti, vtable, n)) != 0) break;
                if (cls == 0) { Log.Write($"RA01 magazine ammo probe: {names[0]} - not in the catalogue"); continue; }
                ReadPtr(cls + ClassParent, out nint parent);
                ReadInt(cls + ClassSize, out int size);
                Log.Write($"RA01 magazine ammo probe: {NameOf(cls + ClassName)} ({size} bytes, parent {(parent == 0 ? "none" : NameOf(parent + ClassName))}) fields: {Fields(cls, ClassProps)}; all fields: {Fields(cls, ClassAllProps)}");
            }
            foreach ((string cls, string fn) in new[] { ("Entity", "QueueEvent"), ("Entity", "QueueEventForEntityID"), ("GameInstance", "GetTransactionSystem"),
                                                        ("TransactionSystem", "GiveItem"), ("TransactionSystem", "RemoveItem"), ("TransactionSystem", "GetItemQuantity"),
                                                        ("WeaponObject", "GetItemID"), ("ItemObject", "GetItemData") })
            {
                nint c = FindClass(rtti, vtable, cls);
                nint f = c == 0 ? 0 : Function(c, fn);
                Log.Write($"RA01 magazine ammo probe: {cls}.{(f == 0 ? fn + " - not found" : Describe(f, fn) + $" returns {ReturnName(f)}")}");
            }
        }
        catch (Exception e) { Log.Write($"RA01 magazine ammo probe: stopped ({e.Message})"); }
    }

    // RA01: M27-7b - ONE ROUND STAYS IN THE CHAMBER (Dev tab "Drop to 1 round (test)" first; then physical reloading, Rob 00:37 / 00:40). With n rounds showing
    // RA01: on the HUD (n > 1), the game's own weapon message "use up magazine rounds" (gameweaponeventsConsumeMagazineAmmo, amount = n - 1 - the probe found
    // RA01: it: 72 bytes, 'amount' a Uint16 at 0x40) is built and sent to V's gun, so the game takes the rounds out itself, the same way a shot does - the HUD
    // RA01: and the gun stay in step. Building it uses the game's own two helpers the RED4ext SDK names (cyberpunk2077_addresses.json, build 68AF45EA):
    // RA01:   rtti::ClassType::CreateObject (CClass::CreateInstance, exe+0x1471EC) - makes the message, zeroed and set up as the game would,
    // RA01:   Handle's constructor (exe+0x1456C0) - wraps it in the game's counted reference, which QueueEvent (built-in) takes.
    // RA01: Our own count on that reference is handed back afterwards (never below 1: if the gun has already dropped its own, the message - 72 bytes - is left).
    // RA01: 'returnRest' (Weapons tab "Dropped magazine rounds" = Back to reserve): the n - 1 rounds go back to V's ammo with the inventory's own GiveItem -
    // RA01: the gun's ammo type is its database value ".ammo"; V is GetPlayerSystem -> GetLocalPlayerMainGameObject. Both helpers' first bytes are checked
    // RA01: before use (a game update that moved them: logged, nothing done - Rehooking). Every step is logged.
    // RA01: Builder picture: instead of reaching into the machine to move the counter, fill in the machine's own order slip and drop it in its tray.
    // RA01: Cost - CPU: extremely low (about ten game calls, once per dropped magazine); GPU: none.
    static int CreateObjectAt => HookRegistry.Rva(HookRegistry.CreateObject);   // RA01: M43 - 2.31: 0x1471EC; another build: from the Rehooker (0 = unknown, then the byte checks fail)
    static int HandleCtorAt => HookRegistry.Rva(HookRegistry.HandleCtor);       // RA01: M43 - 2.31: 0x1456C0
    static readonly byte[] ExpectedCreateObject = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57 };
    static readonly byte[] ExpectedHandleCtor = { 0x48, 0x89, 0x5C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x48, 0x89, 0x7C, 0x24, 0x20, 0x55 };
    static volatile int chamberWanted;          // RA01: 0 idle, 1 asked for (the Dev test or a dropped magazine)
    static volatile string chamberWhy = "";

    // RA01: Dev tab test, or Actions' physical reload when a magazine leaves the gun: one round stays (on the next controller read). Cost - CPU: extremely low; GPU: none.
    // M27-7g - 'keep' = rounds left in the gun: 1 (a chambered round) or 0 (Actions: a revolver's cylinder or a break-open shotgun empties fully - Rob 01:16).
    public static void ChamberTest() { chamberWhy = "Dev test"; chamberKeep = 1; chamberWanted = 1; Log.Write("RA01 chambered round: test asked for - it runs on the next controller read"); }
    public static void KeepChamberedRound(string why, int keep = 1) { chamberWhy = why; chamberKeep = Math.Clamp(keep, 0, 1); chamberWanted = 1; }
    static volatile int chamberKeep = 1;

    // RA01: from RefillTick, on the game's XInput thread, when asked. Never throws. Cost - CPU: extremely low (see above); GPU: none.
    static void ChamberTick()
    {
        chamberWanted = 0;
        try
        {
            int n = BlackboardProbe.MagazineAmmo, keep = chamberKeep;
            if (n <= keep) { Log.Write($"RA01 chambered round ({chamberWhy}): the magazine shows {(n < 0 ? "nothing yet" : n.ToString())} - nothing to take out"); return; }
            if (!CallsChecked()) return;
            if (!Starts(ExeScan.Base + CreateObjectAt, ExpectedCreateObject) || !HandleCtorUsable())
            { Log.Write($"RA01 chambered round: the game's object helpers don't match - CreateObject {ExeScan.Bytes(ExeScan.Base + CreateObjectAt, 16)}, Handle {ExeScan.Bytes(ExeScan.Base + HandleCtorAt, 16)} (E9 or FF 25 first = another program hooked it) - nothing done"); return; }
            nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
            if (!ReadPtr(rtti, out nint vtable) || vtable == 0 || !GameAnchor.TryRightHandWeapon(out nint weapon) || weapon == 0) return;
            nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
            if (weaponClass == 0 || !IsA(weapon, weaponClass, out _)) { Log.Write("RA01 chambered round: V's right-hand item is not a weapon - nothing done"); return; }
            nint evClass = FindClass(rtti, vtable, "gameweaponeventsConsumeMagazineAmmo");
            int amountAt = FieldAt(evClass, "amount");
            ReadInt(evClass + ClassSize, out int size);
            if (evClass == 0 || amountAt < 0x10 || size < amountAt + 2 || size > 0x400) { Log.Write($"RA01 chambered round: the 'use up magazine rounds' message isn't as the probe saw it (size {size}, amount at 0x{amountAt:X}) - nothing done"); return; }
            nint ev = ((delegate* unmanaged<nint, uint, byte, nint>)(ExeScan.Base + CreateObjectAt))(evClass, (uint)size, 1);
            if (ev == 0) { Log.Write("RA01 chambered round: the game couldn't make the message"); return; }
            *(ushort*)(ev + amountAt) = (ushort)(n - keep);
            byte* handle = stackalloc byte[16]; new Span<byte>(handle, 16).Clear();
            ((delegate* unmanaged<byte*, nint, byte*>)(ExeScan.Base + HandleCtorAt))(handle, ev);
            void** a1 = stackalloc void*[1]; a1[0] = handle;
            bool sent = Native(rtti, vtable, weaponClass, "QueueEvent", weapon, a1, 1, null);
            GiveBack(handle);
            bool back = Tuning.DroppedRounds == 0;
            int gone = n - keep;
            string rest = !sent ? "" : back ? (GiveAmmo(rtti, vtable, weaponClass, weapon, gone, out string how) ? $"; {gone} returned to your reserve" : $"; returning {gone} to your reserve FAILED ({how})") : $"; {gone} lost with the magazine";
            Log.Write($"RA01 chambered round ({chamberWhy}): magazine {n} -> {keep} {(sent ? "asked of the gun" : "FAILED (QueueEvent)")}{rest}");
        }
        catch (Exception e) { Log.Write($"RA01 chambered round: stopped ({e.Message})"); }
    }

    // RA01: M27-7c - Rob's 01:03 log: Handle's constructor starts "E9 ..." - another program has put a 5-byte jump over its first instruction (mov [rsp+10h],rbx),
    // the rest is the game's own bytes. That constructor runs every time the game itself makes a counted reference - thousands of times a second, all
    // through that jump - so calling it exactly as the game does is as safe as any of the game's own calls. Accepted only when it is the game's bytes, or
    // a 5-byte jump followed by the game's remaining bytes; logged once. Cost - CPU: extremely low (one 16-byte read); GPU: none.
    static bool HandleCtorUsable()
    {
        if (Starts(ExeScan.Base + HandleCtorAt, ExpectedHandleCtor)) return true;
        byte* live = stackalloc byte[16];
        if (!Read(ExeScan.Base + HandleCtorAt, live, 16) || live[0] != 0xE9) return false;
        for (int i = 5; i < 16; i++) if (live[i] != ExpectedHandleCtor[i]) return false;
        if (!handleHookLogged) { handleHookLogged = true; Log.Write("RA01 chambered round: the game's Handle constructor carries another program's jump (as every game call to it does) - called the game's way, through it"); }
        return true;
    }
    static bool handleHookLogged;

    // RA01: gives V 'count' rounds of her gun's ammo type (TransactionSystem.GiveItem). 'how' = why not. Cost - CPU: extremely low (about seven game calls); GPU: none.
    static bool GiveAmmo(nint rtti, nint vtable, nint weaponClass, nint weapon, int count, out string how)
    {
        how = "";
        byte* item = stackalloc byte[32]; new Span<byte>(item, 32).Clear();
        if (!Native(rtti, vtable, weaponClass, "GetItemID", weapon, null, 0, item)) { how = "no item id"; return false; }
        byte* raw = stackalloc byte[16];
        ulong record = *(ulong*)item & 0xFFFFFFFFFF;
        if (!WeaponEventProbe.StoredRaw(record, ".ammo", raw, 16)) { how = "the gun's ammo type isn't in the database"; return false; }
        byte* ammo = stackalloc byte[16]; new Span<byte>(ammo, 16).Clear();
        *(ulong*)ammo = *(ulong*)(raw + 8) & 0xFFFFFFFFFF;                          // RA01: an ItemID: the ammo's TweakDBID,
        *(uint*)(ammo + 8) = 2;                                                     // RA01: the default seed (a plain stackable item, as ItemID.FromTDBID makes it)
        byte* game = stackalloc byte[32]; new Span<byte>(game, 32).Clear();
        byte* players = stackalloc byte[32]; new Span<byte>(players, 32).Clear();
        byte* player = stackalloc byte[32]; new Span<byte>(player, 32).Clear();
        byte* trans = stackalloc byte[32]; new Span<byte>(trans, 32).Clear();
        void** g1 = stackalloc void*[1]; g1[0] = game;
        nint gameClass = FindClass(rtti, vtable, "GameInstance");
        if (!Native(rtti, vtable, weaponClass, "GetGame", weapon, null, 0, game)
            || !Native(rtti, vtable, gameClass, "GetPlayerSystem", weapon, g1, 1, players) || *(nint*)players == 0
            || !ReadPtr(*(nint*)players + ScriptableClass, out nint psClass) || !Native(rtti, vtable, psClass, "GetLocalPlayerMainGameObject", *(nint*)players, null, 0, player) || *(nint*)player == 0
            || !Native(rtti, vtable, gameClass, "GetTransactionSystem", weapon, g1, 1, trans) || *(nint*)trans == 0 || !ReadPtr(*(nint*)trans + ScriptableClass, out nint tsClass))
        { GiveBack(players); GiveBack(player); GiveBack(trans); how = "V or the inventory couldn't be reached"; return false; }
        int amount = count; byte* noTags = stackalloc byte[16]; new Span<byte>(noTags, 16).Clear();
        byte ok = 0;
        void** a4 = stackalloc void*[4]; a4[0] = player; a4[1] = ammo; a4[2] = &amount; a4[3] = noTags;
        bool called = Native(rtti, vtable, tsClass, "GiveItem", *(nint*)trans, a4, 4, &ok);
        GiveBack(players); GiveBack(player); GiveBack(trans);
        if (!called || ok == 0) { how = called ? "the inventory said no" : "GiveItem failed"; return false; }
        return true;
    }

    // RA01: where a field of that name sits in the class's objects (-1 if not found). Cost - CPU: extremely low; GPU: none.
    static int FieldAt(nint cls, string name)
    {
        if (cls == 0 || !ReadPtr(cls + ClassProps, out nint entries) || !ReadInt(cls + ClassProps + 0xC, out int count) || count < 0 || count > 64) return -1;
        for (int i = 0; i < count; i++)
            if (ReadPtr(entries + i * 8, out nint prop) && prop != 0 && NameOf(prop + PropName) == name && ReadInt(prop + PropOffset, out int at)) return at;
        return -1;
    }

    // RA01: M27-8 - the catalogue half of the HMG crosshair probe (see CrosshairProbe.StartHmgProbe): the HMG's own crosshair class (Crosshair_Custom_HMG, in the
    // game's script names) - what it is built on (its parent classes) and its own functions, so the switch-off can be aimed at it. Log only.
    // Cost - CPU: low, once per click (a few catalogue look-ups on a background thread); GPU: none.
    public static void HmgCrosshairProbe()
    {
        CrosshairProbe.StartHmgProbe();
        Task.Run(() =>
        {
            try
            {
                if (!CallsChecked()) return;
                nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
                if (!ReadPtr(rtti, out nint vtable) || vtable == 0) return;
                foreach (string name in new[] { "Crosshair_Custom_HMG", "Crosshair_Power_Defender", "CrosshairGameController_Basic" })
                {
                    nint cls = FindClass(rtti, vtable, name);
                    if (cls == 0) { Log.Write($"RA01 HMG crosshair probe: {name} - not in the catalogue"); continue; }
                    var chain = new List<string>();
                    for (nint c = cls; c != 0 && chain.Count < 10; ) { chain.Add(NameOf(c + ClassName)); if (!ReadPtr(c + ClassParent, out c)) break; }
                    var own = new List<string>();
                    foreach (int list in new[] { ClassFuncs, ClassStaticFuncs })
                        if (ReadPtr(cls + list, out nint entries) && ReadInt(cls + list + 0xC, out int count) && count > 0 && count <= MaxFuncs)
                            for (int i = 0; i < count; i++) if (ReadPtr(entries + i * 8, out nint fn) && fn != 0) own.Add(NameOf(fn + FuncShortName));
                    Log.Write($"RA01 HMG crosshair probe: {name} is built on {string.Join(" -> ", chain)}; its own functions: {(own.Count > 0 ? string.Join(", ", own) : "none")}");
                }
            }
            catch (Exception e) { Log.Write($"RA01 HMG crosshair probe: catalogue part stopped ({e.Message})"); }
        });
    }

    // RA01: M27-9c - THE HMG CROSSHAIR, FOUND AND SWITCHED OFF (Rob 07:08 / 07:50; CrosshairProbe drives it, on foot only). The M27-8 probe showed the HMG's
    // crosshair manager (gameuiDriverCombatHUDGameController) already faded by the mod, yet its crosshair stays - the HMG's crosshair has its own small
    // controller (Crosshair_Custom_HMG, built on gameuiCrosshairBaseGameController) that sets its own look: the lamp has its own switch on the cord.
    // FindCrosshairControllers looks inside a manager for those controllers - its own slots, and one step further inside each game object it holds - using
    // safe reads only and checking only values that look like real game objects (their function table inside the game's exe, a readable class). Nothing called.
    // Cost - CPU: low, once per HMG pick-up, on the crosshair probe's background thread (about 10 000 safe reads, a few ms); GPU: none.
    internal static int FindCrosshairControllers(nint manager, nint[] found, int[] at1, int[] at2, List<string> names)
    {
        int n = 0;
        if (!CallsChecked() || manager <= 0x10000) return 0;
        for (int a = 0x40; a < CrosshairScan1 && n < found.Length; a += 8)
        {
            if (!ReadPtr(manager + a, out nint p) || p == manager || !GameObject(p, out nint cls)) continue;
            if (CrosshairClass(cls, out string name)) { if (Array.IndexOf(found, p, 0, n) < 0) { found[n] = p; at1[n] = a; at2[n] = -1; names.Add($"{name} at +0x{a:X}"); n++; } continue; }
            for (int b = 0x40; b < CrosshairScan2 && n < found.Length; b += 8)
                if (ReadPtr(p + b, out nint q) && q != manager && q != p && GameObject(q, out nint qcls) && CrosshairClass(qcls, out string qname) && Array.IndexOf(found, q, 0, n) < 0)
                { found[n] = q; at1[n] = a; at2[n] = b; names.Add($"{qname} at +0x{a:X} -> +0x{b:X} (inside a {NameOf(cls + ClassName)})"); n++; }
        }
        return n;
    }
    const int CrosshairScan1 = 0x400, CrosshairScan2 = 0x300;   // RA01: M27-9c - bytes looked through in the manager, and in each object it holds

    // RA01: M27-9c - true if 'p' looks like a live game object: aligned, its function table inside the game's exe, and a class (IScriptable +0x30) whose own table is
    // inside the exe too. Cost - CPU: extremely low (three safe reads); GPU: none.
    static bool GameObject(nint p, out nint cls)
    {
        cls = 0;
        nint lo = ExeScan.Base, hi = ExeScan.Base + (nint)ExeScan.ImageSize;
        return p > 0x10000 && (p & 7) == 0 && ReadPtr(p, out nint vt) && vt >= lo && vt < hi
               && ReadPtr(p + ScriptableClass, out cls) && cls > 0x10000 && ReadPtr(cls, out nint cvt) && cvt >= lo && cvt < hi;
    }

    // RA01: M27-9c - true if the class is a crosshair controller (gameuiCrosshairBaseGameController or built on it); 'name' = its own class name.
    // Cost - CPU: extremely low (up to ten name look-ups); GPU: none.
    static bool CrosshairClass(nint cls, out string name)
    {
        name = NameOf(cls + ClassName);
        for (int k = 0; cls != 0 && k < 10; k++)
        {
            if (NameOf(cls + ClassName) == "gameuiCrosshairBaseGameController") return true;
            if (!ReadPtr(cls + ClassParent, out cls)) break;
        }
        return false;
    }

    // RA01: M27-9c - shows or hides a crosshair controller's whole picture: its top widget (the game's own built-in GetRootWidget, asked once and kept in 'widget'),
    // then the game's own built-in SetVisible on it. Only ever called by CrosshairProbe on the game's own HUD thread, after it checked the controller is still in
    // its manager. False if a step can't be done (logged once by Native) - the caller stops trying then.
    // Cost - CPU: extremely low (one or two game calls, a few times a second while the HMG is held); GPU: none (one widget fewer to draw).
    internal static bool SetCrosshairShown(nint controller, ref nint widget, bool shown)
    {
        if (!CallsChecked()) return false;
        nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
        if (!ReadPtr(rtti, out nint vtable) || vtable == 0) return false;
        if (widget == 0)
        {
            if (!ReadPtr(controller + ScriptableClass, out nint cls) || cls == 0) return false;
            byte* root = stackalloc byte[16]; new Span<byte>(root, 16).Clear();
            if (!Native(rtti, vtable, cls, "GetRootWidget", controller, null, 0, root)) return false;
            widget = *(nint*)root;                                                  // RA01: a weak handle - kept for as long as the controller stays in its manager
            if (widget == 0) return false;
        }
        if (!GameObject(widget, out nint widgetClass)) { widget = 0; return false; }
        byte visible = shown ? (byte)1 : (byte)0;
        void** a1 = stackalloc void*[1]; a1[0] = &visible;
        return Native(rtti, vtable, widgetClass, "SetVisible", widget, a1, 1, null);
    }

    // RA01: a class's own fields as "name: type @offset", comma-separated. Cost - CPU: extremely low (a few safe reads and name look-ups per field); GPU: none.
    static string Fields(nint cls, int list)
    {
        if (!ReadPtr(cls + list, out nint entries) || !ReadInt(cls + list + 0xC, out int count) || count < 0 || count > 64) return "(unreadable)";
        if (count == 0) return "(none of its own)";
        var parts = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (!ReadPtr(entries + i * 8, out nint prop) || prop == 0) { parts.Add("?"); continue; }
            ReadPtr(prop, out nint type);
            ReadInt(prop + PropOffset, out int at);
            parts.Add($"{NameOf(prop + PropName)}: {TypeName(type)} @0x{at:X}");
        }
        return string.Join(", ", parts);
    }

    // RA01: a function's result type's name ("nothing" if none). Cost - CPU: extremely low; GPU: none.
    static string ReturnName(nint fn) => ReadPtr(fn + FuncReturn, out nint ret) && ret != 0 && ReadPtr(ret, out nint type) ? TypeName(type) : "nothing";

    // RA01: an RTTI type's name, by its own GetName (vtable slot TypeGetNameSlot). Cost - CPU: extremely low (one game call that only returns a stored name); GPU: none.
    static string TypeName(nint type)
    {
        if (type == 0 || !ReadPtr(type, out nint vt) || vt == 0 || !ReadPtr(vt + TypeGetNameSlot, out nint get) || get == 0) return "?";
        ulong code = 0;
        nint back = ((delegate* unmanaged<nint, ulong*, nint>)get)(type, &code);   // RA01: a member function returning a CName: MSVC hands it back through a hidden pointer
        if (code == 0 && back != (nint)(&code)) code = (ulong)back;                 // RA01: (in case it came back in the register instead)
        return NameOf((nint)(&code));
    }
}
