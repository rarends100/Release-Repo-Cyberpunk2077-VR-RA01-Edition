using System.Text;

namespace RA01.Core;

// RA01: M43 - THE REHOOKER: one table of every place RA01 hooks or reads inside the game's exe, and what became of each one this start.
// Each row: its name, the feature it serves, where 2.31 keeps it (RVA), its part number in CDPR's address list (a hash that stays the same from build
// to build) and how far into that function it sits, and which byte pattern checks it (file.name - claude_session_tools\RA01_HookCheck.py reads this
// table and those patterns to check a new exe offline).
// Each hook reports here: FOUND (where 2.31 had it), MOVED (found somewhere else: by its pattern, or by its part number and then its check bytes), or OFF.
// The report goes to Logs\RA01_Rehooker.txt (always written, the last run kept as _previous_<date>), the Dev_Rehook tab shows the same lines, and if any
// player feature is off a banner says so once at start-up.
// SAFETY RULE: a MOVED address is used only after the game's bytes there match the check bytes. On 2.31 every hook takes exactly the path it took before.
// Builder picture: the site foreman's clipboard - every fixing point on the drawing, ticked "where the drawing says", "moved, re-measured and checked",
// or "not fitted"; the stock index is only asked when the drawing is out of date.
// Cost - CPU: extremely low (a few list stores per hook at start-up, one small file write; a pattern that fails on a new build adds one short search inside
// the one function the address list names); GPU: none.
internal static unsafe class HookRegistry
{
    public const uint Build231 = 0x68AF45EA;   // RA01: the exe stamp of 2.31, the build every 2.31 RVA below belongs to (cyberpunk2077_addresses.json "Linker map timestamp")
    const int RoomBytes = 0x1000;              // RA01: how far into a function a mid-function spot is looked for on a new build
    public enum State : byte { NotChecked, Found, Moved, Off }

    // RA01: one row of the table. Hash 0 = not in the address list (a DLL, an import, a group of functions). Pattern "" = no byte pattern (found another way).
    // Cost - CPU: extremely low (a plain data holder, made once); GPU: none.
    public readonly record struct Row(string Name, string Feature, bool Dev, int Rva231, uint Hash, int InFunction, string Pattern);

    public const int CameraLocate = 0, CameraFinal = 1, CameraPatch = 2, Pose = 3, ThrowArc = 4, ThrowPath = 5, RayRunner = 6, MultiHit = 7, MultiHitReturn = 8,
        AimCallers = 9, PxRaycast = 10, PxSweep = 11, AimSource = 12, CrosshairUpdate = 13, CrosshairApply = 14, CrosshairFrame = 15, WeaponEvent = 16,
        Blackboard = 17, RenderCamera = 18, NodeAttach = 19, CullingGate = 20, DistanceScale = 21, RttiGet = 22, NameGet = 23, CallNative = 24, CallScripted = 25,
        StackVtable = 26, CreateObject = 27, HandleCtor = 28, Engine = 29, TweakDb = 30, XInput = 31, Present = 32,
        HudParent = 33, HudName = 34, HudScale = 35, HudPivot = 36, VehicleModel = 37, CrosshairSafeState = 38, CrosshairOpacity = 39,
        HoldIndicatorSafety = 40;   // RA01: M44 - fields found by name (FieldNames); their "2.31 place" is the field's place in the object

    public static readonly Row[] Rows =
    {
        new("Camera (stereo view)",            "VR camera",                       false, 0x127F97,  1692210232, 0x3F,  "CameraHook.LocateCamera"),
        new("Camera end (log only)",           "VR camera",                       true,  0x785578,  2624398555, 0xB8,  "CameraHook.FinalCamera"),
        new("Camera patch (log only)",         "VR camera",                       true,  0x1D8A8F,  4281144013, 0x537, "CameraHook.PatchCamera"),
        new("Body pose",                       "Body tracking (VRIK)",            false, 0x4B6968,  1343032985, 0,     "PoseHook.BuildBody"),
        new("Throw arc worker",                "Physical throw",                  false, 0x47323C,  3544784313, 0,     "ThrowLever.ArcHead"),
        new("Throw flight path",               "Physical throw",                  false, 0x84F9AC,  3327736640, 0,     "ThrowLever.PathHead"),
        new("Ray runner",                      "Gun aiming",                      false, 0x2389CC,  1462903155, 0,     "AimProbe.RayRunner"),
        new("Multi-hit ray runner",            "Gun aiming",                      false, 0x2397F8,  4039912614, 0,     "AimProbe.MultiHitRunner"),
        new("PhysX call in the runner",        "Gun aiming",                      false, 0x239935,  4039912614, 0x13D, ""),
        new("Aim line callers (3)",            "Gun aiming",                      false, 0x6F1FAF,  430713883,  0x43,  ""),
        new("PhysX raycast (PhysX3_x64.dll)",  "Gun aiming",                      false, 0x60890,   0,          0,     "AimProbe.PxRaycast"),
        new("PhysX sweep (PhysX3_x64.dll)",    "Gun aiming",                      false, 0x60960,   0,          0,     "AimProbe.PxSweep"),
        new("Shot aim source",                 "Shots from the gun",              false, 0x4D8354,  2604539728, 0,     "ShotProbe.AimSource"),
        new("Crosshair update",                "Crosshair control",               false, 0x5B8D28,  433331076,  0,     "CrosshairProbe.Visibility"),
        new("Crosshair apply",                 "Crosshair control",               false, 0x5B8C18,  3915519740, 0,     "CrosshairProbe.Apply"),
        new("Crosshair per-frame check",       "Crosshair control",               false, 0x1492600, 3117420914, 0,     "CrosshairProbe.ManagerBuilder"),
        new("Weapon event handler",            "Weapon detection",                false, 0x656288,  879305929,  0,     "WeaponEventProbe.Handler"),
        new("Blackboard setters (10)",         "Menu sensing",                    false, 0x32BD8C,  0,          0,     "BlackboardProbe.Setter"),
        new("Render camera",                   "Frame pairing",                   false, 0x623FD8,  1053501143, 0,     "RenderCameraProbe.Site"),
        new("World node attach",               "Room probe (Dev)",                true,  0x3C7C1A,  3515291207, 0x6,   "NodeProbe.AttachBody"),
        new("Occlusion switch",                "Culling lab (Dev)",               true,  0x79E52C,  1827017916, 0x20,  "CullingLab.GateBytes"),
        new("Distance stretch formula",        "Wide-view distance fix (test)",   true,  0x4E427B,  1697190968, 0x24B, "DistanceScaleFix.Head"),
        new("RTTI catalogue",                  "Game calls (refill, sounds)",     false, 0x2F1080,  1247874916, 0,     "GameCall.ExpectedRttiGet"),
        new("Name look-up",                    "Game calls (refill, sounds)",     false, 0x209580,  1759447004, 0,     "GameCall.ExpectedNameGet"),
        new("Call native function",            "Game calls (refill, sounds)",     false, 0x20668A0, 3778157291, 0,     "GameCall.ExpectedCallNative"),
        new("Call script function",            "Game calls (Dev tests)",          true,  0x1BC104,  3791200470, 0,     "GameCall.ExpectedCallScripted"),
        new("Call stack table",                "Game calls (refill, sounds)",     false, 0x2B37B28, 882511585,  0,     ""),
        new("Create object",                   "Chambered round",                 false, 0x1471EC,  1518341917, 0,     "GameCall_Ammo.ExpectedCreateObject"),
        new("Handle maker",                    "Chambered round",                 false, 0x1456C0,  3121353053, 0,     "GameCall_Ammo.ExpectedHandleCtor"),
        new("Game engine pointer",             "Game anchor (V, menus, loading)", false, 0x342AC00, 2549221846, 0,     ""),
        new("Game database pointer",           "No aim zoom, weapon names",       false, 0x37C1490, 3180664207, 0,     ""),
        new("XInputGetState import",           "Controller input",                false, 0,         0,          0,     ""),
        new("Present (frames)",                "VR picture",                      false, 0,         0,          0,     ""),
        new("Widget parent (field)",           "Shrink game HUD (test)",          true,  0x168,     0,          0,     "field"),
        new("Widget name (field)",             "Shrink game HUD (test)",          true,  0x188,     0,          0,     "field"),
        new("Widget scale (field)",            "Shrink game HUD (test)",          true,  0xC0,      0,          0,     "field"),
        new("Widget pivot (field)",            "Shrink game HUD (test)",          true,  0x1A8,     0,          0,     "field"),
        new("Vehicle model name (field)",      "Steering wheel learned banner",   false, 0x1C8,     0,          0,     "field"),
        new("Crosshair safe-state (field)",    "Crosshair control",               false, 0x283,     0,          0,     "field"),
        new("Crosshair reduced opacity (field)", "Crosshair control",             false, 0x278,     0,          0,     "field"),
        new("Hold indicator safety",           "Load safety (Johnny saves)",      false, 0x4D7EF8,  2221084617, 0,     "NullPlayerGuard.HelperHead"), // RA01: M56
    };

    static readonly State[] state = new State[Rows.Length];
    static readonly nint[] at = new nint[Rows.Length];
    static readonly string[] how = new string[Rows.Length];
    static readonly int[] rvaCache = new int[Rows.Length];   // RA01: Rva(): 0 = not worked out yet, -1 = not known on this build
    static readonly object gate = new();
    static bool reportWritten, previousKept;

    // RA01: true when the running game is the build the table was made on. Cost - CPU: extremely low; GPU: none.
    public static bool SameBuild => ExeScan.Base != 0 && ExeScan.TimeStamp == Build231;

    // RA01: every part number in the table (AddressList keeps only these). Cost - CPU: extremely low; GPU: none.
    public static HashSet<uint> Hashes()
    {
        HashSet<uint> set = new();
        foreach (Row row in Rows) if (row.Hash != 0) set.Add(row.Hash);
        return set;
    }

    // RA01: ExeScan.FindAll for a table row, with the safety net. The pattern found exactly once: that place (FOUND or MOVED). Otherwise the row's function is
    // looked up (2.31: its known place; another build: the address list) and its first 'checkBytes' pattern bytes (0 = all) must match there before it is
    // handed back as the one place. Neither: the scan's own result comes back unchanged, so the caller logs and gives up exactly as before, and the row is OFF.
    // Cost - CPU: medium, once per hook at start-up (the same exe search as before); the fallback adds one short search inside one function; GPU: none.
    public static List<nint> FindAll(int id, ReadOnlySpan<byte> pattern, int checkBytes = 0)
    {
        List<nint> found = ExeScan.FindAll(pattern);
        if (found.Count == 1) { Found(id, found[0], "by its pattern"); return found; }
        nint spot = InFunction(id, checkBytes > 0 ? pattern[..checkBytes] : pattern);
        if (spot != 0)
        {
            Found(id, spot, $"pattern found {found.Count} times; {(SameBuild ? "its 2.31 place" : "its part number")} checked by bytes");
            Log.Write($"RA01 rehooker: {Rows[id].Name} - its pattern was found {found.Count} times, picked RVA 0x{spot - ExeScan.Base:X} by {(SameBuild ? "its 2.31 place" : "its part number")} (bytes checked)");
            return new() { spot };
        }
        Off(id, $"pattern found {found.Count} times (needs 1); {Fallback(id)}");
        return found;
    }

    // RA01: the row's spot by its function, checked by 'check': on a start-of-function row the bytes must be right at the start; on a mid-function row they must
    // occur exactly once in the function's first RoomBytes. 0 if the function isn't known or the bytes aren't there.
    // Builder picture: the index names the room; the fixing is found by its shape inside that room, never by a tape measure from the door alone.
    // Cost - CPU: extremely low (one compare, or one 4 KB search); GPU: none.
    public static nint InFunction(int id, ReadOnlySpan<byte> check)
    {
        int function = FunctionRva(id);
        if (function <= 0 || check.Length == 0) return 0;
        nint start = ExeScan.Base + function;
        if (!Readable(start, RoomBytes)) return 0;
        ReadOnlySpan<byte> room = new((void*)start, RoomBytes);
        if (Rows[id].InFunction == 0) return room.StartsWith(check) ? start : 0;
        int first = room.IndexOf(check);
        if (first < 0 || room[(first + 1)..].IndexOf(check) >= 0) return 0;
        return start + first;
    }

    // RA01: where the row's FUNCTION starts this build (RVA), or 0: on 2.31 its known place minus the in-function distance; else from the address list.
    // Cost - CPU: extremely low (the address list loads once, see AddressList); GPU: none.
    public static int FunctionRva(int id)
    {
        Row row = Rows[id];
        if (ExeScan.Base == 0) return 0;
        if (SameBuild) return row.Rva231 - row.InFunction;
        return row.Hash != 0 && AddressList.TryRva(row.Hash, out uint rva) ? (int)rva : 0;
    }

    // RA01: for the rows with no pattern search (fixed places): the RVA to use this build - the 2.31 one, or the address list's (function + distance). 0 = not known,
    // and the row is marked OFF. The owner still checks the bytes there before calling anything. Cached, so hot paths may call it freely.
    // Cost - CPU: extremely low (an array read after the first call); GPU: none.
    public static int Rva(int id)
    {
        int cached = Volatile.Read(ref rvaCache[id]);
        if (cached != 0) return Math.Max(cached, 0);
        if (ExeScan.Base == 0) return 0;   // RA01: too early - not cached, asked again later
        int function = FunctionRva(id);
        int rva = function > 0 ? function + Rows[id].InFunction : -1;
        if (rva < 0) Off(id, Fallback(id));
        Volatile.Write(ref rvaCache[id], rva);
        return Math.Max(rva, 0);
    }

    // RA01: why a row could not be re-found, for the report. Cost - CPU: extremely low; GPU: none.
    static string Fallback(int id) =>
        Rows[id].Hash == 0 ? "not in the address list" : SameBuild ? "not at its 2.31 place either" : $"address list: {(AddressList.TryRva(Rows[id].Hash, out _) ? "found, but the bytes there don't match" : AddressList.Status)}";

    // RA01: a row was found at 'where' ('by' says how). FOUND if that is the 2.31 place, otherwise MOVED. Cost - CPU: extremely low; GPU: none.
    public static void Found(int id, nint where, string by)
    {
        bool same = where - ExeScan.Base == Rows[id].Rva231 || Rows[id].Rva231 == 0;
        Set(id, same ? State.Found : State.Moved, where, by);
    }

    // RA01: a row is off ('why'), even if it was found a moment before (a later check failed). Cost - CPU: extremely low; GPU: none.
    public static void Off(int id, string why) => Set(id, State.Off, 0, why);

    // RA01: stores a row's result; after start-up, rewrites the report (late hooks, such as the culling lab ~15 s in). Cost - CPU: extremely low (a file write after start-up only); GPU: none.
    static void Set(int id, State s, nint where, string text)
    {
        bool rewrite;
        lock (gate) { state[id] = s; at[id] = where; how[id] = text; rewrite = reportWritten; }
        if (rewrite) WriteReport();
    }

    // RA01: one row as text: "FOUND at RVA 0x127F97" / "MOVED 0x127F97 -> 0x12A0F7 (how)" / "OFF - why" / "not checked yet". Cost - CPU: extremely low; GPU: none.
    public static string Line(int id)
    {
        lock (gate)
        {
            Row row = Rows[id];
            string place = row.Name.Contains(".dll") || row.Pattern == "field" ? "+0x" : "RVA 0x";   // RA01: M44 - a field's place is inside its object
            nint rva = at[id] == 0 ? 0 : at[id] - ExeScan.Base;   // RA01: the PhysX rows report ExeScan.Base + their DLL offset, so this gives the offset back
            return state[id] switch
            {
                State.Found => row.Rva231 == 0 ? $"FOUND ({how[id]})" : $"FOUND at {place}{row.Rva231:X}",
                State.Moved => $"MOVED {place}{row.Rva231:X} -> 0x{rva:X} ({how[id]})",
                State.Off => $"OFF - {how[id]}",
                _ => "not checked yet",
            };
        }
    }

    // RA01: the game build line. Cost - CPU: extremely low; GPU: none.
    public static string BuildLine =>
        ExeScan.Base == 0 ? "exe not read" : $"exe stamp {ExeScan.TimeStamp:X8} - {(SameBuild ? "2.31, the build RA01 was made on" : $"NOT 2.31 ({Build231:X8}): addresses come from the address list")}";

    // RA01: the player features with at least one part off (Dev rows don't count). Cost - CPU: extremely low (33 rows); GPU: none.
    public static List<string> FeaturesOff()
    {
        List<string> off = new();
        lock (gate)
            for (int i = 0; i < Rows.Length; i++)
                if (state[i] == State.Off && !Rows[i].Dev && !off.Contains(Rows[i].Feature)) off.Add(Rows[i].Feature);
        return off;
    }

    // RA01: writes Logs\RA01_Rehooker.txt (the whole table, one line each); at the end of start-up also shows the banner if a player feature is off. After
    // start-up it rewrites the file whenever a row changes. Never throws.
    // Cost - CPU: low (a ~40-line file written; at start-up once, then only when a late hook reports); GPU: none.
    public static void WriteReport()
    {
        try
        {
            bool first;
            lock (gate) { first = !reportWritten; reportWritten = true; }
            List<string> off = FeaturesOff();
            StringBuilder text = new();
            text.AppendLine($"RA01 Rehooker report - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Game build:   {BuildLine}");
            text.AppendLine($"Address list: {AddressList.Status}");
            text.AppendLine($"Features off: {(off.Count == 0 ? "none" : string.Join(", ", off))}");
            text.AppendLine();
            for (int i = 0; i < Rows.Length; i++)
                text.AppendLine($"{Rows[i].Name,-32} {Rows[i].Feature,-34} {Line(i)}");
            text.AppendLine();
            text.AppendLine("FOUND = where 2.31 had it. MOVED = found somewhere else and checked by its bytes. OFF = not used this start (the game runs as normal there).");
            text.AppendLine($"A new game build: run claude_session_tools\\RA01_HookCheck.py on the new exe and address list, and put the RA01_Addresses_<build>.ini it makes in the Settings folder.");
            lock (gate)
            {
                Directory.CreateDirectory(ModFolders.Logs);
                string path = Path.Combine(ModFolders.Logs, "RA01_Rehooker.txt");
                if (!previousKept)
                {
                    previousKept = true;
                    if (File.Exists(path))
                    {
                        foreach (string old in Directory.GetFiles(ModFolders.Logs, "RA01_Rehooker_previous_*.txt")) File.Delete(old);
                        File.Move(path, Path.Combine(ModFolders.Logs, $"RA01_Rehooker_previous_{File.GetLastWriteTime(path):yyyy-MM-dd_HH-mm-ss}.txt"));
                    }
                }
                File.WriteAllText(path, text.ToString());
            }
            if (first)
            {
                Log.Write($"RA01 rehooker: {BuildLine}; features off: {(off.Count == 0 ? "none" : string.Join(", ", off))} (Logs\\RA01_Rehooker.txt)");
                if (off.Count > 0) Banner.Show($"RA01: {off.Count} FEATURE{(off.Count == 1 ? "" : "S")} OFF ON THIS GAME BUILD", 15000);
            }
        }
        catch { }
    }

    // RA01: true if 'bytes' from 'start' are all readable (the safe way: VirtualQuery, never a fault). Cost - CPU: extremely low (one or two VirtualQuery calls); GPU: none.
    static bool Readable(nint start, int bytes)
    {
        Win32.MemoryBasicInformation info;
        nint end = start + bytes;
        for (nint p = start; p < end; p = (nint)((long)info.BaseAddress + (long)info.RegionSize))
        {
            if (Win32.VirtualQuery(p, &info, (nuint)sizeof(Win32.MemoryBasicInformation)) == 0) return false;
            if (info.State != 0x1000 || (info.Protect & 0xEE) == 0 || (info.Protect & 0x101) != 0) return false;   // RA01: committed, readable, not no-access or guard (as ExeScan)
        }
        return true;
    }
}
