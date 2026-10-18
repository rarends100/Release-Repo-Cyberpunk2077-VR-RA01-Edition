using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - ROB'S STANDING RULES, checked on the code itself:
//  - the folders' boundaries: Core never calls into Actions or Visuals (Entry.cs, the start-up wiring that switches them on, is the one door);
//  - each file's namespace matches its folder;
//  - no NEW static Vector3 / Quaternion / Matrix4x4 fields (the NativeAOT compiler quirk - arrays are fine);
//  - every comment block starts with RA01 (once, on its first line), and every class and function comment ends with its CPU/GPU cost;
//  - a file keeps one kind of line ending.
// The comment rules were not kept perfectly before the tests existed, so each file has an allowance (the counts found on 2026-10-11): a file may not get
// WORSE than its allowance. When you fix old ones, lower the number. Builder picture: a snag list - the old snags are written down, new ones fail inspection.
// Cost - CPU: low (reads every .cs once and walks the mod's compiled code once, well under a second); GPU: none.
public class CodeRulesTests
{
    // RA01: declarations (class, struct, function) with no "Cost" in the comment above them, per file, as found when the tests were written.
    static readonly Dictionary<string, int> NoCostAllowance = new()
    {
        ["Actions/HandTargets.cs"] = 1, ["Actions/MeleeActions.cs"] = 6, ["Actions/PhysicalReload.cs"] = 3, ["Actions/PhysicalSteering.cs"] = 3,
        ["Core/BoneFinder.cs"] = 2, ["Core/CrashWatch.cs"] = 1, ["Core/GameData/AimZoomData.cs"] = 5, ["Core/GameData/GameAnchor.cs"] = 1,
        ["Core/GameData/GameCall.cs"] = 3, ["Core/GameData/GameCall_Ammo.cs"] = 1, ["Core/GameData/HookRegistry.cs"] = 1, ["Core/GameData/PointerFinder.cs"] = 1,
        ["Core/GameData/PointerTableDump.cs"] = 3, ["Core/GunCalibration.cs"] = 1, ["Core/HandStrip.cs"] = 1, ["Core/Hooks/CameraHook.cs"] = 2,
        ["Core/Hooks/SkeletonHook.cs"] = 2, ["Core/PouchGlow.cs"] = 2, ["Core/Probes/AimProbe.cs"] = 1, ["Core/Probes/CrosshairProbe.cs"] = 2,
        ["Core/Probes/WeaponProbe.cs"] = 1, ["Core/SkeletonRadiate.cs"] = 10, ["Core/Tuning.cs"] = 1, ["Core/UI_SettingsMenus/AutoTune.cs"] = 1,
        ["Core/UI_SettingsMenus/SettingsMenu.cs"] = 2, ["Core/VehicleState.cs"] = 1, ["Core/Video/CullingLab.cs"] = 3, ["Core/Video/HudLayer.cs"] = 8,
        ["Core/Video/ScopeLens.cs"] = 4, ["Core/Video/VRRuntimes/OpenXR.cs"] = 33, ["Core/Video/VRRuntimes/XrSession.cs"] = 1,
        ["Core/controls_mapping/ControllerMapping.cs"] = 2, ["Core/controls_mapping/MenuPointer.cs"] = 1, ["Visuals/VRIK.cs"] = 6,
    };

    // RA01: comments that don't start with RA01, per file, as found when the tests were written (HudLayer's section dividers, FireArmAiming's header block, a FrameCopy note).
    static readonly Dictionary<string, int> NotRa01Allowance = new()
    {
        ["Actions/FireArmAiming.cs"] = 5, ["Core/Video/FrameCopy.cs"] = 1, ["Core/Video/HudLayer.cs"] = 4,
    };

    static readonly Regex Declaration = new(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|static|unsafe|sealed|readonly|partial|abstract|override|virtual|async|new)\s+)+(?:class\b|struct\b|record\b|enum\b|[\w<>\[\],\.\?\* ]+?\s+\w+\s*\()", RegexOptions.Compiled);
    static readonly Regex Strings = new(@"@""(?:""""|[^""])*""|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])'", RegexOptions.Compiled);

    // RA01: the IL operand sizes, so the compiled code can be walked one instruction at a time. Cost - CPU: extremely low (once); GPU: none.
    static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    // RA01: Core (except Entry.cs) never touches anything in RA01.Actions or RA01.Visuals - checked in the compiled code, so no comment or string can fool it.
    // Cost - CPU: low (walks every Core method's compiled code once); GPU: none.
    [Fact]
    public void Core_does_not_reach_into_Actions_or_Visuals()
    {
        List<string> crossings = new();
        foreach (Type t in typeof(Kit).Assembly.GetTypes().Where(t => t.Namespace == "RA01.Core"))
        {
            Type outer = t; while (outer.DeclaringType != null) outer = outer.DeclaringType;
            if (outer.Name == "Entry") continue;   // RA01: the start-up wiring: switches Actions and Visuals on (Install), by design
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (Outside(f.FieldType)) crossings.Add($"{t.FullName}.{f.Name} (field of type {f.FieldType.FullName})");
            foreach (MethodBase m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                                      .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)))
                foreach (string used in Uses(m))
                    crossings.Add($"{t.FullName}.{m.Name} uses {used}");
        }
        Assert.True(crossings.Count == 0, "Core reaches into Actions or Visuals (Core may not; Actions and Visuals read Core instead):\n  " + string.Join("\n  ", crossings.Distinct()));
    }

    // RA01: is this type (or its element type) in Actions or Visuals? Cost - CPU: extremely low; GPU: none.
    static bool Outside(Type? t)
    {
        while (t != null && (t.IsArray || t.IsPointer || t.IsByRef)) t = t.GetElementType();
        if (t == null) return false;
        Type outer = t; while (outer.DeclaringType != null) outer = outer.DeclaringType;
        return outer.Namespace is "RA01.Actions" or "RA01.Visuals";
    }

    // RA01: every Actions or Visuals type or member one method's compiled code refers to. Cost - CPU: extremely low (one pass over its bytes); GPU: none.
    static IEnumerable<string> Uses(MethodBase m)
    {
        byte[]? il;
        try { il = m.GetMethodBody()?.GetILAsByteArray(); } catch { yield break; }
        if (il == null) yield break;
        Type[]? typeArgs = m.DeclaringType!.IsGenericType ? m.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArgs = m.IsGenericMethod ? m.GetGenericArguments() : null;
        for (int at = 0; at < il.Length;)
        {
            short value = il[at] == 0xFE ? (short)(0xFE00 | il[at + 1]) : il[at];
            at += il[at] == 0xFE ? 2 : 1;
            OpCode op = OpCodesByValue[value];
            int token = op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok ? BitConverter.ToInt32(il, at) : 0;
            at += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
                _ => 4,
            };
            if (token == 0) continue;
            MemberInfo? used = null;
            try { used = m.Module.ResolveMember(token, typeArgs, methodArgs); } catch { }
            if (used is Type type ? Outside(type) : Outside(used?.DeclaringType)) yield return used is Type x ? x.FullName! : $"{used!.DeclaringType!.FullName}.{used.Name}";
        }
    }

    // RA01: each file's namespace is its top folder's (Core\... -> RA01.Core). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Namespaces_match_folders()
    {
        foreach (string file in Kit.SourceFiles())
        {
            string expected = "RA01." + file.Split('/')[0];
            Match ns = Regex.Match(Kit.Source(file), @"^namespace\s+([\w\.]+)", RegexOptions.Multiline);
            Assert.True(ns.Success && ns.Groups[1].Value == expected, $"{file}: namespace should be {expected}, is {(ns.Success ? ns.Groups[1].Value : "missing")}");
        }
    }

    // RA01: the NativeAOT quirk: no static Vector2/3/4, Quaternion or Matrix4x4 fields anywhere in the mod (keep them in float arrays). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void No_static_vector_fields()
    {
        Type[] banned = { typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Quaternion), typeof(Matrix4x4) };
        // RA01: the ones already in the mod on 2026-10-11 - they compile fine with NativeAOT (the quirk strikes only some methods). A new one fails here:
        // RA01: prefer a float array; if you keep it and build.ps1 still builds RA01.dll, add its name to this list.
        string[] known =
        {
            "RA01.Visuals.VRIK.pendingWristL", "RA01.Visuals.VRIK.pendingWristR", "RA01.Visuals.VRIK.eyeSum", "RA01.Visuals.VRIK.hipsBase", "RA01.Visuals.VRIK.hipsWritten",
            "RA01.Actions.TwoHandGrip.barrelInHand", "RA01.Actions.TwoHandGrip.lastGunTurn", "RA01.Actions.TwoHandGrip.grabInHand", "RA01.Actions.TwoHandGrip.offTurnInHand",
            "RA01.Actions.WeaponWeight.turnNow", "RA01.Core.GunCalibration.sum", "RA01.Core.CameraBodyProbe.gameLookSpot", "RA01.Core.CameraBodyProbe.gameLookTurn",
            "RA01.Core.WeaponProbe.magSlotPos", "RA01.Core.WeaponProbe.gripBonePos", "RA01.Core.WeaponProbe.muzzlePos", "RA01.Core.WeaponProbe.muzzleTurn",
            "RA01.Core.WeaponProbe.bodyMuzzle", "RA01.Core.WeaponProbe.bodyMuzzleTurn",
        };
        string[] found = typeof(Kit).Assembly.GetTypes().Where(t => t.Namespace is "RA01.Core" or "RA01.Actions" or "RA01.Visuals")
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(f => banned.Contains(f.FieldType) && !known.Contains($"{t.FullName}.{f.Name}")).Select(f => $"{t.FullName}.{f.Name} ({f.FieldType.Name})"))
            .ToArray();
        Assert.True(found.Length == 0, "static vector/turn fields (the ILC quirk - use a float array instead): " + string.Join(", ", found));
    }

    // RA01: every // comment block starts with RA01: a whole-line comment directly under another whole-line comment belongs to that block
    // and needs no RA01 of its own (a file may not go over its allowance of old ones). Cost - CPU: low (reads every file once); GPU: none.
    [Fact]
    public void Comments_start_with_RA01()
    {
        List<string> over = new();
        foreach (string file in Kit.SourceFiles())
        {
            string[] lines = Kit.Source(file).Split('\n');
            string[] bad = lines.Select((l, i) => (line: Strings.Replace(l, "\"\""), i))
                .Where(x => x.line.Contains("//") && !Regex.IsMatch(x.line[x.line.IndexOf("//")..], @"^//+\s*RA01")
                            && !(x.i > 0 && lines[x.i].TrimStart().StartsWith("//") && lines[x.i - 1].TrimStart().StartsWith("//")))
                .Select(x => $"    line {x.i + 1}: {x.line.Trim()}").ToArray();
            int allowed = NotRa01Allowance.GetValueOrDefault(file);
            if (bad.Length > allowed) over.Add($"{file} ({bad.Length} found, {allowed} allowed):\n" + string.Join("\n", bad));
        }
        Assert.True(over.Count == 0, "comments that don't start with RA01:\n" + string.Join("\n", over));
    }

    // RA01: every class, struct and function has a cost in the comment above it (a file may not go over its allowance of old ones).
    // Cost - CPU: low (reads every file once); GPU: none.
    [Fact]
    public void Declarations_say_their_cost()
    {
        List<string> over = new();
        foreach (string file in Kit.SourceFiles())
        {
            string[] missing = NoCost(file).ToArray();
            int allowed = NoCostAllowance.GetValueOrDefault(file);
            if (missing.Length > allowed) over.Add($"{file} ({missing.Length} found, {allowed} allowed):\n" + string.Join("\n", missing));
        }
        Assert.True(over.Count == 0, "declarations whose comment has no 'Cost - CPU: ...; GPU: ...' line:\n" + string.Join("\n", over));
    }

    // RA01: the declarations in one file with no "Cost" on their own line or in the comment lines (and attributes) right above. Cost - CPU: extremely low; GPU: none.
    internal static IEnumerable<string> NoCost(string file)
    {
        string[] lines = Kit.Source(file).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            if (!Declaration.IsMatch(l) || Regex.IsMatch(l, @"\b(const|event|extern|delegate)\b")) continue;
            int paren = l.IndexOf('('), equals = l.IndexOf('=');
            if (equals >= 0 && (paren < 0 || equals < paren)) continue;   // RA01: a field with a value, not a function
            if (l.Contains("Cost")) continue;
            bool said = false;
            for (int j = i - 1; j >= 0 && (lines[j].TrimStart().StartsWith("//") || lines[j].TrimStart().StartsWith("[")); j--)
                if (lines[j].Contains("Cost")) { said = true; break; }
            if (!said) yield return $"    line {i + 1}: {l.Trim()}";
        }
    }

    // RA01: a file keeps one kind of line ending (all LF, or all CRLF - whatever the file had). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Line_endings_are_not_mixed()
    {
        foreach (string file in Kit.SourceFiles())
        {
            string text = Kit.Source(file);
            int crlf = Regex.Matches(text, "\r\n").Count, lf = Regex.Matches(text, "(?<!\r)\n").Count;
            Assert.True(crlf == 0 || lf == 0, $"{file} mixes line endings ({crlf} CRLF, {lf} LF)");
        }
    }
}
