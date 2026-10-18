using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: M27-6g-1 - THE GUN SOUND PROBE (Dev tab "Gun sound probe (log)"; Rob 23:04: the physical reload needs the game's own magazine sounds). Part of GameCall
// (same catalogue and the same call path). It uses the game's own BUILT-IN (native) functions only, through CallNative like the refill - never the
// script call function another program has hooked:
//   1. V's gun -> GetGame (the game itself)          2. the game -> GetAudioSystem (the game's one sound system: its PA system)
//   3. V's gun -> GetItemID -> the gun's record (TweakDBInterface.GetWeaponItemRecord) -> AudioName (what the game calls this gun's sounds)
//   4. the sound system -> Play(sound name, V's gun, no emitter): "<audio name>_mag_in" if the audio name looks like the sound list's
//      (w_gun_rifle_power_ajax...), else the Ajax's own, so playing itself is still heard.
// Every step is logged (with each function's inputs), so step 2 (the reload's sounds) is built on what the game really answers. The game hands back
// its sound system and the gun's record as counted references; each count we were given is handed back (never below 1 - those objects belong to the game).
// Builder picture: ask the gun which building it is in, ask the building for its PA system, look up the gun's name tag, then call the tag over the PA.
// Cost - CPU: extremely low, only when the row is clicked (about eight game calls on the controller thread); GPU: none.
internal static unsafe partial class GameCall
{
    static volatile int soundWanted;
    const string FallbackSound = "w_gun_rifle_power_ajax_mag_in";

    // RA01: Dev tab: ask for one probe (it runs on the next controller read). Cost - CPU: extremely low (one store); GPU: none.
    public static void SoundProbe()
    {
        soundWanted = 1;
        Log.Write("RA01 gun sound probe: asked for - it runs on the next controller read");
    }

    // RA01: from RefillTick, on the game's XInput thread, when asked. Never throws. Cost - CPU: extremely low (see the class comment); GPU: none.
    static void SoundTick()
    {
        soundWanted = 0;
        try
        {
            if (!CallsChecked()) { Log.Write("RA01 gun sound probe: not run - the game's call function can't be used on this build"); return; }
            nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
            if (!ReadPtr(rtti, out nint vtable) || vtable == 0) { Log.Write("RA01 gun sound probe: the catalogue can't be read"); return; }
            if (!GameAnchor.TryRightHandWeapon(out nint weapon) || weapon == 0) { Log.Write("RA01 gun sound probe: V holds no weapon in her right hand"); return; }
            nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
            if (weaponClass == 0 || !IsA(weapon, weaponClass, out string actual)) { Log.Write("RA01 gun sound probe: V's right-hand item is not a weapon - nothing called"); return; }

            byte* game = stackalloc byte[32]; new Span<byte>(game, 32).Clear();
            if (!Native(rtti, vtable, weaponClass, "GetGame", weapon, null, 0, game)) return;
            nint gameClass = FindClass(rtti, vtable, "GameInstance");
            byte* audio = stackalloc byte[32]; new Span<byte>(audio, 32).Clear();
            void** a1 = stackalloc void*[1]; a1[0] = game;
            if (!Native(rtti, vtable, gameClass, "GetAudioSystem", weapon, a1, 1, audio)) return;
            nint audioSystem = *(nint*)audio;
            ReadPtr(audioSystem + ScriptableClass, out nint audioClass);
            Log.Write($"RA01 gun sound probe: the game's sound system is 0x{audioSystem:X} ({(audioClass == 0 ? "?" : NameOf(audioClass + ClassName))})");

            probeRecord = 0;
            string audioName = GunAudioName(rtti, vtable, weaponClass, weapon);
            ModelFlats(probeRecord);                                      // RA01: M27-6h - the gun model hunt: the gun's own database values
            string sound = audioName.StartsWith("w_gun_", StringComparison.Ordinal) ? audioName + "_mag_in" : FallbackSound;
            Log.Write($"RA01 gun sound probe: {actual} - audio name '{audioName}' -> playing {sound}{(sound == FallbackSound ? " (the stand-in - the audio name doesn't look like the sound list's)" : "")}");

            byte* id = stackalloc byte[16]; new Span<byte>(id, 16).Clear();
            Native(rtti, vtable, weaponClass, "GetEntityID", weapon, null, 0, id);
            ulong eventCode = NameCode(sound), noEmitter = 0;
            void** a3 = stackalloc void*[3]; a3[0] = &eventCode; a3[1] = id; a3[2] = &noEmitter;
            bool played = audioSystem != 0 && audioClass != 0 && Native(rtti, vtable, audioClass, "Play", audioSystem, a3, 3, null);
            Log.Write($"RA01 gun sound probe: Play({sound}) {(played ? "called - did you hear it?" : "FAILED")}");
            GiveBack(audio);
        }
        catch (Exception e) { Log.Write($"RA01 gun sound probe: stopped ({e.Message})"); }
    }

    // RA01: M27-6g-2 - THE SOUND QUEUE (Rob 23:31: the probe's Play works; one sound per gun TYPE is plenty). Actions asks for a sound by its name, now or
    // 'delayMs' later (a dropped magazine lands after its fall); the controller thread plays each when due on V's gun, through the same built-in calls
    // as the probe (GetGame -> GetAudioSystem -> Play with the gun's EntityID). Core picks no sounds itself: what plays when is Actions' choice.
    // At most QueueMax waiting (a burst beyond that is dropped). A sound that can't be played is logged once per name; each name is logged the first time.
    // Cost - CPU: extremely low (a lock and an add per ask; four game calls per sound played); GPU: none.
    public static void PlaySound(string name, int delayMs = 0)
    {
        lock (queue) if (queue.Count < QueueMax) queue.Add((name, Environment.TickCount64 + Math.Max(0, delayMs)));
    }
    static readonly List<(string Name, long Due)> queue = new();
    static readonly HashSet<string> soundsSeen = new();
    const int QueueMax = 8;

    // RA01: from RefillTick: plays every queued sound that is due. Never throws. Cost - CPU: extremely low (see PlaySound); GPU: none.
    static void SoundQueueTick()
    {
        string? name = null;
        long now = Environment.TickCount64;
        lock (queue)
        {
            if (queue.Count == 0) return;
            for (int i = 0; i < queue.Count; i++) if (queue[i].Due <= now) { name = queue[i].Name; queue.RemoveAt(i); break; }
        }
        if (name == null) return;
        try
        {
            bool first = soundsSeen.Add(name);
            if (!CallsChecked()) return;
            nint rtti = ((delegate* unmanaged<nint>)(ExeScan.Base + RttiGetAt))();
            if (!ReadPtr(rtti, out nint vtable) || vtable == 0 || !GameAnchor.TryRightHandWeapon(out nint weapon) || weapon == 0) return;
            nint weaponClass = FindClass(rtti, vtable, "WeaponObject");
            if (weaponClass == 0 || !IsA(weapon, weaponClass, out _)) return;
            byte* game = stackalloc byte[32]; new Span<byte>(game, 32).Clear();
            byte* audio = stackalloc byte[32]; new Span<byte>(audio, 32).Clear();
            byte* id = stackalloc byte[16]; new Span<byte>(id, 16).Clear();
            void** a1 = stackalloc void*[1]; a1[0] = game;
            if (!Native(rtti, vtable, weaponClass, "GetGame", weapon, null, 0, game) || !Native(rtti, vtable, FindClass(rtti, vtable, "GameInstance"), "GetAudioSystem", weapon, a1, 1, audio)) return;
            nint audioSystem = *(nint*)audio;
            bool played = false;
            if (audioSystem != 0 && ReadPtr(audioSystem + ScriptableClass, out nint audioClass) && audioClass != 0 && Native(rtti, vtable, weaponClass, "GetEntityID", weapon, null, 0, id))
            {
                ulong eventCode = NameCode(name), noEmitter = 0;
                void** a3 = stackalloc void*[3]; a3[0] = &eventCode; a3[1] = id; a3[2] = &noEmitter;
                played = Native(rtti, vtable, audioClass, "Play", audioSystem, a3, 3, null);
            }
            GiveBack(audio);
            if (first) Log.Write($"RA01 gun sound: {name} {(played ? "played (first time)" : "could NOT be played")}");
        }
        catch (Exception e) { Log.Write($"RA01 gun sound: {name} stopped ({e.Message})"); }
    }

    // RA01: V's gun's audio name from its record ("" if any step fails, logged). Cost - CPU: extremely low (three game calls); GPU: none.
    static string GunAudioName(nint rtti, nint vtable, nint weaponClass, nint weapon)
    {
        byte* item = stackalloc byte[32]; new Span<byte>(item, 32).Clear();
        if (!Native(rtti, vtable, weaponClass, "GetItemID", weapon, null, 0, item)) return "";
        Log.Write($"RA01 gun sound probe: the gun's item id - record 0x{*(ulong*)item:X16}");
        probeRecord = *(ulong*)item & 0xFFFFFFFFFF;                             // RA01: M27-6h - for the model hunt
        byte* record = stackalloc byte[32]; new Span<byte>(record, 32).Clear();
        void** a1 = stackalloc void*[1]; a1[0] = item;                           // RA01: an ItemID starts with its record's TweakDBID (8 bytes)
        if (!Native(rtti, vtable, FindClass(rtti, vtable, "TweakDBInterface"), "GetWeaponItemRecord", weapon, a1, 1, record)) return "";
        nint rec = *(nint*)record;
        string name = "";
        if (rec != 0 && ReadPtr(rec + ScriptableClass, out nint recClass) && recClass != 0)
        {
            ulong code = 0;
            if (Native(rtti, vtable, recClass, "AudioName", rec, null, 0, &code)) name = NameOf((nint)(&code));
            nint cfg = Function(recClass, "AudioWeaponConfiguration");
            Log.Write($"RA01 gun sound probe: the gun's record 0x{rec:X} ({NameOf(recClass + ClassName)}) - AudioName '{name}'{(cfg != 0 ? $"; also has {Describe(cfg, "AudioWeaponConfiguration")}" : "")}");
        }
        else Log.Write("RA01 gun sound probe: the gun's record came back empty");
        GiveBack(record);
        return name;
    }

    // RA01: M27-6h - THE GUN MODEL HUNT (log only): the game's record for V's gun didn't come back through the record look-up, but the mod already reads the
    // game's database directly (WeaponEventProbe: that is how it knows the gun type and Smart). So this reads the gun's OWN stored values under the names
    // that should carry the model - its entity name, appearance names, audio name, friendly name, audio set - and logs each as a name, a text and an ID,
    // so the next step knows which one says "ajax", "unity", "saratoga"... Like reading every label on a crate to find the one with the model number.
    // Cost - CPU: extremely low (eight database look-ups and a few name-pool reads, only when the probe row is clicked); GPU: none.
    static void ModelFlats(ulong id)
    {
        if (id == 0) { Log.Write("RA01 gun model hunt: no record id"); return; }
        byte* raw = stackalloc byte[32];                                             // RA01: one buffer for all (a stackalloc inside the loop would grow the stack each pass - CA2014)
        foreach (string suffix in new[] { ".entityName", ".appearanceName", ".appearanceResourceName", ".audioName", ".friendlyName", ".audioWeaponConfiguration", ".visualTags", ".weaponAudio" })
        {
            new Span<byte>(raw, 32).Clear();
            if (!WeaponEventProbe.StoredRaw(id, suffix, raw, 32)) { Log.Write($"RA01 gun model hunt: {suffix[1..]} - none"); continue; }
            ulong v = *(ulong*)(raw + 8);
            string asName = NameOf((nint)(raw + 8));
            string asText = TextAt(raw + 8);
            Log.Write($"RA01 gun model hunt: {suffix[1..]} - raw {Convert.ToHexString(new ReadOnlySpan<byte>(raw, 32))}; as a name '{asName}'; as a text '{asText}'; as an ID 0x{v & 0xFFFFFFFFFF:X10}");
        }
    }
    static ulong probeRecord;          // RA01: M27-6h - the probed gun's record id (GetItemID), 0 = none

    // RA01: M27-6h - the game's text type (CString): short texts sit inside it, longer ones behind a pointer. Printable letters only, at most 64. Cost - CPU: extremely low; GPU: none.
    static string TextAt(byte* s)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < 0x14 && s[i] >= 0x20 && s[i] < 0x7F; i++) text.Append((char)s[i]);
        if (text.Length >= 3) return text.ToString();
        text.Clear();
        nint p = *(nint*)s;
        byte* buf = stackalloc byte[64];
        if (p != 0 && Read(p, buf, 64)) for (int i = 0; i < 64 && buf[i] >= 0x20 && buf[i] < 0x7F; i++) text.Append((char)buf[i]);
        return text.ToString();
    }

    // RA01: one BUILT-IN game function by name on 'cls' (or a parent), with these input values (each a pointer to the value, typed from the function's own
    // inputs) and its result written to 'result' (or none). Refuses scripted functions and a wrong input count (logged with the real inputs).
    // Cost - CPU: extremely low (a look-up and one game call); GPU: none.
    static bool Native(nint rtti, nint vtable, nint cls, string name, nint context, void** values, int count, void* result)
    {
        nint fn = cls == 0 ? 0 : Function(cls, name);
        if (fn == 0) { Log.Write($"RA01 gun sound: {name} not found"); return false; }
        ReadInt(fn + FuncFlags, out int flags);
        if ((flags & 1) == 0) { Log.Write($"RA01 gun sound: {Describe(fn, name)} is a script function - not called (built-in only)"); return false; }
        ReadPtr(fn + FuncParams, out nint ps); ReadInt(fn + FuncParams + 0xC, out int have);
        if (ps == 0 && have != 0 || have != count)
        { Log.Write($"RA01 gun sound: {Describe(fn, name)} takes {have} inputs, not {count} - not called"); return false; }
        nint* args = stackalloc nint[Math.Max(2, count * 2)];
        for (int i = 0; i < count; i++)
        {
            if (!ReadPtr(ps + i * 8, out nint prop) || prop == 0 || !ReadPtr(prop, out nint type) || type == 0) { Log.Write($"RA01 gun sound: {name}'s input {i} can't be read"); return false; }
            args[i * 2] = type; args[i * 2 + 1] = (nint)values[i];
        }
        nint* res = stackalloc nint[2];
        bool wantResult = result != null && ReadPtr(fn + FuncReturn, out nint ret) && ret != 0 && ReadPtr(ret, out res[0]) && res[0] != 0;
        res[1] = (nint)result;
        bool ok = Run(rtti, vtable, fn, context, args, count, wantResult ? res : null);
        if (!ok) Log.Write($"RA01 gun sound: {Describe(fn, name)} failed");
        return ok;
    }

    // RA01: hands back the count on a reference the game gave us (instance, then its count block: strong at +0) - never below 1, those objects are the game's.
    // Cost - CPU: extremely low (one read and one locked subtract); GPU: none.
    static void GiveBack(byte* handle)
    {
        nint counts = *(nint*)(handle + 8);
        if (counts == 0 || !ReadInt(counts, out int strong) || strong <= 1) return;
        Interlocked.Decrement(ref *(int*)counts);
        *(nint*)handle = 0; *(nint*)(handle + 8) = 0;
    }
}
