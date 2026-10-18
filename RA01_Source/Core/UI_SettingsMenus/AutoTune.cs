namespace RA01.Core;

// RA01: M24-2 - AUTO TUNE (Video tab "Auto tune: headset" and "Auto tune: apply"). One press sets the three settings that decide how wide and how sharp the
// game draws - View coverage, Game view width limit and Game resolution - to a good starting point for the headset picked. Everything stays adjustable after.
// The field of view itself always comes from the headset (Projection mode measures it at start-up), so the profile only says how much of that view is
// worth drawing (very wide lenses waste pixels on the outermost rim) and how many pixels each eye gets.
// RESOLUTION BY HEIGHT, not by percent: Game resolution is a percent of the size the runtime recommends per eye, and that size changes with the runtime and
// its own supersampling (Virtual Desktop recommended 2496x2688 for a Quest 3; Link asks less). So each profile names a target picture HEIGHT in pixels per eye,
// and the percent is worked out from the recommended size this headset reported - the same sharpness on any runtime. Rounded to the slider's 5% steps.
// Builder picture: a set of drill bits for each make of wall plug - pick the make, the right bit goes in, and you can still swap it by hand.
// DETECTION: the headset's name as OpenXR reports it (XrSession, at start-up) is matched against each profile's name parts. Through SteamVR the name is often
// just the runtime's, so an unknown name leaves the picker on the first profile and says "not detected - pick yours".
// The starting values are the mod's best judgement from each headset's published lens and panel specs, not measurements - tune by eye after.
// Cost - CPU: extremely low (a short string search once at start-up; a few stores on a press); GPU: none (higher resolution costs GPU, as the slider says).
internal static class AutoTune
{
    // RA01: one headset: its menu name, name parts that identify it (any one, case ignored; the first profile that matches wins, so specific names come first),
    // View coverage %, width limit in degrees, and the per-eye picture height (pixels) aimed for.
    // Cost - CPU: extremely low (a plain data holder, made once); GPU: none.
    sealed record Profile(string Name, string[] Match, float Coverage, float WidthDeg, int EyeHeight);

    static readonly Profile[] Profiles =
    {
        new("Meta Quest 3",        new[] { "Quest 3" },                       100, 100, 1350),  // RA01: 2064x2208 panels, about 110 deg wide - 50% of VD's 2688 = 1344 (the old default)
        new("Meta Quest 3S",       new[] { "Quest 3S" },                      100,  95, 1250),  // RA01: Quest 2 panels and lenses
        new("Meta Quest 2",        new[] { "Quest 2", "Quest2" },             100,  95, 1250),
        new("Meta Quest Pro",      new[] { "Quest Pro" },                     100, 100, 1350),
        new("Valve Index",         new[] { "Index", "Valve" },                100, 105, 1350),
        new("HTC Vive / Vive Pro", new[] { "Vive Pro", "Vive" },              100, 100, 1250),
        new("HTC Vive Pro 2",      new[] { "Vive Pro 2" },                     97, 120, 1600),
        new("HTC Vive Focus / XR Elite", new[] { "Focus", "XR Elite" },       100, 100, 1350),
        new("HP Reverb G2",        new[] { "Reverb", "G2", "Windows Mixed Reality" }, 100, 95, 1550),
        new("Pico 4",              new[] { "Pico" },                          100, 100, 1400),
        new("Bigscreen Beyond",    new[] { "Beyond", "Bigscreen" },           100,  95, 1650),
        new("PlayStation VR2",     new[] { "PlayStation", "PSVR2", "PS VR2" }, 100, 100, 1450),
        new("Pimax Crystal",       new[] { "Crystal" },                        95, 125, 1700),
        new("Pimax 5K / 8K / 12K (wide)", new[] { "Pimax" },                   88, 150, 1500),
        new("Varjo Aero",          new[] { "Varjo", "Aero" },                  97, 120, 1700),
    };

    static int detected = -1;                // RA01: the profile the headset's name matched (-1 none)
    static float choice;                     // RA01: the picker's profile (a float: the menu row is a stepped slider)
    static string result = "";              // RA01: what the last press did, shown on the picker row until it changes

    // RA01: XrSession, once at start-up: picks the profile matching the headset's name (if any) and starts the picker on it. Logged.
    // Cost - CPU: extremely low (a few dozen short string searches, once); GPU: none.
    public static void Detect(string systemName)
    {
        detected = -1;
        int best = 0;                        // RA01: the longest matching name part wins ("Quest 3S" over "Quest 3", "Vive Pro 2" over "Vive")
        for (int i = 0; i < Profiles.Length; i++)
            foreach (string m in Profiles[i].Match)
                if (systemName.Contains(m, StringComparison.OrdinalIgnoreCase) && m.Length > best) { best = m.Length; detected = i; }
        choice = Math.Max(detected, 0);
        Log.Write(detected >= 0 ? $"RA01 auto tune: '{systemName}' is a {Profiles[detected].Name} - the Video tab's Auto tune starts on it"
                                : $"RA01 auto tune: '{systemName}' is not a headset the mod knows by name - pick yours in the Video tab's Auto tune");
    }

    // RA01: the picker row: its value (the profile's number), its range, and setting it. Cost - CPU: extremely low; GPU: none.
    public static float Choice { get => choice; set { choice = Math.Clamp(MathF.Round(value), 0, Profiles.Length - 1); result = ""; } }
    public static float Last => Profiles.Length - 1;

    // RA01: the picker row's text: the headset, whether it was detected, and what a press would set (or what the last press did).
    // Cost - CPU: extremely low (one small string, per repaint of the open window); GPU: none.
    public static string Text()
    {
        int i = (int)choice;
        Profile p = Profiles[i];
        string tag = i == detected ? " (detected)" : detected < 0 ? " (not detected - pick yours)" : "";
        return result.Length > 0 ? $"{p.Name}{tag} - {result}" : $"{p.Name}{tag}: coverage {p.Coverage:0}%, width {p.WidthDeg:0} deg, resolution {Percent(p)}";
    }

    // RA01: the Apply row: sets the three settings to the picked profile. Logged; the resolution takes hold as Game resolution's own tooltip says.
    // Cost - CPU: extremely low (three stores and a log line); GPU: none here.
    public static void Apply()
    {
        Profile p = Profiles[(int)choice];
        Tuning.ViewCoverage = Math.Clamp(p.Coverage, Tuning.MinViewCoverage, Tuning.MaxViewCoverage);
        Tuning.GameWidthLimitDeg = Math.Clamp(p.WidthDeg, Tuning.MinGameWidthDeg, Tuning.MaxGameWidthDeg);
        float percent = PercentValue(p);
        if (percent > 0) Tuning.GameResolutionPercent = percent;
        result = percent > 0 ? "set (resolution from the next start)" : "set (resolution left alone - the headset's size isn't known yet)";
        Log.Write($"RA01 auto tune: {p.Name} - view coverage {Tuning.ViewCoverage:0}%, width limit {Tuning.GameWidthLimitDeg:0} deg, " +
                  (percent > 0 ? $"game resolution {percent:0}% of {Tuning.HeadsetWidth}x{Tuning.HeadsetHeight} (about {p.EyeHeight} px tall per eye)" : "game resolution unchanged (no headset size known yet)"));
    }

    // RA01: the resolution percent that gives this profile's picture height on this headset's recommended size, in the slider's 5% steps (0 = size unknown).
    // Cost - CPU: extremely low (a divide and a round); GPU: none.
    static float PercentValue(Profile p) => Tuning.HeadsetHeight <= 0 ? 0f
        : Math.Clamp(MathF.Round(100f * p.EyeHeight / Tuning.HeadsetHeight / 5f) * 5f, Tuning.MinGameResolutionPercent, Tuning.MaxGameResolutionPercent);
    static string Percent(Profile p) { float v = PercentValue(p); return v > 0 ? $"{v:0}%" : "unchanged"; }
}
