using System.Globalization;
using System.Numerics;

namespace RA01.Core;

// RA01: M15-2 - GUN CALIBRATION: where each gun's SIGHT LINE sits, measured from its muzzle, so the shot can be ZEROED. The M15-1 test showed V's own guns are
// already straight - the barrel bone runs parallel to the game's sight line (within about 0.1-0.6 degrees) - but the sight line sits a few cm above the
// bore (SMG 5.5 cm, rifles 5-7 cm). A real rifle is "zeroed": the barrel is tipped up a hair so the bullet climbs to cross the sight line at a chosen
// range. Here: the shot leaves the muzzle aimed at the point on the sight line ZeroMetres ahead, so at that range it lands exactly on the sight's centre
// (and within a few cm before and after it).
// How a gun gets calibrated: VRIK (the game's own aim-down-sights pose - the game lines whatever sight is fitted up with its camera) hands in
// measurements while you aim (Measure); SamplesNeeded steady ones are averaged and kept for that gun - keyed by its name and its
// fitted scope (the game's own scope offset, or "iron sights"), so a different scope is a different entry. Kept ones are written to RA01_GunCalibration.ini
// (next to RA01_Settings.ini, plain text, one gun a line) and read back once at start-up: a calibrated gun is never measured again (Needed), unless the
// Dev row "Recalibrate guns" asks for it. Delete the file to recalibrate them all.
// M16-A2 - also kept per gun: where the game puts your EYE behind the sight (how far behind the muzzle along the sight line - the scope lens sits a little
// in front of it, on the scope's eyepiece), and the scope lens's size and its distance in front of that eye, which you set with the Weapons tab sliders
// while aiming through that scope. A line is "key = x y z eye size distance" (older 3-number lines are read too; such a gun is measured once more for its eye).
// Cost - CPU: extremely low (a dictionary lookup per shot; a few sums while measuring an uncalibrated gun; one small file read at start-up and a small
// write when a gun is calibrated); GPU: none.
internal static class GunCalibration
{
    const string FileName = "RA01_GunCalibration.ini";
    public const float ZeroMetres = 50f;                       // RA01: the range at which the shot crosses the sight line
    const int SamplesNeeded = 5;                               // RA01: steady measurements averaged into one calibration
    const float MaxBarrelVsSightDeg = 1.5f, MaxSightCm = 25f;  // RA01: a measurement outside these is the aim pose still moving (or not a sight) - skipped
    public const float DefaultLensMetres = 0.035f, DefaultLensAhead = 0.07f; // RA01: M16-A2 - a scope eyepiece's usual width, and how far in front of the eye it sits
    public const float MinLensMetres = 0.01f, MaxLensMetres = 0.10f, MinLensAhead = 0.02f, MaxLensAhead = 0.25f;
    // RA01: M16-A2 - one gun's numbers: Sight = the sight line's offset from the muzzle (muzzle axes, metres); EyeAhead = how far ahead of the eye the muzzle is,
    // along the sight line (NaN = not measured yet); LensMetres / LensAhead = the scope lens's width and its distance in front of the eye.
    public struct Entry { public Vector3 Sight; public float EyeAhead, LensMetres, LensAhead; }
    static readonly Dictionary<string, Entry> sights = new();
    static readonly object gate = new();
    static string measuringKey = "";
    static Vector3 sum;
    static float eyeSum;
    static int samples;
    static int loaded;

    // RA01: the current gun's key: its name and its fitted scope (or "iron sights"). Empty if no gun yet, its name isn't known yet, or its scope can't be read yet.
    // M17-5 - the rig's bone count is no longer part of it: it depended on which of the gun's skeletons was read first (the same Precision rifle read 17
    // and 27), so one gun got several names. Lines written with a bone count are no longer matched: each gun is measured once more.
    // Cost - CPU: extremely low (a few reads and a short string); GPU: none.
    public static string CurrentKey()
    {
        if (!WeaponProbe.TryGetMuzzle(out _, out _, out _, out _) || WeaponProbe.HeldName.Length == 0) return "";
        if (!GameAnchor.TryScope(out bool has, out Vector4 at)) return "";
        if (has && MathF.Abs(at.X) + MathF.Abs(at.Y) + MathF.Abs(at.Z) < 0.001f) return ""; // RA01: M17-4 - a fitted scope whose offset still reads 0 isn't filled in yet: no name until it is
        string scope = has ? string.Format(CultureInfo.InvariantCulture, "scope ({0:F1} {1:F1} {2:F1} cm)", at.X * 100, at.Y * 100, at.Z * 100) : "iron sights";
        return $"{WeaponProbe.HeldName}, {scope}";
    }

    // RA01: the sight line's offset from the muzzle for this key (muzzle axes, metres). False if the gun isn't calibrated yet.
    // Cost - CPU: extremely low (a lock and a dictionary lookup; the file once at the first call); GPU: none.
    public static bool TryGet(string key, out Vector3 sight)
    {
        bool found = TryGetEntry(key, out Entry e);
        sight = e.Sight;
        return found;
    }

    // RA01: M16-A2 - everything kept for this key. False if the gun isn't calibrated yet.
    // Cost - CPU: extremely low (a lock and a dictionary lookup); GPU: none.
    public static bool TryGetEntry(string key, out Entry entry)
    {
        Load();
        lock (gate) return sights.TryGetValue(key, out entry);
    }

    // RA01: M16-A2 - the Weapons tab sliders: change this gun's scope lens width or distance (clamped) and save the file. False if the gun isn't calibrated.
    // Cost - CPU: extremely low (a lookup and a small file write per slider change); GPU: none.
    public static bool SetLens(string key, float lensMetres, float lensAhead)
    {
        Load();
        lock (gate)
        {
            if (!sights.TryGetValue(key, out Entry e)) return false;
            e.LensMetres = Math.Clamp(lensMetres, MinLensMetres, MaxLensMetres);
            e.LensAhead = Math.Clamp(lensAhead, MinLensAhead, MaxLensAhead);
            sights[key] = e;
        }
        Save();
        return true;
    }

    // RA01: does this gun still need measuring? (not calibrated, or the Dev row asks to recalibrate)
    // Cost - CPU: extremely low (as TryGet); GPU: none.
    public static bool Needed(string key) => key.Length > 0 && (Tuning.RecalibrateGuns || !TryGetEntry(key, out Entry e) || float.IsNaN(e.EyeAhead)); // RA01: M16-A2 - or its eye not measured yet

    // RA01: VRIK, while aiming down the sights: one measurement - the sight line's offset from the muzzle in the muzzle's axes, how far ahead of the game's eye
    // the muzzle is along the sight line (M16-A2), and how far apart the two lines point. SamplesNeeded steady ones in a row for the same key are averaged,
    // kept (a lens size already set for the gun is kept too), written to the file and logged.
    // Cost - CPU: extremely low (a few sums; a small file write once per calibrated gun); GPU: none.
    public static void Measure(string key, Vector3 sightFromMuzzle, float eyeAhead, float barrelVsSightDeg)
    {
        if (!Needed(key) || barrelVsSightDeg > MaxBarrelVsSightDeg || sightFromMuzzle.Length() * 100f > MaxSightCm || !(eyeAhead > 0.05f && eyeAhead < 2f)) return;
        bool known = TryGetEntry(key, out Entry before) && !float.IsNaN(before.EyeAhead);   // RA01: M33-2 - already in the file: no message (a Dev recalibration)
        Entry kept;
        lock (gate)
        {
            if (key != measuringKey) { measuringKey = key; sum = Vector3.Zero; eyeSum = 0; samples = 0; }
            sum += sightFromMuzzle;
            eyeSum += eyeAhead;
            if (++samples < SamplesNeeded) return;
            if (!sights.TryGetValue(key, out kept)) kept = new Entry { LensMetres = DefaultLensMetres, LensAhead = DefaultLensAhead };
            kept.Sight = sum / samples;
            kept.EyeAhead = eyeSum / samples;
            sights[key] = kept;
            measuringKey = ""; samples = 0;
        }
        Log.Write($"RA01 gun calibration: {key} - sight line {kept.Sight.Length() * 100:F1} cm from the muzzle, the eye {kept.EyeAhead * 100:F0} cm behind it; zeroed at {ZeroMetres:F0} m from now on (saved to {FileName})");
        Save();
        if (!known) Banner.Show($"GUN LEARNED: {key.ToUpperInvariant()}");    // RA01: M33-2 - Rob 12:08: a learned thing says so on screen, the first time only
    }

    // RA01: reads the file once (key = x y z per line, # comments). A bad line is skipped.
    // Cost - CPU: extremely low, once (a small file read); GPU: none.
    static void Load()
    {
        if (Interlocked.Exchange(ref loaded, 1) == 1) return;
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return;
            int count = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int eq = line.LastIndexOf(" = ", StringComparison.Ordinal);
                if (line.StartsWith('#') || eq < 0) continue;
                string[] parts = line[(eq + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;
                Entry e = new() { Sight = new Vector3(x, y, z), EyeAhead = Number(parts, 3, float.NaN), LensMetres = Number(parts, 4, DefaultLensMetres), LensAhead = Number(parts, 5, DefaultLensAhead) }; // RA01: M16-A2
                lock (gate) sights[line[..eq]] = e;
                count++;
            }
            Log.Write($"RA01 gun calibration: {count} gun(s) read from {FileName}");
        }
        catch (Exception e) { Log.Write($"RA01 gun calibration: {FileName} could not be read ({e.Message}) - guns will be measured again"); }
    }

    // RA01: M16-A2 - the number at 'index' of a line, or 'missing' if the line is shorter (an older line) or it doesn't read.
    // Cost - CPU: extremely low (one parse, at start-up); GPU: none.
    static float Number(string[] parts, int index, float missing)
        => index < parts.Length && float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : missing;

    // RA01: writes every calibrated gun, one a line, with a short explanation on top.
    // Cost - CPU: extremely low (a small file write, once per newly calibrated gun); GPU: none.
    static void Save()
    {
        try
        {
            var lines = new List<string>
            {
                "# RA01 gun calibration - written by the mod. One gun (name, fitted sight) a line:",
                "# where its sight line sits measured from its muzzle, in the muzzle's own axes (x, y = along the barrel, z), metres;",
                "# then how far ahead of the eye the muzzle is (the game's own aiming pose), the scope lens's width, and its distance in front of the eye (metres).",
                $"# Shots are zeroed to cross the sight line {ZeroMetres:F0} m out. Delete a line (or this file) to measure that gun again.",
            };
            lock (gate)
                foreach (var (key, e) in sights)
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} = {1:F4} {2:F4} {3:F4} {4:F4} {5:F4} {6:F4}", key, e.Sight.X, e.Sight.Y, e.Sight.Z,
                        float.IsNaN(e.EyeAhead) ? "-" : e.EyeAhead.ToString("F4", CultureInfo.InvariantCulture), e.LensMetres, e.LensAhead));
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception e) { Log.Write($"RA01 gun calibration: {FileName} could not be written ({e.Message})"); }
    }

    static string FilePath => Path.Combine(SettingsFile.Folder ?? ModFolders.Settings, FileName); // RA01: M20-4
}
