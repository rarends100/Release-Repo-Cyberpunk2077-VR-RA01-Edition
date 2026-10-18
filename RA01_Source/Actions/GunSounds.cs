using RA01.Core;

namespace RA01.Actions;

// RA01: M27-6k - THE GUN SOUNDS FILE (Rob 00:21: the reload sounds in a file of their own, so they can be changed without a rebuild). Settings\RA01_GunSounds.txt
// holds which of the game's sound events each gun plays when its magazine comes out, goes in, and when a dropped one lands:
//   <model> = <out sound>, <in sound>        the model is the gun's audio set, as the log line "RA01 weapon audio set" shows it (unity, sor22...)
//   type <gun type> = <out sound>, <in sound> used when a gun's model isn't listed (the type as the log writes it; ", Smart" for Smart guns)
//   type default = ...                        anything else
//   floor = <sound> / floor Smart = <sound>   a dropped magazine landing
// The first time it is missing, the mod writes it with the built-in choices (all 50 guns of the game's sound list with a reload pair). It is read again
// whenever the file changes (looked at, at most, once a second while sounds play), so an edit is heard the next reload. A broken or missing line uses the
// built-in choice; '#' starts a comment. A model is matched exactly first, else a listed model the gun's audio set contains ("lexington_iconic" -> lexington).
// Builder picture: a label maker's list on the workshop wall - change a label on the list, the next box gets the new label, no new machine needed.
// Cost - CPU: extremely low (a dictionary look-up per sound; a file read only when it changed); GPU: none.
internal static class GunSounds
{
    const string FileName = "RA01_GunSounds.txt";
    const long CheckMs = 1000;
    static readonly object gate = new();
    static Dictionary<string, (string Out, string In)> models = new();
    static Dictionary<string, (string Out, string In)> types = new();
    static string floor = "", floorSmart = "";
    static DateTime fileTime;
    static long checkedAt;
    static bool loaded;

    // RA01: the sound to play: 0 = out, 1 = in, 2 = floor; 'set' = the gun's audio set ("" unknown), 'type' = WeaponProbe.HeldName. "" = nothing to play.
    // Cost - CPU: extremely low (see the class comment); GPU: none.
    public static string Pick(int what, string set, string type, bool smart)
    {
        Refresh();
        lock (gate)
        {
            if (what == 2) return smart ? floorSmart : floor;
            if (set.Length > 0)
            {
                if (models.TryGetValue(set, out var own)) return what == 0 ? own.Out : own.In;
                foreach (var kv in models) if (set.Contains(kv.Key, StringComparison.Ordinal)) return what == 0 ? kv.Value.Out : kv.Value.In;
            }
            if (types.TryGetValue(type, out var t) || types.TryGetValue("default", out t)) return what == 0 ? t.Out : t.In;
            return "";
        }
    }

    // RA01: reads the file if it changed (at most once a CheckMs); writes it with the built-in choices if it is missing. Never throws.
    // Cost - CPU: extremely low (one file-time look-up a second while sounds play; a short read when changed); GPU: none.
    static void Refresh()
    {
        long now = Environment.TickCount64;
        if (loaded && now - checkedAt < CheckMs) return;
        checkedAt = now;
        string path = Path.Combine(ModFolders.Settings, FileName);
        try
        {
            if (!File.Exists(path)) { File.WriteAllText(path, DefaultText()); Log.Write($"RA01 gun sounds: wrote {FileName} with the built-in choices - edit it to change any gun's reload sounds"); }
            DateTime t = File.GetLastWriteTimeUtc(path);
            if (loaded && t == fileTime) return;
            Parse(File.ReadAllLines(path), out int lines, out int bad);
            fileTime = t;
            Log.Write($"RA01 gun sounds: read {FileName} - {lines} sounds set{(bad > 0 ? $", {bad} line(s) not understood (the built-in choice is used for those)" : "")}");
        }
        catch (Exception e)
        {
            if (!loaded) { Parse(DefaultText().Split('\n'), out _, out _); Log.Write($"RA01 gun sounds: {FileName} can't be used ({e.Message}) - the built-in choices are used"); }
        }
        loaded = true;
    }

    // RA01: the built-in choices first, then each understood line of the file on top. Cost - CPU: extremely low (a hundred short lines); GPU: none.
    static void Parse(string[] fileLines, out int set, out int bad)
    {
        set = bad = 0;
        var m = new Dictionary<string, (string, string)>(); var ty = new Dictionary<string, (string, string)>();
        string f = "", fs = "";
        foreach (string[] text in new[] { DefaultText().Split('\n'), fileLines })
            foreach (string raw in text)
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                string key = eq > 0 ? line[..eq].Trim() : "", value = eq > 0 ? line[(eq + 1)..].Trim() : "";
                string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                bool counted = ReferenceEquals(text, fileLines);
                if (key == "floor" && parts.Length == 1) f = parts[0];
                else if (key == "floor Smart" && parts.Length == 1) fs = parts[0];
                else if (key.StartsWith("type ", StringComparison.Ordinal) && parts.Length == 2) ty[key[5..].Trim()] = (parts[0], parts[1]);
                else if (key.Length > 0 && !key.Contains(' ') && parts.Length == 2) m[key] = (parts[0], parts[1]);
                else { if (counted) bad++; continue; }
                if (counted) set++;
            }
        lock (gate) { models = m; types = ty; floor = f; floorSmart = fs; }
    }

    // RA01: the built-in choices, as the file is first written. Cost - CPU: extremely low (one string build, rarely); GPU: none.
    static string DefaultText()
    {
        var t = new System.Text.StringBuilder();
        t.AppendLine("# RA01 gun reload sounds - which of the game's own sound events each gun plays during a physical reload (Weapons tab: Physical reloading).");
        t.AppendLine("# Edit and save while the game runs: the next reload uses it. Delete this file to get the built-in choices back.");
        t.AppendLine("# <model> = <magazine out sound>, <magazine in sound>   - the model is the gun's audio set (the log line 'RA01 weapon audio set' names it)");
        t.AppendLine("# type <gun type> = <out>, <in>   - for guns whose model isn't listed; 'type default' for anything else");
        t.AppendLine("# floor = <sound>   - a dropped magazine landing ('floor Smart' for Smart guns). Sound names: the game's eventsmetadata.json (w_gun_...).");
        t.AppendLine();
        t.AppendLine("floor = w_gun_mag_metal_hit_floor");
        t.AppendLine("floor Smart = w_gun_mag_plastic_hit_floor");
        t.AppendLine();
        foreach (string prefix in GunSoundSets)
        {
            string model = prefix[(prefix.LastIndexOf('_') + 1)..];
            (string so, string si) = prefix switch
            {
                "w_gun_shotgun_power_carnage" => ("_shell_rattle", "_shell_in"),
                "w_gun_shotgun_power_igla" => ("_open_eject", "_shell_insert"),
                "w_gun_shotgun_power_tactician" => ("", "_shell_in"),
                "w_gun_shotgun_power_testera" => ("_eject", "_shell_in"),
                "w_gun_shotgun_smart_palica" or "w_gun_shotgun_tech_satara" => ("_open", "_shell_in"),
                _ => ("_mag_out", "_mag_in")
            };
            t.AppendLine($"{model} = {(so == "" ? "w_gun_shotgun_power_carnage_shell_rattle" : prefix + so)}, {prefix + si}");   // RA01: the Tactician has no "out" of its own
        }
        t.AppendLine();
        foreach (var (type, prefix) in TypeDefaults)
            t.AppendLine(prefix.Contains("shotgun_power") && !prefix.Contains("zhuo")
                ? $"type {type} = {(prefix.EndsWith("igla") ? prefix + "_open_eject, " + prefix + "_shell_insert" : "w_gun_shotgun_power_carnage_shell_rattle, w_gun_shotgun_power_tactician_shell_in")}"
                : $"type {type} = {prefix}_mag_out, {prefix}_mag_in");
        return t.ToString();
    }

    // RA01: every gun in the game's sound list with a reload pair (eventsmetadata.json, Rob's WolvenKit export); the last word is the model.
    static readonly string[] GunSoundSets =
    {
        "w_gun_borg4a", "w_gun_grit", "w_gun_lmg_power_defender", "w_gun_lmg_power_ma70", "w_gun_metel", "w_gun_osprey",
        "w_gun_pistol_power_lexington", "w_gun_pistol_power_liberty", "w_gun_pistol_power_nue", "w_gun_pistol_power_silverhand", "w_gun_pistol_power_unity",
        "w_gun_pistol_smart_chao", "w_gun_pistol_smart_kappa", "w_gun_pistol_smart_yukimura", "w_gun_pistol_tech_kenshin", "w_gun_pistol_tech_omaha", "w_gun_pistol_toygun",
        "w_gun_revol_power_crusher", "w_gun_revol_power_nova", "w_gun_revol_power_overture", "w_gun_revol_tech_burya", "w_gun_revol_tech_quasar",
        "w_gun_rifle_power_ajax", "w_gun_rifle_power_copperhead", "w_gun_rifle_power_kolac", "w_gun_rifle_power_kyubi", "w_gun_rifle_power_masamune",
        "w_gun_rifle_power_nowaki", "w_gun_rifle_power_pozhar", "w_gun_rifle_power_sor22", "w_gun_rifle_tech_achilles",
        "w_gun_shotgun_power_carnage", "w_gun_shotgun_power_igla", "w_gun_shotgun_power_tactician", "w_gun_shotgun_power_testera", "w_gun_shotgun_smart_palica",
        "w_gun_shotgun_smart_zhuo", "w_gun_shotgun_tech_satara",
        "w_gun_smg_power_guillotine", "w_gun_smg_power_pulsar", "w_gun_smg_power_saratoga", "w_gun_smg_power_umbra", "w_gun_smg_smart_dian", "w_gun_smg_smart_shingen",
        "w_gun_smg_smart_sidewinder", "w_gun_smg_tech_senkoh", "w_gun_sniper_power_grad", "w_gun_sniper_tech_nekomata", "w_gun_ticon", "w_gun_warden"
    };

    // RA01: the per-type stand-ins (the gun type exactly as Core\Probes\WeaponEventProbe names it; ", Smart" added for Smart guns).
    static readonly (string Type, string Prefix)[] TypeDefaults =
    {
        ("Handgun (pistol)", "w_gun_pistol_power_unity"), ("Handgun (pistol), Smart", "w_gun_pistol_smart_kappa"), ("Revolver", "w_gun_revol_power_overture"),
        ("SMG", "w_gun_smg_power_saratoga"), ("SMG, Smart", "w_gun_smg_smart_dian"), ("Assault rifle", "w_gun_rifle_power_ajax"), ("Rifle", "w_gun_rifle_power_ajax"),
        ("Precision rifle", "w_gun_rifle_power_kolac"), ("Sniper rifle", "w_gun_sniper_power_grad"), ("Shotgun", "w_gun_shotgun_power_tactician"),
        ("Shotgun, Smart", "w_gun_shotgun_smart_zhuo"), ("Double-barrel shotgun", "w_gun_shotgun_power_igla"),
        ("Light machine gun", "w_gun_lmg_power_defender"), ("Heavy machine gun", "w_gun_lmg_power_defender"), ("default", "w_gun_rifle_power_ajax")
    };
}
