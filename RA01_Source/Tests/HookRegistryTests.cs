using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - THE REHOOKER'S TABLE (Core\GameData\HookRegistry.cs). Every hook, fixed address and field RA01 uses in the game is one row there; these tests make
// sure the table, its row numbers and the byte patterns it names all still agree with each other and with the 2.31 snapshot below.
// Builder picture: checking the foreman's clipboard against the drawings - every fixing point listed once, numbered right, and its template still on the shelf.
// Cost - CPU: extremely low (a few hundred compares, test runs only); GPU: none.
public class HookRegistryTests
{
    // RA01: the table as it was when the tests were written (2.31). Name, feature, Dev, 2.31 place, part number, distance into the function, pattern.
    static readonly HookRegistry.Row[] Snapshot =
    {
        new("Camera (stereo view)", "VR camera", false, 0x127F97, 1692210232u, 0x3F, "CameraHook.LocateCamera"),
        new("Camera end (log only)", "VR camera", true, 0x785578, 2624398555u, 0xB8, "CameraHook.FinalCamera"),
        new("Camera patch (log only)", "VR camera", true, 0x1D8A8F, 4281144013u, 0x537, "CameraHook.PatchCamera"),
        new("Body pose", "Body tracking (VRIK)", false, 0x4B6968, 1343032985u, 0x0, "PoseHook.BuildBody"),
        new("Throw arc worker", "Physical throw", false, 0x47323C, 3544784313u, 0x0, "ThrowLever.ArcHead"),
        new("Throw flight path", "Physical throw", false, 0x84F9AC, 3327736640u, 0x0, "ThrowLever.PathHead"),
        new("Ray runner", "Gun aiming", false, 0x2389CC, 1462903155u, 0x0, "AimProbe.RayRunner"),
        new("Multi-hit ray runner", "Gun aiming", false, 0x2397F8, 4039912614u, 0x0, "AimProbe.MultiHitRunner"),
        new("PhysX call in the runner", "Gun aiming", false, 0x239935, 4039912614u, 0x13D, ""),
        new("Aim line callers (3)", "Gun aiming", false, 0x6F1FAF, 430713883u, 0x43, ""),
        new("PhysX raycast (PhysX3_x64.dll)", "Gun aiming", false, 0x60890, 0u, 0x0, "AimProbe.PxRaycast"),
        new("PhysX sweep (PhysX3_x64.dll)", "Gun aiming", false, 0x60960, 0u, 0x0, "AimProbe.PxSweep"),
        new("Shot aim source", "Shots from the gun", false, 0x4D8354, 2604539728u, 0x0, "ShotProbe.AimSource"),
        new("Crosshair update", "Crosshair control", false, 0x5B8D28, 433331076u, 0x0, "CrosshairProbe.Visibility"),
        new("Crosshair apply", "Crosshair control", false, 0x5B8C18, 3915519740u, 0x0, "CrosshairProbe.Apply"),
        new("Crosshair per-frame check", "Crosshair control", false, 0x1492600, 3117420914u, 0x0, "CrosshairProbe.ManagerBuilder"),
        new("Weapon event handler", "Weapon detection", false, 0x656288, 879305929u, 0x0, "WeaponEventProbe.Handler"),
        new("Blackboard setters (10)", "Menu sensing", false, 0x32BD8C, 0u, 0x0, "BlackboardProbe.Setter"),
        new("Render camera", "Frame pairing", false, 0x623FD8, 1053501143u, 0x0, "RenderCameraProbe.Site"),
        new("World node attach", "Room probe (Dev)", true, 0x3C7C1A, 3515291207u, 0x6, "NodeProbe.AttachBody"),
        new("Occlusion switch", "Culling lab (Dev)", true, 0x79E52C, 1827017916u, 0x20, "CullingLab.GateBytes"),
        new("Distance stretch formula", "Wide-view distance fix (test)", true, 0x4E427B, 1697190968u, 0x24B, "DistanceScaleFix.Head"),
        new("RTTI catalogue", "Game calls (refill, sounds)", false, 0x2F1080, 1247874916u, 0x0, "GameCall.ExpectedRttiGet"),
        new("Name look-up", "Game calls (refill, sounds)", false, 0x209580, 1759447004u, 0x0, "GameCall.ExpectedNameGet"),
        new("Call native function", "Game calls (refill, sounds)", false, 0x20668A0, 3778157291u, 0x0, "GameCall.ExpectedCallNative"),
        new("Call script function", "Game calls (Dev tests)", true, 0x1BC104, 3791200470u, 0x0, "GameCall.ExpectedCallScripted"),
        new("Call stack table", "Game calls (refill, sounds)", false, 0x2B37B28, 882511585u, 0x0, ""),
        new("Create object", "Chambered round", false, 0x1471EC, 1518341917u, 0x0, "GameCall_Ammo.ExpectedCreateObject"),
        new("Handle maker", "Chambered round", false, 0x1456C0, 3121353053u, 0x0, "GameCall_Ammo.ExpectedHandleCtor"),
        new("Game engine pointer", "Game anchor (V, menus, loading)", false, 0x342AC00, 2549221846u, 0x0, ""),
        new("Game database pointer", "No aim zoom, weapon names", false, 0x37C1490, 3180664207u, 0x0, ""),
        new("XInputGetState import", "Controller input", false, 0x0, 0u, 0x0, ""),
        new("Present (frames)", "VR picture", false, 0x0, 0u, 0x0, ""),
        new("Widget parent (field)", "Shrink game HUD (test)", true, 0x168, 0u, 0x0, "field"),
        new("Widget name (field)", "Shrink game HUD (test)", true, 0x188, 0u, 0x0, "field"),
        new("Widget scale (field)", "Shrink game HUD (test)", true, 0xC0, 0u, 0x0, "field"),
        new("Widget pivot (field)", "Shrink game HUD (test)", true, 0x1A8, 0u, 0x0, "field"),
        new("Vehicle model name (field)", "Steering wheel learned banner", false, 0x1C8, 0u, 0x0, "field"),
        new("Crosshair safe-state (field)", "Crosshair control", false, 0x283, 0u, 0x0, "field"),
        new("Crosshair reduced opacity (field)", "Crosshair control", false, 0x278, 0u, 0x0, "field"),
        new("Hold indicator safety", "Load safety (Johnny saves)", false, 0x4D7EF8, 2221084617u, 0x0, "NullPlayerGuard.HelperHead"),
    };

    // RA01: the bytes of every pattern the table names, as they were when the tests were written. These are the "templates" each hook is found by on 2.31.
    static readonly Dictionary<string, string> PatternSnapshot = new()
    {
        ["CameraHook.LocateCamera"] = "F3 0F 11 43 20 48 8D 54 24 20 48 8B 06",
        ["CameraHook.FinalCamera"] = "F3 44 0F 5E 4E 40 49 81 C0 20 0F 00 00",
        ["CameraHook.PatchCamera"] = "0F 11 02 80 BE B1 00 00 00 00 89 45 88",
        ["PoseHook.BuildBody"] = "40 53 48 83 EC 40 4C 8B 52 10 4C 8B C9 44 8B 41",
        ["ThrowLever.ArcHead"] = "48 89 5C 24 10 48 89 74 24 18 57 48 81 EC B0 00 00 00 48 8B DA 48 8B F1 E8",
        ["ThrowLever.PathHead"] = "48 89 5C 24 08 57 48 83 EC 20 48 8D 99 70 04 00 00 48 8B FA 48 8B CB E8",
        ["AimProbe.RayRunner"] = "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 55 41 56 41 57 48 8D 68 98 48 81 EC 50 01 00 00 4C 8B F9 49 8B F0 48 8B CA",
        ["AimProbe.MultiHitRunner"] = "48 8B C4 48 89 58 08 48 89 70 10 48 89 78 18 55 41 56 41 57 48 8D A8 88 EF FF FF B8 60 11 00 00 E8 F3 C3 7F 01 48 2B E0",
        ["AimProbe.PxRaycast"] = "F3 0F 11 5C 24 20 4C 8B DC 53 48 83 EC 70 0F AE 9C 24 88 00 00 00 8B 9C",
        ["AimProbe.PxSweep"] = "48 8B C4 48 89 58 20 55 56 57 41 56 41 57 48 81 EC 80 00 00 00 48 8B BC",
        ["ShotProbe.AimSource"] = "48 8B C4 48 89 58 10 48 89 70 18 48 89 78 20 55 41 54 41 55 41 56 41 57 48 8D A8 C8 FA FF FF 48 81 EC 10 06 00 00 48 8B",
        ["CrosshairProbe.Visibility"] = "48 89 5C 24 10 48 89 7C 24 18 55 48 8B EC 48 81 EC 80 00 00 00 33 FF 48",
        ["CrosshairProbe.Apply"] = "48 89 5C 24 18 55 48 8B EC 48 83 EC 70 48 8B D9",
        ["CrosshairProbe.ManagerBuilder"] = "48 83 EC 28 E8 07 81 E2 FE 33 D2 48 8D 05 76 76",
        ["WeaponEventProbe.Handler"] = "48 89 5C 24 10 48 89 74 24 18 55 57 41 56 48 8B EC 48 83 EC 60 48 8B F1 48 8B DA 48",
        ["BlackboardProbe.Setter"] = "4C 8B DC 49 89 5B 08 49 89 73 10 49 89 7B 18 41 56 48 83 EC 40 48 8B 01 48 8B F2 4C 8B F1 49 8B D0 49 8D 4B D8 41 8A F9 48 8B 98 10 01 00 00 E8",
        ["RenderCameraProbe.Site"] = "48 89 5C 24 08 57 48 83 EC 20 4C 89 41 18 0F 57 C0 49 8B 80 00 3B 0C 00",
        ["NodeProbe.AttachBody"] = "48 8B 01 48 8B D9 80 89 88 00 00 00 84 FF 90 68 01 00 00 8A 83 88 00 00 00 24 FB 0C 30",
        ["CullingLab.GateBytes"] = "44 38 81 9E 01 00 00 0F 84",
        ["DistanceScaleFix.Head"] = "F3 0F 59 C0 48 8B C3 F3 0F 59 05",
        ["GameCall.ExpectedRttiGet"] = "48 83 EC 28 65 48 8B 04 25 58 00 00 00",
        ["GameCall.ExpectedNameGet"] = "48 83 EC 38 48 8B 11 48 8D 4C 24 20 E8",
        ["GameCall.ExpectedCallNative"] = "48 89 5C 24 08 48 89 7C 24 10 55 48 8D AC",
        ["GameCall.ExpectedCallScripted"] = "40 55 48 81 EC D0 00 00 00 48 8D 6C 24 40",
        ["GameCall_Ammo.ExpectedCreateObject"] = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57",
        ["GameCall_Ammo.ExpectedHandleCtor"] = "48 89 5C 24 10 48 89 74 24 18 48 89 7C 24 20 55",
        ["NullPlayerGuard.HelperHead"] = "40 53 48 83 EC 30 44 8A 82 55 01 00 00 41 B1 01",
    };

    // RA01: each row number constant (HookRegistry.CameraLocate = 0 ...) and the row name it must point at. A row added in the middle without moving the
    // RA01: numbers after it would make a hook report on someone else's row - this catches that.
    static readonly Dictionary<string, string> IndexSnapshot = new()
    {
        ["CameraLocate"] = "Camera (stereo view)", ["CameraFinal"] = "Camera end (log only)", ["CameraPatch"] = "Camera patch (log only)", ["Pose"] = "Body pose",
        ["ThrowArc"] = "Throw arc worker", ["ThrowPath"] = "Throw flight path", ["RayRunner"] = "Ray runner", ["MultiHit"] = "Multi-hit ray runner",
        ["MultiHitReturn"] = "PhysX call in the runner", ["AimCallers"] = "Aim line callers (3)", ["PxRaycast"] = "PhysX raycast (PhysX3_x64.dll)",
        ["PxSweep"] = "PhysX sweep (PhysX3_x64.dll)", ["AimSource"] = "Shot aim source", ["CrosshairUpdate"] = "Crosshair update", ["CrosshairApply"] = "Crosshair apply",
        ["CrosshairFrame"] = "Crosshair per-frame check", ["WeaponEvent"] = "Weapon event handler", ["Blackboard"] = "Blackboard setters (10)", ["RenderCamera"] = "Render camera",
        ["NodeAttach"] = "World node attach", ["CullingGate"] = "Occlusion switch", ["DistanceScale"] = "Distance stretch formula", ["RttiGet"] = "RTTI catalogue",
        ["NameGet"] = "Name look-up", ["CallNative"] = "Call native function", ["CallScripted"] = "Call script function", ["StackVtable"] = "Call stack table",
        ["CreateObject"] = "Create object", ["HandleCtor"] = "Handle maker", ["Engine"] = "Game engine pointer", ["TweakDb"] = "Game database pointer",
        ["XInput"] = "XInputGetState import", ["Present"] = "Present (frames)", ["HudParent"] = "Widget parent (field)", ["HudName"] = "Widget name (field)",
        ["HudScale"] = "Widget scale (field)", ["HudPivot"] = "Widget pivot (field)", ["VehicleModel"] = "Vehicle model name (field)",
        ["CrosshairSafeState"] = "Crosshair safe-state (field)", ["CrosshairOpacity"] = "Crosshair reduced opacity (field)",
        ["HoldIndicatorSafety"] = "Hold indicator safety",
    };

    // RA01: the whole table matches the snapshot, row by row (a tripwire: any change to a place, part number or pattern name shows here).
    // Cost - CPU: extremely low (40 row compares); GPU: none.
    [Fact]
    public void Table_matches_snapshot()
    {
        for (int i = 0; i < Math.Min(Snapshot.Length, HookRegistry.Rows.Length); i++)
            Assert.True(Snapshot[i] == HookRegistry.Rows[i], Kit.Tripwire($"Rehooker row {i} (was {Snapshot[i]}, now {HookRegistry.Rows[i]})", "HookRegistryTests.cs, Snapshot"));
        Assert.True(Snapshot.Length == HookRegistry.Rows.Length, Kit.Tripwire($"the number of Rehooker rows ({Snapshot.Length} -> {HookRegistry.Rows.Length})", "HookRegistryTests.cs, Snapshot"));
    }

    // RA01: every row number constant points at the row it is named for, and every row has exactly one constant. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Row_numbers_point_at_their_rows()
    {
        var consts = typeof(HookRegistry).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(int)).ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!);
        foreach ((string name, int index) in consts)
        {
            Assert.True(index >= 0 && index < HookRegistry.Rows.Length, $"HookRegistry.{name} = {index} is outside the table (0..{HookRegistry.Rows.Length - 1})");
            Assert.True(IndexSnapshot.TryGetValue(name, out string? expected), Kit.Tripwire($"a new row number HookRegistry.{name}", "HookRegistryTests.cs, IndexSnapshot"));
            Assert.True(HookRegistry.Rows[index].Name == expected, $"HookRegistry.{name} = {index} points at \"{HookRegistry.Rows[index].Name}\", expected \"{expected}\" - a row was added or moved without moving the numbers after it");
        }
        Assert.Equal(HookRegistry.Rows.Length, consts.Values.Distinct().Count());
    }

    // RA01: every "File.Name" pattern exists in that file's class as bytes, and the bytes are the snapshot's. Cost - CPU: extremely low (26 look-ups); GPU: none.
    [Fact]
    public void Every_pattern_exists_and_matches_snapshot()
    {
        foreach (HookRegistry.Row row in HookRegistry.Rows.Where(r => r.Pattern.Contains('.')))
        {
            byte[]? bytes = Kit.PatternBytes(row.Pattern);
            Assert.True(bytes != null, $"row \"{row.Name}\": its pattern {row.Pattern} is not a byte pattern in that file any more (RA01_HookCheck.py reads it by this name too)");
            Assert.True(bytes!.Length >= 8, $"row \"{row.Name}\": pattern {row.Pattern} is only {bytes.Length} bytes - too short to be found exactly once");
            Assert.True(PatternSnapshot.TryGetValue(row.Pattern, out string? hex), Kit.Tripwire($"a new pattern {row.Pattern}", "HookRegistryTests.cs, PatternSnapshot"));
            Assert.True(hex == Kit.Hex(bytes), Kit.Tripwire($"the bytes of {row.Pattern} (was {hex}, now {Kit.Hex(bytes)})", "HookRegistryTests.cs, PatternSnapshot"));
        }
    }

    // RA01: the rules each kind of row must keep. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Rows_keep_their_rules()
    {
        Assert.Equal(HookRegistry.Rows.Length, HookRegistry.Rows.Select(r => r.Name).Distinct().Count());
        foreach (HookRegistry.Row row in HookRegistry.Rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Feature), $"a row with an empty name or feature: {row}");
            Assert.True(row.Pattern == "" || row.Pattern == "field" || row.Pattern.Count(c => c == '.') == 1, $"row \"{row.Name}\": pattern must be \"\", \"field\" or File.Name, got \"{row.Pattern}\"");
            if (row.Pattern == "field") Assert.True(row.Hash == 0 && row.InFunction == 0 && row.Rva231 > 0 && row.Rva231 < 0x1000, $"field row \"{row.Name}\": a field's place is a small offset inside its object, with no part number");
            if (row.Hash != 0) Assert.True(row.Rva231 - row.InFunction > 0x1000, $"row \"{row.Name}\": its function start (2.31 place minus distance) must be inside the exe's code");
            if (row.InFunction != 0) Assert.True(row.Hash != 0, $"row \"{row.Name}\": a mid-function row needs a part number to be re-found on a new build");
        }
    }

    // RA01: the PhysX return point sits 0x13D into the multi-hit runner, in the same function (the handoff's "PhysX call at +0x137" plus its 6 bytes).
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void PhysX_return_point_is_inside_the_multi_hit_runner()
    {
        HookRegistry.Row runner = HookRegistry.Rows[HookRegistry.MultiHit], back = HookRegistry.Rows[HookRegistry.MultiHitReturn];
        Assert.Equal(runner.Hash, back.Hash);
        Assert.Equal(runner.Rva231 + back.InFunction, back.Rva231);
    }

    // RA01: Hashes() hands AddressList every distinct part number in the table, and nothing else. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Hashes_lists_every_part_number_once()
    {
        HashSet<uint> hashes = HookRegistry.Hashes();
        Assert.Equal(HookRegistry.Rows.Where(r => r.Hash != 0).Select(r => r.Hash).Distinct().OrderBy(h => h), hashes.OrderBy(h => h));
        Assert.DoesNotContain(0u, hashes);
    }

    // RA01: the 2.31 exe stamp, and the launcher's "made for" build, move together (a new build means both are updated, see the game update guide).
    // Cost - CPU: extremely low (one small file read); GPU: none.
    [Fact]
    public void Build_stamp_and_launcher_build_agree()
    {
        Assert.True(HookRegistry.Build231 == 0x68AF45EA, Kit.Tripwire("HookRegistry.Build231 (the 2.31 exe stamp)", "HookRegistryTests.cs"));
        string launcher = File.ReadAllText(Path.Combine(Kit.Root, "Launcher", "LauncherWindow.cs"));
        Assert.True(launcher.Contains("MadeForBuild = \"2.31\""), Kit.Tripwire("Launcher\\LauncherWindow.cs MadeForBuild (expected \"2.31\")", "HookRegistryTests.cs"));
    }
}
