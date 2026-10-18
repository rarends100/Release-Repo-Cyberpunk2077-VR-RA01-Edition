using System.Runtime.InteropServices;
using System.Text;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M27-6c-1 - THE GAME'S OWN CATALOGUE (read-only). The game keeps a catalogue of every class and function it knows, filed by name (RED4ext calls it the RTTI
// system) - like a parts catalogue with an index. One small function hands it over (CRTTISystem::Get); from there a class is looked up by its script name,
// and each class lists its functions (name, flags, parameters). A second small function turns a name code back into text (CNamePool::Get).
// WHERE THEY ARE: the RED4ext SDK reference names them; its address list (ghidra_decomplie_cp2077_exe\cyberpunk2077_addresses.json, build 68AF45EA) places
// them at exe+0x2F1080 and exe+0x209580 for this game build. Both are checked before use: the exe's build stamp must match AND the first bytes at each
// address must be the ones capstone showed (Expected*). After a game update the check fails, the log says so, and nothing is called (the Rehooking tab,
// Batch E, will find them again).
// LAYOUTS (RED4ext SDK, RTTITypes.hpp / Functions.hpp / CProperty.hpp): a class - parent +0x10, name +0x18, functions +0x48, static functions +0x58 (each a
// list: entries pointer, then the count at +0xC); a function - short name +0x10, parameters +0x28 (a list of properties: name at +0x08), flags +0xA8
// (bit 0 native, bit 1 static). The catalogue's own GetClassByScriptName is its slot 0x108, GetClass its slot 0x10.
// Every read of the game's memory goes through ReadProcessMemory (a bad pointer fails one read, never crashes the game). The only calls made are the two
// small functions above and the catalogue's two look-ups - all read-only.
// M27-6c-2 - THE REFILL TEST (Dev tab "Refill test"): CALLING a game function, the first time the mod does. Rob's catalogue log (17:58) showed the weapon's
// own native StartReload(durationOverride) and StopReload(reloadStatus) - called ON the weapon - and the scripted IsMagazineEmpty(). The row only raises a flag;
// the calls run on the game's own XInput thread (a before-build slot, like every controller feature), never beside the game's scripts on another thread:
//  1. V's right-hand weapon (GameAnchor) is checked to really be a weapon: the class it records at +0x30 (IScriptable.nativeType) must be WeaponObject or
//     a child of it - walked up through parents with safe reads, no calls.
//  2. The reload status names and values are read from the game's own enum (gameweaponReloadStatus) and logged; "Standard" is the one used.
//  3. Logged: magazine empty?, total ammo, reload pending?  Then StartReload(0); RefillStopMs later StopReload(Standard); RefillAfterMs later the same three again.
//  M27-6c-2c - Rob: the HUD didn't refill. Now: StartReload(RefillSeconds) with 'pending' read right after, StopReload 2.5 s later (a reload stopped
//  early may count as cancelled), the magazine count itself (GetMagazineAmmoCount, a stand-alone game function) before and after, and every weapon function's name.
// HOW A CALL IS MADE (RED4ext SDK Stack.hpp / Utils-inl.hpp, ExecuteFunction): a small call record (CStack, 0x58 bytes) wearing the game's own CStack vtable
// (exe .rdata +0xAAB28 = RVA 0x2B37B28, its slot +0x28 writes the arguments out for the function), the weapon as its context, a list of {type, value}
// arguments (types read from the function's own parameter list) and a {type, value} for the result. Native functions go through the game's CallNative
// (RVA 0x20668A0), scripted ones through InternalCall (RVA 0x1BC104) - both checked by build stamp and first bytes like the catalogue functions.
// M27-6c-2b - Rob's first test: "the call functions' bytes don't match" (one of them is changed in the running game - not by RA01). Now each is checked on
// its own, the live bytes of a changed one are logged (a jump first = another program hooked it), and a call only goes through a matching one - the reload
// functions are all native, so only CallNative must match; IsMagazineEmpty (scripted) reads "?" if InternalCall doesn't.
// M27-5g - THE REAL REFILL (Rob 18:35: "that did it" - StartReload(0.1), StopReload(Standard) later, the HUD magazine refilled from the reserve, no animation,
// no sound): Actions\PhysicalReload.cs asks RequestRefill when a new magazine goes in; StopReload follows Tuning.RefillDelayMs later (Dev tab, default 500).
// If the build or the native call function doesn't match, RequestRefill says no and the insert taps X as before.
// Builder picture: the parts catalogue at the hardware counter - look up the part by name, read its spec sheet, touch nothing on the shelf.
// Cost - CPU: low, only when a Dev tab row is clicked (a few hundred safe reads and name look-ups; the refill test: a handful of game calls); GPU: none.
internal static unsafe partial class GameCall
{
    // RA01: M43 - the game functions' places come from the Rehooker (Core\GameData\HookRegistry.cs): on 2.31 exactly the old numbers (RTTI get 0x2F1080, name get
    // RA01: 0x209580, CallNative 0x20668A0, InternalCall 0x1BC104, stack table 0x2B37B28); on another build the address list's, and 0 if unknown. Each is still
    // RA01: checked by its first bytes (Ready, CallsChecked) before anything is called - that check, not the build stamp, is now the gate.
    static int RttiGetAt => HookRegistry.Rva(HookRegistry.RttiGet);
    static int NameGetAt => HookRegistry.Rva(HookRegistry.NameGet);
    static int CallNativeAt => HookRegistry.Rva(HookRegistry.CallNative);
    static int CallScriptedAt => HookRegistry.Rva(HookRegistry.CallScripted);
    static int StackVtableAt => HookRegistry.Rva(HookRegistry.StackVtable);
    static readonly byte[] ExpectedCallNative = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x7C, 0x24, 0x10, 0x55, 0x48, 0x8D, 0xAC }; // RA01: mov [rsp+8],rbx; mov [rsp+10],rdi; push rbp
    static readonly byte[] ExpectedCallScripted = { 0x40, 0x55, 0x48, 0x81, 0xEC, 0xD0, 0x00, 0x00, 0x00, 0x48, 0x8D, 0x6C, 0x24, 0x40 }; // RA01: push rbp; sub rsp,D0; lea rbp,[rsp+40]
    const int SlotGetEnum = 0x18, ScriptableClass = 0x30, FuncReturn = 0x18, EnumNames = 0x28, EnumValues = 0x38, StackBytes = 0x58;
    const long RefillStopMs = 2500, RefillAfterMs = 600;   // RA01: M27-6c-2 - StopReload this long after StartReload (M27-6c-2c: 2.5 s, was 0.15 - the reload must finish first); the after-reading this long after that
    const float RefillSeconds = 0.1f;                      // RA01: M27-6c-2c - the reload length asked for (0 may mean 'the gun's own length')
    const int SlotGetFunction = 0x30, ScriptableSelfRef = 0x08; // RA01: M27-6c-2c - the catalogue's global-function look-up; an object's weak handle to itself (ISerializable)
    static volatile int refillPhase;                        // RA01: M27-6c-2 - 0 idle, 1 asked for, 2 started (waiting to stop), 3 stopped (waiting to read)
    static long refillAt;
    static nint refillWeapon;
    static readonly byte[] ExpectedRttiGet = { 0x48, 0x83, 0xEC, 0x28, 0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0x00, 0x00, 0x00 }; // RA01: sub rsp,28; mov rax,gs:[58]
    static readonly byte[] ExpectedNameGet = { 0x48, 0x83, 0xEC, 0x38, 0x48, 0x8B, 0x11, 0x48, 0x8D, 0x4C, 0x24, 0x20, 0xE8 }; // RA01: sub rsp,38; mov rdx,[rcx]; lea rcx,[rsp+20]; call
    const int SlotGetClass = 0x10, SlotGetClassByScriptName = 0x108;
    const int ClassParent = 0x10, ClassName = 0x18, ClassFuncs = 0x48, ClassStaticFuncs = 0x58;
    const int FuncShortName = 0x10, FuncParams = 0x28, FuncFlags = 0xA8, PropName = 0x08;
    const int MaxFuncs = 2000, MaxParams = 16;
    static readonly nint Self = GetCurrentProcess();
    static int busy;

    // RA01: the classes and the words the catalogue log looks for (Dev tab row "Game function catalogue (log)"): every function of the reload decision and
    // RA01: event classes, and the weapon's functions whose names mention reloading or ammunition.
    static readonly string[] AllOf = { "ReloadDecisions", "ReloadEvents" };
    static readonly string[] Matching = { "WeaponObject" };
    static readonly string[] Words = { "reload", "ammo", "magazine", "clip" };

    // RA01: Dev tab: logs the catalogue lines on a background thread (one run at a time). Cost - CPU: low (see the class comment); GPU: none.
    public static void LogCatalogue()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        Task.Run(() =>
        {
            try { Catalogue(); }
            catch (Exception e) { Log.Write($"RA01 game catalogue: stopped ({e.Message})"); }
            finally { busy = 0; }
        });
    }

    // RA01: M27-6c-2 - registers the refill test's before-build step. Call once at start-up, after XInputHook. Cost - CPU: extremely low (stores one pointer); GPU: none.
    public static void Install()
    {
        ControllerMapping.AddBeforeBuild(&RefillTick);
        ReportAddresses();   // RA01: M43
    }

    // RA01: M43 - tells the Rehooker, once at start-up, whether each game function is where this build keeps it AND starts with the expected bytes. Calls nothing.
    // Cost - CPU: extremely low (seven small safe reads); GPU: none.
    static void ReportAddresses()
    {
        Check(HookRegistry.RttiGet, RttiGetAt, ExpectedRttiGet);
        Check(HookRegistry.NameGet, NameGetAt, ExpectedNameGet);
        Check(HookRegistry.CallNative, CallNativeAt, ExpectedCallNative);
        Check(HookRegistry.CallScripted, CallScriptedAt, ExpectedCallScripted);
        Check(HookRegistry.CreateObject, CreateObjectAt, ExpectedCreateObject);
        if (HandleCtorAt != 0 && HandleCtorUsable()) HookRegistry.Found(HookRegistry.HandleCtor, ExeScan.Base + HandleCtorAt, "bytes checked");
        else if (HandleCtorAt != 0) HookRegistry.Off(HookRegistry.HandleCtor, "its first bytes don't match");
        nint table = StackVtableAt != 0 && ReadPtr(ExeScan.Base + StackVtableAt, out nint first) && first >= ExeScan.Base && first < ExeScan.Base + (nint)ExeScan.ImageSize ? first : 0;
        if (table != 0) HookRegistry.Found(HookRegistry.StackVtable, ExeScan.Base + StackVtableAt, "its first entry points into the game's code");
        else if (StackVtableAt != 0) HookRegistry.Off(HookRegistry.StackVtable, "its first entry doesn't point into the game's code");

        static void Check(int id, int rva, byte[] expected)
        {
            if (rva == 0) return;   // RA01: already reported OFF by HookRegistry.Rva
            if (Starts(ExeScan.Base + rva, expected)) HookRegistry.Found(id, ExeScan.Base + rva, "bytes checked");
            else HookRegistry.Off(id, "its first bytes don't match");
        }
    }

    // RA01: M27-6c-2 - Dev tab: ask for one refill test (it runs on the next controller read). Cost - CPU: extremely low (one store); GPU: none.
    public static void RefillTest()
    {
        if (refillPhase != 0) return;
        refillPhase = 1;
        Log.Write("RA01 refill test: asked for - it runs on the next controller read");
    }

    // RA01: M27-6c-2 - the refill test's steps, on the game's XInput thread (see the class comment). Never throws (ControllerMapping wraps it too).
    // Cost - CPU: extremely low when idle (one read); a handful of game calls during the one test; GPU: none.
    static void RefillTick()
    {
        long now = Environment.TickCount64;
        if (now >= ruleCheckAt) { ruleCheckAt = now + RuleCheckMs; UpdateReloadRule(); }   // RA01: M27-6c-3b - the reload rule follows the setting
        if (soundWanted != 0) SoundTick();                                              // RA01: M27-6g-1 - the gun sound probe (GameCall_Sound.cs)
        SoundQueueTick();                                                                // RA01: M27-6g-2 - the reload's sounds, when due
        if (ammoProbeWanted != 0) AmmoProbeTick();                                       // RA01: M27-7a - the magazine ammo probe (GameCall_Ammo.cs)
        if (chamberWanted != 0) ChamberTick();                                           // RA01: M27-7b - one round stays in the chamber
        int phase = refillPhase;
        if (phase == 0) return;
        try
        {
            if (phase == 1)
            {
                refillPhase = 0;
                if (!Ready(out string why)) { Log.Write($"RA01 refill test: not run - {why}"); return; }
                nativeOk = Starts(ExeScan.Base + CallNativeAt, ExpectedCallNative);                     // RA01: M27-6c-2b - checked one by one: natives need only CallNative
                scriptedOk = Starts(ExeScan.Base + CallScriptedAt, ExpectedCallScripted);
                if (!nativeOk || !scriptedOk)
                    Log.Write($"RA01 refill test: live bytes - CallNative {ExeScan.Bytes(ExeScan.Base + CallNativeAt, 16)} ({(nativeOk ? "as expected" : "CHANGED")}), " +
                              $"InternalCall {ExeScan.Bytes(ExeScan.Base + CallScriptedAt, 16)} ({(scriptedOk ? "as expected" : "CHANGED")}) - E9 or FF 25 first = another program hooked it");
                if (!nativeOk) { Log.Write("RA01 refill test: not run - CallNative doesn't match (the reload functions are all native)"); return; }
                nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
                if (!ReadPtr(rtti, out nint vtable) || vtable == 0) { Log.Write("RA01 refill test: the catalogue can't be read"); return; }
                if (!GameAnchor.TryRightHandWeapon(out nint weapon) || weapon == 0) { Log.Write("RA01 refill test: V holds no weapon in her right hand"); return; }
                nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
                string actual = "unknown";
                if (weaponClass == 0 || !IsA(weapon, weaponClass, out actual)) { Log.Write($"RA01 refill test: V's right-hand item is not a weapon ({actual}) - nothing called"); return; }
                LogEnum(rtti, vtable, "gameweaponReloadStatus", out standard);
                LogAllNames(weaponClass);                                                             // RA01: M27-6c-2c - every function the weapon has, names only
                refillWeapon = weapon;
                Log.Write($"RA01 refill test: weapon 0x{weapon:X} ({actual}) before - {State(rtti, vtable, weaponClass, weapon)}");
                bool ok = Call(rtti, vtable, weaponClass, weapon, "StartReload", RefillSeconds, 0);
                string pending = CallFor(rtti, vtable, weaponClass, weapon, "HasPendingReload", out long p) ? (p != 0 ? "yes" : "no") : "?";
                Log.Write($"RA01 refill test: StartReload({RefillSeconds}) {(ok ? "called" : "FAILED")} - reload pending right after: {pending}; StopReload in {RefillStopMs / 1000.0:F1} s");
                if (ok) { refillAt = now + RefillStopMs; refillPhase = 2; }
            }
            else if (phase == 2 && now >= refillAt)
            {
                nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
                ReadPtr(rtti, out nint vtable);
                nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
                bool same = GameAnchor.TryRightHandWeapon(out nint weapon) && weapon == refillWeapon;
                bool ok = same && Call(rtti, vtable, weaponClass, weapon, "StopReload", 0f, standard);
                Log.Write($"RA01 refill test: StopReload(Standard = {standard}) {(!same ? "skipped - the weapon changed" : ok ? "called" : "FAILED")}");
                refillAt = now + RefillAfterMs; refillPhase = same ? 3 : 0;
            }
            else if (phase == 11)                                                        // RA01: M27-5g - the real refill (PhysicalReload's insert): start
            {
                refillPhase = 0;
                if (!CallsChecked()) return;                                             // RA01: logged once by CallsChecked
                if (!PrepareRefill(out nint rtti, out nint vtable, out nint weaponClass, out nint weapon, out string why)) { Log.Write($"RA01 refill: not done - {why}"); return; }
                int shows = BlackboardProbe.MagazineAmmo;
                bool wentEmpty = BlackboardProbe.TakeSawEmpty();                         // RA01: M81 - seen empty since the last refill (taken here either way)
                refillNote = $"magazine read {shows}, went empty since the last refill: {(wentEmpty ? "yes" : "no")} - reload door {(shows == 0 || wentEmpty ? "opened" : "shut")} (M81)";
                if (shows == 0 || wentEmpty)                                             // RA01: M48-2 - an EMPTY gun: the game's reload door opens for this refill only (M81: or one seen empty)
                {
                    refillDoorUntil = now + Math.Clamp(Tuning.RefillDelayMs, 100, 3000) + RefillDoorExtraMs;
                    UpdateReloadRule();                                                  // RA01: open now, before StartReload, so the gun can take the game's own Reload state
                }
                bool ok = Call(rtti, vtable, weaponClass, weapon, "StartReload", RefillSeconds, 0);
                if (!ok) { Log.Write("RA01 refill: StartReload failed"); return; }
                refillWeapon = weapon; refillAt = now + Math.Clamp(Tuning.RefillDelayMs, 100, 3000); refillPhase = 12;
            }
            else if (phase == 12 && now >= refillAt)                                     // RA01: M27-5g - ...and finish it (the bullets go in here)
            {
                refillPhase = 0;
                nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
                ReadPtr(rtti, out nint vtable);
                nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
                bool same = GameAnchor.TryRightHandWeapon(out nint weapon) && weapon == refillWeapon;
                bool ok = same && Call(rtti, vtable, weaponClass, weapon, "StopReload", 0f, standard);
                Log.Write($"RA01 refill: {(!same ? "skipped - the weapon changed before it finished" : ok ? $"magazine refilled by the game's own reload ({Tuning.RefillDelayMs} ms)" : "StopReload failed")}; {refillNote}");
                // RA01: M27-5g - THE SOUND SLOT: the game's own "magazine in" sound event plays here once its name is found (WolvenKit, the game's sound event list).
            }
            else if (phase == 3 && now >= refillAt)
            {
                refillPhase = 0;
                nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
                ReadPtr(rtti, out nint vtable);
                nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
                if (GameAnchor.TryRightHandWeapon(out nint weapon) && weapon == refillWeapon)
                    Log.Write($"RA01 refill test: after - {State(rtti, vtable, weaponClass, weapon)} (look at the HUD ammo counter too)");
            }
        }
        catch (Exception e) { refillPhase = 0; Log.Write($"RA01 refill test: stopped ({e.Message})"); }
    }
    static long standard;
    static bool standardKnown;
    static string refillNote = "";               // RA01: M81 - what the refill's start read (magazine count, seen empty, door) - for its log line

    // RA01: M27-5g - Actions' physical reload: refill V's gun the game's own way (StartReload, then StopReload(Standard) Tuning.RefillDelayMs later) on the next
    // controller read. False (nothing asked) if a test or refill is already running, or the game build / call function doesn't match - then the caller taps X.
    // Cost - CPU: extremely low (a few reads and one store; the calls run on the XInput thread); GPU: none.
    public static bool RequestRefill()
    {
        if (refillPhase != 0 || !CallsChecked()) return false;
        refillPhase = 11;
        return true;
    }

    // RA01: M27-5g - the build and the native call function checked once (the scripted one isn't needed). Cost - CPU: extremely low (cached after the first check); GPU: none.
    static bool CallsChecked()
    {
        if (callsChecked != 0) return callsChecked > 0;
        bool ok = Ready(out _) && Starts(ExeScan.Base + CallNativeAt, ExpectedCallNative);
        nativeOk = ok;
        scriptedOk = ok && Starts(ExeScan.Base + CallScriptedAt, ExpectedCallScripted);
        callsChecked = ok ? 1 : -1;
        if (!ok) Log.Write("RA01 refill: the game's own reload can't be called on this game build - inserting a magazine taps X instead");
        return ok;
    }
    static int callsChecked;     // RA01: M27-5g - 0 not checked, 1 fine, -1 not usable

    // RA01: M27-5g - the catalogue, the weapon class and V's right-hand weapon (checked to be a weapon), and the "Standard" reload status (read once).
    // Cost - CPU: extremely low (a few reads and look-ups); GPU: none.
    static bool PrepareRefill(out nint rtti, out nint vtable, out nint weaponClass, out nint weapon, out string why)
    {
        why = ""; weaponClass = 0; weapon = 0; vtable = 0;
        rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
        if (!ReadPtr(rtti, out vtable) || vtable == 0) { why = "the catalogue can't be read"; return false; }
        if (!GameAnchor.TryRightHandWeapon(out weapon) || weapon == 0) { why = "V holds no weapon in her right hand"; return false; }
        weaponClass = FindClass(rtti, vtable, "WeaponObject");
        if (weaponClass == 0 || !IsA(weapon, weaponClass, out string actual)) { why = "V's right-hand item is not a weapon"; return false; }
        if (!standardKnown) { LogEnum(rtti, vtable, "gameweaponReloadStatus", out standard); standardKnown = true; }
        return true;
    }

    // RA01: M27-6c-2 - "magazine empty: no, total ammo: 210, reload pending: no". Cost - CPU: extremely low (three game calls); GPU: none.
    static string State(nint rtti, nint vtable, nint weaponClass, nint weapon)
    {
        string Value(string fn, bool flag) => CallFor(rtti, vtable, weaponClass, weapon, fn, out long v) ? (flag ? (v != 0 ? "yes" : "no") : v.ToString()) : "?";
        string magazine = MagazineCount(rtti, vtable, weapon, out long m) ? m.ToString() : "?";
        return $"magazine: {magazine}, magazine empty: {Value("IsMagazineEmpty", true)}, total ammo: {Value("GetTotalAmmoCount", false)}, reload pending: {Value("HasPendingReload", true)}";
    }

    // RA01: M27-6c-2c - the bullets in the magazine, from the game's GetMagazineAmmoCount(weapon) (a stand-alone function: its script name is
    // "GetMagazineAmmoCount;WeaponObject"). The weapon is passed as the game passes it - a weak handle (the object's own handle to itself, at +0x08, used only
    // if it really points back at the weapon). Called only if it is native (CallNative is the call function that matched). Logged once where it was found.
    // Cost - CPU: extremely low (one look-up and one game call); GPU: none.
    static bool MagazineCount(nint rtti, nint vtable, nint weapon, out long count)
    {
        count = 0;
        if (magazineFn == 0 && !magazineLooked)
        {
            magazineLooked = true;
            if (ReadPtr(vtable + SlotGetFunction, out nint get) && get != 0)
                foreach (string name in new[] { "GetMagazineAmmoCount;WeaponObject", "WeaponObject::GetMagazineAmmoCount", "GetMagazineAmmoCount" })
                {
                    magazineFn = ((delegate* unmanaged<nint, ulong, nint>)get)(rtti, NameCode(name));
                    if (magazineFn != 0) { ReadInt(magazineFn + FuncFlags, out int f); Log.Write($"RA01 refill test: found {name} - {Describe(magazineFn, NameOf(magazineFn + FuncShortName))} ({((f & 1) != 0 ? "native" : "scripted")})"); break; }
                }
            if (magazineFn == 0) Log.Write("RA01 refill test: GetMagazineAmmoCount not found as a stand-alone function");
        }
        if (magazineFn == 0 || !ReadPtr(weapon + ScriptableSelfRef, out nint self) || self != weapon) return false;
        if (!ReadPtr(magazineFn + FuncParams, out nint ps) || !ReadInt(magazineFn + FuncParams + 0xC, out int n) || n != 1 || !ReadPtr(ps, out nint prop) || !ReadPtr(prop, out nint type)) return false;
        if (!ReadPtr(magazineFn + FuncReturn, out nint ret) || ret == 0 || !ReadPtr(ret, out nint retType)) return false;
        long buffer = 0;
        nint* result = stackalloc nint[2]; result[0] = retType; result[1] = (nint)(&buffer);
        nint* arg = stackalloc nint[2]; arg[0] = type; arg[1] = weapon + ScriptableSelfRef;  // RA01: the weak handle's 16 bytes, as they sit in the weapon
        if (!Run(rtti, vtable, magazineFn, 0, arg, 1, result)) return false;
        count = buffer & 0xFFFFFFFF;
        return true;
    }
    static nint magazineFn;
    static bool magazineLooked;

    // RA01: M27-6c-2c - every function name of a class (not its parents), a few lines of names. Logged once a session. Cost - CPU: low (once); GPU: none.
    static void LogAllNames(nint cls)
    {
        if (namesLogged) return;
        namesLogged = true;
        var names = new List<string>();
        foreach (int list in new[] { ClassFuncs, ClassStaticFuncs })
        {
            if (!ReadPtr(cls + list, out nint entries) || !ReadInt(cls + list + 0xC, out int count) || count < 0 || count > MaxFuncs) continue;
            for (int i = 0; i < count; i++) if (ReadPtr(entries + i * 8, out nint fn) && fn != 0) names.Add(NameOf(fn + FuncShortName) + (list == ClassStaticFuncs ? "(static)" : ""));
        }
        for (int i = 0; i < names.Count; i += 30) Log.Write($"RA01 refill test: weapon functions {i + 1}-{Math.Min(i + 30, names.Count)}: {string.Join(", ", names.Skip(i).Take(30))}");
    }
    static bool namesLogged;

    // RA01: M27-6c-2 - true if the object's recorded class is 'cls' or a child of it ('actual' = its class name). Safe reads only.
    // Cost - CPU: extremely low (a few safe reads); GPU: none.
    static bool IsA(nint obj, nint cls, out string actual)
    {
        actual = "unreadable";
        if (!ReadPtr(obj + ScriptableClass, out nint c) || c == 0) return false;
        actual = NameOf(c + ClassName);
        for (int i = 0; i < 16 && c != 0; i++) { if (c == cls) return true; if (!ReadPtr(c + ClassParent, out c)) return false; }
        return false;
    }

    // RA01: M27-6c-2 - logs an enum's names and values from the game (and finds "Standard"). Cost - CPU: extremely low (a few reads and name look-ups); GPU: none.
    static void LogEnum(nint rtti, nint vtable, string name, out long standardValue)
    {
        standardValue = 0;
        if (!ReadPtr(vtable + SlotGetEnum, out nint get) || get == 0) return;
        nint en = ((delegate* unmanaged<nint, ulong, nint>)get)(rtti, NameCode(name));
        if (en == 0 || !ReadPtr(en + EnumNames, out nint names) || !ReadInt(en + EnumNames + 0xC, out int count) || !ReadPtr(en + EnumValues, out nint values) || count <= 0 || count > 64) { Log.Write($"RA01 refill test: enum {name} not found"); return; }
        var parts = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string n = NameOf(names + i * 8);
            ReadPtr(values + i * 8, out nint v);
            parts.Add($"{n} = {(long)v}");
            if (n == "Standard") standardValue = (long)v;
        }
        Log.Write($"RA01 refill test: {name}: {string.Join(", ", parts)}");
    }

    // RA01: M27-6c-2 - the function of that name on the class or a parent (null if none). Cost - CPU: extremely low (a few dozen safe reads and name look-ups); GPU: none.
    static nint Function(nint cls, string name)
    {
        for (int level = 0; level < 8 && cls != 0; level++)
        {
            foreach (int list in new[] { ClassFuncs, ClassStaticFuncs })
            {
                if (!ReadPtr(cls + list, out nint entries) || !ReadInt(cls + list + 0xC, out int count) || count < 0 || count > MaxFuncs) continue;
                for (int i = 0; i < count; i++)
                    if (ReadPtr(entries + i * 8, out nint fn) && fn != 0 && NameOf(fn + FuncShortName) == name) return fn;
            }
            if (!ReadPtr(cls + ClassParent, out cls)) break;
        }
        return 0;
    }

    // RA01: M27-6c-2 - calls a no-argument function on the object and reads its result (a bool or a number) into 'value'.
    // Cost - CPU: extremely low (one game call); GPU: none.
    static bool CallFor(nint rtti, nint vtable, nint cls, nint obj, string name, out long value)
    {
        value = 0;
        nint fn = Function(cls, name);
        if (fn == 0 || !ReadPtr(fn + FuncReturn, out nint ret) || ret == 0 || !ReadPtr(ret, out nint retType)) return false;
        long buffer = 0;
        nint* result = stackalloc nint[2];
        result[0] = retType; result[1] = (nint)(&buffer);
        if (!Run(rtti, vtable, fn, obj, null, 0, result)) return false;
        value = buffer;
        return true;
    }

    // RA01: M27-6c-2 - calls a one-argument function (a Float, or an enum/number passed as 'number') with no result. Cost - CPU: extremely low (one game call); GPU: none.
    static bool Call(nint rtti, nint vtable, nint cls, nint obj, string name, float f, long number)
    {
        nint fn = Function(cls, name);
        if (fn == 0 || !ReadPtr(fn + FuncParams, out nint ps) || !ReadInt(fn + FuncParams + 0xC, out int count) || count != 1 || !ReadPtr(ps, out nint prop) || !ReadPtr(prop, out nint type))
        { Log.Write($"RA01 refill test: {name} not found or not one argument"); return false; }
        long box = 0;
        if (name == "StartReload") *(float*)&box = f; else box = number;
        nint* arg = stackalloc nint[2];
        arg[0] = type; arg[1] = (nint)(&box);
        return Run(rtti, vtable, fn, obj, arg, 1, null);
    }

    // RA01: M27-6c-2 - one call through the game: the call record (see the class comment), then CallNative or InternalCall by the function's native flag.
    // Cost - CPU: extremely low (one game call); GPU: none.
    static bool Run(nint rtti, nint vtable, nint fn, nint context, nint* args, int argCount, nint* result)
    {
        byte* stack = stackalloc byte[StackBytes];
        new Span<byte>(stack, StackBytes).Clear();
        *(nint*)stack = ExeScan.Base + StackVtableAt;
        *(nint*)(stack + 0x18) = context; *(nint*)(stack + 0x20) = context;
        *(nint*)(stack + 0x28) = FindClass(rtti, vtable, "IScriptable");
        *(nint*)(stack + 0x30) = (nint)args; *(int*)(stack + 0x38) = argCount; *(nint*)(stack + 0x40) = (nint)result;
        ReadInt(fn + FuncFlags, out int flags);
        if ((flags & 1) != 0 ? !nativeOk : !scriptedOk) return false;   // RA01: M27-6c-2b - never through a call function whose bytes didn't match
        return (flags & 1) != 0
            ? ((delegate* unmanaged<nint, byte*, byte>)(ExeScan.Base + CallNativeAt))(fn, stack) != 0
            : ((delegate* unmanaged<nint, byte*, nint, byte>)(ExeScan.Base + CallScriptedAt))(fn, stack, 0) != 0;
    }

    // RA01: M27-6c-2 - the two call functions still start with the bytes capstone showed. Cost - CPU: extremely low; GPU: none.
    static bool nativeOk, scriptedOk;   // RA01: M27-6c-2b - each call function checked on its own when the test starts

    // RA01: M27-6c-3a - THE RELOAD RULE PROBE (Dev tab "Reload rule probe (log)", read-only): every script function carries its own list of instructions (its
    // bytecode: CBaseFunction +0x78 CCompiledCode, whose buffer is a data pointer at +0x80 and a size at +0x88 - RED4ext SDK Functions.hpp / Buffer.hpp).
    // Logs the instructions of the reload decision (ReloadDecisions.ToReload) and of a few tiny known functions next to it, so the game's own numbers for
    // "return" and "false" can be read off, not guessed, before the block (6c-3b) swaps in a "return false" of our own. Changes nothing.
    // Cost - CPU: low, only when the row is clicked (a few look-ups and safe reads on a background thread); GPU: none.
    public static void LogReloadRule()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                if (!Ready(out string why)) { Log.Write($"RA01 reload rule probe: not used - {why}"); return; }
                nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
                if (!ReadPtr(rtti, out nint vtable) || vtable == 0) return;
                foreach ((string cls, string fn) in new[] { ("ReloadDecisions", "ToReload"), ("ReloadDecisions", "EnterCondition"), ("ReloadDecisions", "ToSemiAuto"),
                                                            ("WeaponTransition", "GetBurstTimeName"), ("WeaponTransition", "GetIsDelayFireName"), ("WeaponTransition", "IsHeavyWeaponEmpty"),
                                                            ("ReloadEvents", "IsCoolFirearmWeaponType") })
                {
                    nint c = FindClass(rtti, vtable, cls);
                    nint f = c == 0 ? 0 : Function(c, fn);
                    if (f == 0) { Log.Write($"RA01 reload rule probe: {cls}.{fn} - not found"); continue; }
                    ReadInt(f + FuncFlags, out int flags);
                    if (!ReadPtr(f + CodeData, out nint code) || !ReadInt(f + CodeSize, out int size) || code == 0 || size <= 0 || size > 100000)
                    { Log.Write($"RA01 reload rule probe: {cls}.{fn} ({((flags & 1) != 0 ? "native" : "script")}) - no instructions readable"); continue; }
                    int show = Math.Min(size, MaxCodeShown);
                    byte[] bytes = new byte[show];
                    fixed (byte* b = bytes) if (!Read(code, b, show)) { Log.Write($"RA01 reload rule probe: {cls}.{fn} - instructions at 0x{code:X} can't be read"); continue; }
                    Log.Write($"RA01 reload rule probe: {cls}.{fn} (function 0x{f:X}, {size} bytes at 0x{code:X}): {Convert.ToHexString(bytes)}{(size > show ? " ..." : "")}");
                }
            }
            catch (Exception e) { Log.Write($"RA01 reload rule probe: stopped ({e.Message})"); }
            finally { busy = 0; }
        });
    }
    const int CodeData = 0x80, CodeSize = 0x88, MaxCodeShown = 400;   // RA01: M27-6c-3a - CBaseFunction.bytecode (+0x78) + CCompiledCode.bytecode (+0x08): data, size

    // RA01: M27-6c-3b - THE RELOAD RULE BLOCK (approved 19:01; M27-6e: and EnterCondition, the reload state's own door). While Tuning.PhysicalReload is on, the game's own reload decision ReloadDecisions.ToReload (the rule
    // asked "reload now?" for X and for an empty gun) answers NO: its instructions are pointed at RA01's own three bytes 27 14 00 - "return false", end - read
    // off the game's own functions by the probe (6c-3a: 27 = return, as in GetBurstTimeName "27 0E <name> 00"; 14 = false, as in IsCoolFirearmWeaponType's
    // "27 14 00"; ToReload's own no-reload branches already hold "27 14"). The original pointer and size are kept and put back the moment the setting goes off.
    // Only swapped if ToReload still starts with the bytes the probe saw (ExpectedRule) and is RuleSize long - else logged, never touched (Rehooking). The
    // original instructions are never freed, so a decision the game is already running finishes on them. Nothing is hooked: like replacing one page of the
    // rulebook - the game reads the rule as always, and the rule now says "no reload". Vehicle-mounted guns and thrown knives have their own rules (untouched).
    // M30-2 - ON FOOT ONLY: in a vehicle (car or bike, driver or passenger) the page goes back in, so X reloads and an empty gun auto-reloads like the base
    // game; out of the vehicle it is swapped again (within RuleCheckMs) - the same safe swap as flipping the setting.
    // Cost - CPU: extremely low (a setting read every RuleCheckMs; a one-off look-up and two writes when the setting changes); GPU: none.
    static void UpdateReloadRule()
    {
        bool want = Tuning.PhysicalReload && !VehicleState.Mounted                // RA01: M30-2 - Rob 09:06: in a vehicle the game reloads as usual (X and auto-reload)
                    && Environment.TickCount64 >= refillDoorUntil;                // RA01: M48-2 - open while an empty-magazine refill runs (see refillDoorUntil)
        if (want == ruleBlocked || ruleFailed) return;
        if (!Ready(out string why)) { ruleFailed = true; Log.Write($"RA01 reload rule: left alone - {why}"); return; }
        if (ruleFn[0] == 0)
        {
            nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
            if (!ReadPtr(rtti, out nint vtable) || vtable == 0) return;
            nint cls = FindClass(rtti, vtable, "ReloadDecisions");
            for (int i = 0; i < RuleNames.Length; i++)
            {
                ruleFn[i] = cls == 0 ? 0 : Function(cls, RuleNames[i]);
                if (ruleFn[i] == 0) { ruleFailed = true; Log.Write($"RA01 reload rule: ReloadDecisions.{RuleNames[i]} not found - the game's own reload stays on"); return; }
            }
        }
        if (want)
        {
            for (int i = 0; i < RuleNames.Length; i++)                                  // RA01: M27-6e - all checked first: either both rules are switched, or neither
            {
                ReadPtr(ruleFn[i] + CodeData, out nint code); ReadInt(ruleFn[i] + CodeSize, out int size);
                if (code == 0 || size != RuleSizes[i] || !Starts(code, RuleStarts[i]))
                { ruleFailed = true; Log.Write($"RA01 reload rule: {RuleNames[i]} isn't the one the probe saw (size {size}) - left alone, the game's own reload stays on (needs Rehooking)"); return; }
                ruleOld[i] = code; ruleOldSize[i] = size;
            }
            if (ruleNew == 0) { ruleNew = Marshal.AllocHGlobal(16); for (int i = 0; i < ReturnFalse.Length; i++) ((byte*)ruleNew)[i] = ReturnFalse[i]; } // RA01: kept for the whole session
            for (int i = 0; i < RuleNames.Length; i++)
            {
                *(int*)(ruleFn[i] + CodeSize) = ReturnFalse.Length;
                *(nint*)(ruleFn[i] + CodeData) = ruleNew;                                 // RA01: one aligned 8-byte write - the game sees the old rule or the new, never half
            }
            BlockOtherDoors(true);                                                       // RA01: M27-6f - and every other weapon state's door into reloading (the empty-magazine auto-reload)
            ruleBlocked = true;
            Log.Write($"RA01 reload rule: the game's own reload is OFF (physical reloading on: {string.Join(" and ", RuleNames)} say no) - no auto-reload, X doesn't reload");
        }
        else
        {
            for (int i = 0; i < RuleNames.Length; i++)
                if (ReadPtr(ruleFn[i] + CodeData, out nint code) && code == ruleNew)
                {
                    *(nint*)(ruleFn[i] + CodeData) = ruleOld[i];
                    *(int*)(ruleFn[i] + CodeSize) = ruleOldSize[i];
                }
            BlockOtherDoors(false);
            ruleBlocked = false;
            Log.Write($"RA01 reload rule: the game's own reload is back ON ({(!Tuning.PhysicalReload ? "physical reloading off" : VehicleState.Mounted ? "in a vehicle - physical reloading is on foot only" : "for a moment - an empty-magazine refill takes the game's own Reload state (M48-2)")})");
        }
    }
    // RA01: M27-6e - the reload state's two doors (Rob 20:26: with only ToReload switched, X still reloaded - sound and bullets - through EnterCondition, the reload
    // RA01: state's own entry rule). Sizes and first bytes from the 6c-3a probe (build 68AF45EA); EnterCondition's third byte on is a live pointer, so only two are checked.
    static readonly string[] RuleNames = { "ToReload", "EnterCondition" };
    static readonly int[] RuleSizes = { 161, 902 };
    static readonly byte[][] RuleStarts = { new byte[] { 0x20, 0x34, 0x00, 0x24 }, new byte[] { 0x16, 0x18 } };
    static readonly byte[] ReturnFalse = { 0x27, 0x14, 0x00 };          // RA01: return, false, end
    const long RuleCheckMs = 500;
    static readonly nint[] ruleFn = new nint[2], ruleOld = new nint[2];
    static readonly int[] ruleOldSize = new int[2];
    static nint ruleNew;
    static long ruleCheckAt;
    static bool ruleFailed;
    static volatile bool ruleBlocked;
    // RA01: M48-2 - THE DOOR OPENS FOR AN EMPTY-MAGAZINE REFILL (Rob approved 2026-10-11): with the door shut, a refill of an EMPTY gun took the weapon state from
    // RA01: NoAmmo to 3 instead of the game's Reload (2), so the crosshair never passed its reload position (5) and some reticles (the Guillotine's) kept showing
    // RA01: "reloading" until a weapon switch (the M48-1 reload windows: the game's own reload goes weapon 4 -> 2 -> 5 and crosshair 5 -> 8). So while such a refill
    // RA01: runs - its delay plus RefillDoorExtraMs - the reload rule stands open, like a door wedged for one delivery, then shuts at the next check (RuleCheckMs).
    // RA01: Partial reloads, physical reloading off, and vehicles: unchanged. Cost - CPU: extremely low (one clock compare per rule check); GPU: none.
    static long refillDoorUntil;
    const long RefillDoorExtraMs = 1000;

    // RA01: M27-6f - THE OTHER DOORS (Rob 22:48: with the reload state's own two rules switched, an EMPTY gun still reloaded by itself). The weapon's state machine
    // moves into reloading from whichever state it is in (ready, shooting, semi/full auto, burst, charging, cycling a round, no ammo...), and each of those
    // states has its OWN "ToReload" rule - the empty-magazine one doesn't ask the reload state first. So every one of those is switched to "return false"
    // too, with the same swap and the same put-back. Safety: only classes that descend from WeaponTransition (the weapon's own states - never vehicle
    // turrets or thrown knives), only a scripted function named ToReload with the two usual inputs (state context, script interface), each once (a rule
    // shared through a parent is switched once). Each switched one is logged by name and size, so a game update that moves them shows up (Rehooking).
    // Builder picture: the reload room had its front door locked - now every side door from the other rooms is locked too.
    // Cost - CPU: extremely low (a one-off look-up of about twenty classes the first time; then two writes per door when the setting changes); GPU: none.
    static void BlockOtherDoors(bool block)
    {
        if (block && !doorsLooked)
        {
            doorsLooked = true;
            nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
            if (!ReadPtr(rtti, out nint vtable) || vtable == 0) { Log.Write("RA01 reload rule: the catalogue can't be read - the other weapon states' reload doors stay open"); return; }
            var found = new List<string>();
            foreach (string name in DoorClasses)
            {
                nint cls = FindClass(rtti, vtable, name);
                if (cls == 0 || !Descends(cls, "WeaponTransition")) continue;
                nint fn = Function(cls, "ToReload");
                if (fn == 0 || Array.IndexOf(ruleFn, fn) >= 0 || doorFn.Contains(fn)) continue;
                if (!ReadInt(fn + FuncFlags, out int flags) || (flags & 1) != 0) continue;                                       // RA01: native - not ours to touch
                if (!ReadInt(fn + FuncParams + 0xC, out int inputs) || inputs != 2 || !ReadPtr(fn + FuncReturn, out nint ret) || ret == 0) continue;
                if (!ReadPtr(fn + CodeData, out nint code) || code == 0 || !ReadInt(fn + CodeSize, out int size) || size < 3 || size > 20000) continue;
                doorFn.Add(fn); doorOld.Add(code); doorOldSize.Add(size);
                found.Add($"{name}.ToReload ({size})");
            }
            Log.Write(found.Count == 0 ? "RA01 reload rule: no other weapon state has its own reload door" : $"RA01 reload rule: also switching the other weapon states' reload doors: {string.Join(", ", found)}");
        }
        for (int i = 0; i < doorFn.Count; i++)
        {
            nint fn = doorFn[i];
            if (block)
            {
                if (!ReadPtr(fn + CodeData, out nint code) || code != doorOld[i]) continue;                                      // RA01: changed under us - leave it
                *(int*)(fn + CodeSize) = ReturnFalse.Length;
                *(nint*)(fn + CodeData) = ruleNew;
            }
            else if (ReadPtr(fn + CodeData, out nint code) && code == ruleNew)
            {
                *(nint*)(fn + CodeData) = doorOld[i];
                *(int*)(fn + CodeSize) = doorOldSize[i];
            }
        }
    }

    // RA01: M27-6f - true if the class or one of its parents has this name. Cost - CPU: extremely low (up to 12 safe reads and name look-ups); GPU: none.
    static bool Descends(nint cls, string name)
    {
        for (int level = 0; level < 12 && cls != 0; level++)
        {
            if (NameOf(cls + ClassName) == name) return true;
            if (!ReadPtr(cls + ClassParent, out cls)) break;
        }
        return false;
    }
    // RA01: M27-6f - the weapon state machine's states (names from the game's script bundle, build 68AF45EA); any that doesn't exist or has no ToReload is skipped.
    static readonly string[] DoorClasses =
    {
        "ReadyDecisions", "ShootDecisions", "SemiAutoDecisions", "FullAutoDecisions", "BurstDecisions", "ChargeDecisions", "ChargeMaxDecisions", "ChargeReadyDecisions",
        "CycleRoundDecisions", "CycleTriggerModeDecisions", "NoAmmoDecisions", "SafeDecisions", "PublicSafeDecisions", "PublicSafeToReadyDecisions", "NotReadyDecisions",
        "OverheatDecisions", "DischargeDecisions", "QuickMeleeDecisions", "EquippedDecisions", "ZoomBlockedDecisions", "AimDecisions"
    };
    static readonly List<nint> doorFn = new(), doorOld = new();
    static readonly List<int> doorOldSize = new();
    static bool doorsLooked;

    // RA01: M27-6c-3b - for Actions: true while the game's own reload is switched off (then X ejects instead). Cost - CPU: extremely low (a read); GPU: none.
    public static bool GameReloadBlocked => ruleBlocked;

    // RA01: the catalogue log itself. Cost - CPU: low (see the class comment); GPU: none.
    static void Catalogue()
    {
        if (!Ready(out string why)) { Log.Write($"RA01 game catalogue: not used - {why}"); return; }
        nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
        if (!ReadPtr(rtti, out nint vtable) || vtable == 0) { Log.Write("RA01 game catalogue: the catalogue object can't be read"); return; }
        Log.Write($"RA01 game catalogue: found at 0x{rtti:X} (build {ExeScan.TimeStamp:X})");
        foreach (string name in AllOf) LogClass(rtti, vtable, name, null);
        foreach (string name in Matching) LogClass(rtti, vtable, name, Words);
    }

    // RA01: one class and its parents (up to three): every function, or (words) only those whose names contain one of the words.
    // Cost - CPU: low (one look-up, then two safe reads and one name look-up per function); GPU: none.
    static void LogClass(nint rtti, nint vtable, string scriptName, string[]? words)
    {
        nint cls = FindClass(rtti, vtable, scriptName);
        if (cls == 0) { Log.Write($"RA01 game catalogue: class {scriptName} - not found"); return; }
        for (int level = 0; level < 4 && cls != 0; level++)
        {
            string native = NameOf(cls + ClassName);
            var lines = new List<string>();
            int total = 0;
            foreach (int list in new[] { ClassFuncs, ClassStaticFuncs })
            {
                if (!ReadPtr(cls + list, out nint entries) || !ReadInt(cls + list + 0xC, out int count) || count < 0 || count > MaxFuncs) continue;
                for (int i = 0; i < count; i++)
                {
                    if (!ReadPtr(entries + i * 8, out nint fn) || fn == 0) continue;
                    total++;
                    string shortName = NameOf(fn + FuncShortName);
                    if (words != null && !words.Any(w => shortName.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                    lines.Add(Describe(fn, shortName));
                }
            }
            Log.Write($"RA01 game catalogue: {(level == 0 ? scriptName : "  parent")} = {native} - {total} functions{(words != null ? $", {lines.Count} matching" : "")}");
            foreach (string line in lines) Log.Write($"RA01 game catalogue:     {line}");
            if (!ReadPtr(cls + ClassParent, out cls)) break;
        }
    }

    // RA01: "static native StartReload(weapon, duration)". Cost - CPU: extremely low (a few safe reads and name look-ups); GPU: none.
    static string Describe(nint fn, string shortName)
    {
        ReadInt(fn + FuncFlags, out int flags);
        var text = new StringBuilder();
        if ((flags & 2) != 0) text.Append("static ");
        text.Append((flags & 1) != 0 ? "native " : "script ").Append(shortName).Append('(');
        if (ReadPtr(fn + FuncParams, out nint entries) && ReadInt(fn + FuncParams + 0xC, out int count) && count > 0 && count <= MaxParams)
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(ReadPtr(entries + i * 8, out nint prop) && prop != 0 ? NameOf(prop + PropName) : "?");
            }
        return text.Append(')').ToString();
    }

    // RA01: the class with this script name (or, failing that, this native name). Cost - CPU: extremely low (one or two catalogue look-ups); GPU: none.
    static nint FindClass(nint rtti, nint vtable, string name)
    {
        ulong code = NameCode(name);
        if (ReadPtr(vtable + SlotGetClassByScriptName, out nint byScript) && byScript != 0)
        {
            nint cls = ((delegate* unmanaged<nint, ulong, nint>)byScript)(rtti, code);
            if (cls != 0) return cls;
        }
        return ReadPtr(vtable + SlotGetClass, out nint get) && get != 0 ? ((delegate* unmanaged<nint, ulong, nint>)get)(rtti, code) : 0;
    }

    // RA01: M27-6j - for other Core readers: the text of a name code, "" if this game build's name pool isn't the one checked (CallsChecked). Cost - CPU: extremely low; GPU: none.
    internal static string NameText(ulong code)
    {
        if (code == 0 || !CallsChecked()) return "";
        string text = NameOf((nint)(&code));
        return text == "?" || text.StartsWith('#') ? "" : text;
    }

    // RA01: the text of the name code stored at 'at' ("?" if it can't be read). Cost - CPU: extremely low (one safe read and one name-pool look-up); GPU: none.
    static string NameOf(nint at)
    {
        if (!ReadPtr(at, out nint raw)) return "?";
        ulong code = (ulong)raw;
        if (code == 0) return "None";
        nint text = ((delegate* unmanaged<ulong*, nint>)(ExeScan.Base + NameGetAt))(&code);
        return text == 0 ? $"#{code:X}" : Marshal.PtrToStringUTF8(text) ?? "?";
    }

    // RA01: true if this game build is the one the two addresses belong to and both still start with the expected bytes. Cost - CPU: extremely low; GPU: none.
    static bool Ready(out string why)
    {
        why = "";
        if (ExeScan.Base == 0) { why = "the exe isn't read yet"; return false; }
        if (RttiGetAt == 0 || NameGetAt == 0) { why = $"this game build ({ExeScan.TimeStamp:X}) has no address for the catalogue functions - see the Dev_Rehook tab"; return false; }   // RA01: M43 - was: build stamp must be 2.31
        if (!Starts(ExeScan.Base + RttiGetAt, ExpectedRttiGet) || !Starts(ExeScan.Base + NameGetAt, ExpectedNameGet)) { why = "the catalogue functions' bytes don't match"; return false; }
        return true;
    }

    // RA01: Cost - CPU: extremely low (one safe read and a compare); GPU: none.
    static bool Starts(nint at, byte[] expected)
    {
        byte* got = stackalloc byte[expected.Length];
        if (!Read(at, got, expected.Length)) return false;
        for (int i = 0; i < expected.Length; i++) if (got[i] != expected[i]) return false;
        return true;
    }

    static bool ReadPtr(nint at, out nint value) { nint v; bool ok = Read(at, &v, 8); value = ok ? v : 0; return ok; }
    static bool ReadInt(nint at, out int value) { int v; bool ok = Read(at, &v, 4); value = ok ? v : 0; return ok; }

    // RA01: safe read - a bad address fails this one call instead of crashing the game. Cost - CPU: extremely low (one ReadProcessMemory call); GPU: none.
    static bool Read(nint address, void* buffer, int bytes)
    {
        nuint got;
        return ReadProcessMemory(Self, address, buffer, (nuint)bytes, &got) && got == (nuint)bytes;
    }

    // RA01: the game's name code: FNV-1a 64-bit over the name's bytes (RED4ext CName; the same as WeaponProbe.NameCode). Cost - CPU: extremely low; GPU: none.
    static ulong NameCode(string name)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (byte b in Encoding.UTF8.GetBytes(name)) { hash ^= b; hash *= 0x100000001B3UL; }
        return hash;
    }
}
