using System.Globalization;
using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - RA01_Settings.ini (Core\UI_SettingsMenus\SettingsFile.cs): every setting that is saved comes back the same when loaded, every saved line is
// understood by the loader, a change to any saved setting is noticed (so it really gets saved), and old or broken files still load safely.
// Each test writes its ini into its own temp folder (SettingsFile.Folder), and Tuning is put back afterwards.
// Builder picture: packing every tool into the van and unpacking it again - nothing may be missing, swapped, or end up in the wrong drawer.
// Cost - CPU: low (about 250 small file writes and reads per run, a fraction of a second); GPU: none.
public sealed class SettingsFileTests : IDisposable
{
    // RA01: every key the file saves, in order. A RENAMED key silently loses every player's setting for it (their old line is ignored), so this is a tripwire.
    static readonly string[] KeySnapshot =
    {
        "PictureSize", "HeadPredictionMs", "FrameLag", "IpdMm", "Stereo", "WideViewDistanceFix", "GameWidthLimit", "ViewCoverage",
        "PictureMode", "FovScale", "PictureShiftRightDeg", "PictureShiftUpDeg", "GameMenuSizeDeg", "GameMenuUpDeg", "GameMenuRightDeg", "MenuWidth",
        "MenuHeight", "MenuX", "MenuY", "GameResolutionPercent", "HeadsetWidth", "HeadsetHeight", "AutoFov", "LeftEyeShiftRightDeg",
        "LeftEyeShiftUpDeg", "RightEyeShiftRightDeg", "RightEyeShiftUpDeg", "EyeTrimsZeroed", "SavedLeftEyeShiftRightDeg", "SavedLeftEyeShiftUpDeg", "SavedRightEyeShiftRightDeg", "SavedRightEyeShiftUpDeg",
        "BodyTurnDeadZoneDeg", "StickPitch", "KeepViewLevel", "FollowStandIn", "FlatAtDevices", "DeviceScreenBackCm", "HudEyePairing", "VrikInScenes",
        "PhysicalCrouch", "CrouchDepthCm", "PhysicalSwimming", "SwimForwardStrength", "SwimSideStrength", "SwimTurnDeadZoneDeg", "WeaponWeight", "PhysicalReload",
        "NoAimZoom", "MonowireLook", "LogCoreVR", "LogWeapons", "LogVRIK", "LogControls", "LogVehicles", "LogGameData",
        "LogMenus", "PouchGlow", "PouchColour", "PouchSize", "DroppedRounds", "ReloadSounds", "LeftHanded", "MenuOnMonitor",
        "WeaponWeightStrength", "PhysicalPowerAttacks", "GestureMode", "ExactPairing", "FrameLagAuto", "RemoteViewControls", "PhysicalLadder", "LadderStrength",
        "LaserSight", "LaserColour", "LaserBrightness", "LaserThicknessMm", "LaserLengthM", "PhysicalSteering", "SteerFeel", "CarViewHeightCm", "CarViewForwardCm",
        "SteerSoftness", "WheelLockDeg", "BarLockDeg", "SteerSpeed", "SteerReturn", "PhysicalAiming", "PhysicalMelee", "SwingSpeed",
        "PhysicalGrenadeThrow", "ThrowStrength", "PhysicalKnifeThrow", "KnifeThrowSpeed", "KnifeThrowStrength", "KnifeAimHelp", "KnifeFlight", "ChestFollowsHead",
        "UprightSpine", "LeanWithHead", "BodyFollowEnabled", "ArmsFollowControllers", "DesktopEye", "UserArmSpan", "RestShoulders", "ArmSizeAuto",
        "ArmSizePercent", "ForearmTwist", "PerHandCyberware", "ScopeMagnifier", "AimStickDpad", "ShoulderScope", "TwoHandGrip", "VirtualStock",
        "TwoHandMelee", "OffHandFingers", "StockStrength", "HudPanel", "HudDistance", "HudWidth", "HudHeight", "HudSide",
        "HudFollow", "CameraForwardCm", "CameraUpCm", "WristLX", "WristLY", "WristLZ", "WristLW", "WristRX",
        "WristRY", "WristRZ", "WristRW",
    };

    // RA01: the word pairs a non-number setting can take (the first of each pair is what Save writes for "on").
    static readonly (string, string)[] Words = { ("on", "off"), ("x", "straight") };

    readonly Action restoreTuning = Kit.Remember(typeof(Tuning)), restoreFile = Kit.Remember(typeof(SettingsFile));
    readonly CultureInfo culture = CultureInfo.CurrentCulture;
    readonly string path;

    // RA01: each test starts from Reset all, with its own empty folder. Cost - CPU: extremely low; GPU: none.
    public SettingsFileTests()
    {
        SettingsFile.Folder = Kit.NewFolder("settings");
        path = Path.Combine(SettingsFile.Folder, "RA01_Settings.ini");
        Tuning.ResetAll();
    }

    // RA01: puts Tuning, SettingsFile and the PC's number format back. Cost - CPU: extremely low; GPU: none.
    public void Dispose()
    {
        CultureInfo.CurrentCulture = culture;
        restoreTuning();
        restoreFile();
    }

    // RA01: small helpers: save now and read the text; write text and load it; the bundle of every saved value (SettingsFile.Current).
    // RA01: Cost - CPU: extremely low (one small file write or read each); GPU: none.
    string SaveText() { Kit.Call(typeof(SettingsFile), "Save"); return File.ReadAllText(path); }
    void LoadText(string text) { File.WriteAllText(path, text); SettingsFile.Load(); }
    static object Current => Kit.GetObj(typeof(SettingsFile), "Current")!;
    static bool Apply(string line) => (bool)Kit.Call(typeof(SettingsFile), "Apply", line)!;
    static IEnumerable<(int index, string key, string value)> Settings(string text) =>
        text.Split('\n').Select((l, i) => (i, l.TrimEnd('\r'))).Where(x => x.Item2.Length > 0 && x.Item2[0] != '#').Select(x => (x.i, x.Item2[..x.Item2.IndexOf('=')], x.Item2[(x.Item2.IndexOf('=') + 1)..]));
    static string Replace(string text, int index, string line) { string[] lines = text.Split('\n'); lines[index] = line + (lines[index].EndsWith('\r') ? "\r" : ""); return string.Join('\n', lines); }

    // RA01: the defaults written, read back and written again give the very same file and the very same values. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Defaults_survive_save_and_load()
    {
        string first = SaveText();
        object before = Current;
        LoadText(first);
        Assert.Equal(before, Current);
        Assert.Equal(first, SaveText());
    }

    // RA01: the list of saved keys is the snapshot's (tripwire), and no key is written twice. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Saved_keys_match_snapshot()
    {
        string[] keys = Settings(SaveText()).Select(s => s.key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        string added = string.Join(", ", keys.Except(KeySnapshot)), gone = string.Join(", ", KeySnapshot.Except(keys));
        Assert.True(added.Length == 0 && gone.Length == 0, Kit.Tripwire($"the saved setting keys (new: [{added}], gone or renamed: [{gone}]) - a renamed key loses players' old setting", "SettingsFileTests.cs, KeySnapshot"));
        Assert.True(keys.SequenceEqual(KeySnapshot), Kit.Tripwire("the order of the saved setting keys", "SettingsFileTests.cs, KeySnapshot"));
    }

    // RA01: every line Save writes is understood by the loader (otherwise that setting is saved but never comes back). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Every_saved_line_is_understood()
    {
        foreach (string line in SaveText().Split('\n').Select(l => l.TrimEnd('\r')))
            Assert.True(Apply(line), $"the loader does not understand the line Save writes: \"{line}\"");
    }

    // RA01: each on/off (or x/straight) setting flipped on its own comes back flipped, nothing else moves, and the change is noticed for saving.
    // Cost - CPU: low (about 70 small load-and-save rounds); GPU: none.
    [Fact]
    public void Every_switch_round_trips()
    {
        string defaults = SaveText();
        object defaultValues = Current;
        int checkedCount = 0;
        foreach ((int index, string key, string value) in Settings(defaults))
        {
            (string a, string b) = Words.FirstOrDefault(w => w.Item1 == value || w.Item2 == value);
            if (a == null) continue;
            string flipped = Replace(defaults, index, $"{key}={(value == a ? b : a)}");
            LoadText(flipped);
            Assert.True(!Current.Equals(defaultValues), $"{key}: flipping it is not noticed by SettingsFile's change check (the Values bundle) - a change made in the game would never be saved");
            string again = SaveText();
            Assert.True(again == flipped, $"{key}: flipped to {(value == a ? b : a)}, but after load + save the file differs:\n{Diff(flipped, again)}");
            LoadText(defaults);
            checkedCount++;
        }
        Assert.True(checkedCount > 50, $"only {checkedCount} switches found - the file's on/off format may have changed");
    }

    // RA01: each number setting, changed on its own to a nearby value inside its range, comes back exactly, nothing else moves, and the change is noticed.
    // Cost - CPU: low (about 60 numbers, a few tries each); GPU: none.
    [Fact]
    public void Every_number_round_trips()
    {
        string defaults = SaveText();
        object defaultValues = Current;
        List<string> failed = new();
        foreach ((int index, string key, string value) in Settings(defaults))
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) continue;
            string format = value.Contains('.') ? "0." + new string('0', value.Length - value.IndexOf('.') - 1) : "0.###";
            float[] tries = { v + 1, v - 1, v * 0.9f, v * 1.1f, v + 0.5f, v - 0.5f, v + 0.1f, v - 0.1f, v + 5, v - 5, 1000, 2000, 0.5f, 1, 50, 64, 150, 300 };
            bool ok = false;
            foreach (float t in tries)
            {
                string candidate = t.ToString(format, CultureInfo.InvariantCulture);
                if (candidate == value || (float.Parse(candidate, CultureInfo.InvariantCulture) == v)) continue;
                string changed = Replace(defaults, index, $"{key}={candidate}");
                LoadText(changed);
                bool noticed = !Current.Equals(defaultValues);
                string again = SaveText();
                LoadText(defaults);
                if (again == changed) { Assert.True(noticed, $"{key}: changing it is not noticed by SettingsFile's change check (the Values bundle) - a change made in the game would never be saved"); ok = true; break; }
                if (Settings(again).Where(s => s.key != key).SequenceEqual(Settings(changed).Where(s => s.key != key)) == false)
                    Assert.Fail($"{key}={candidate}: loading it changed ANOTHER setting:\n{Diff(changed, again)}");
            }
            if (!ok) failed.Add(key);
        }
        Assert.True(failed.Count == 0, $"no changed value came back after save + load for: {string.Join(", ", failed)} (the loader may not set what the saver writes)");
    }

    // RA01: comments, empty lines and the retired keys are understood; nonsense is refused, and NaN or text is never taken as a number.
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Old_and_broken_lines_are_safe()
    {
        foreach (string ok in new[] { "", "# a comment", "; a comment", "SwapEyes=on", "ResolutionBoostPercent=120", "stereo = ON", "STEREO=yes" })
            Assert.True(Apply(ok), $"should be understood: \"{ok}\"");
        foreach (string bad in new[] { "Nonsense=1", "PictureSize=abc", "PictureSize=NaN", "PictureSize=Infinity", "=5", "PictureSize", "Stereo=maybe" })
            Assert.False(Apply(bad), $"should be refused: \"{bad}\"");
        Assert.Equal(1f, Tuning.SizeScale);
    }

    // RA01: on/off are also accepted as true/false, yes/no and 1/0. Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData("on", true)] [InlineData("true", true)] [InlineData("yes", true)] [InlineData("1", true)]
    [InlineData("off", false)] [InlineData("false", false)] [InlineData("no", false)] [InlineData("0", false)]
    public void Switch_words(string word, bool expected)
    {
        Tuning.Stereo = !expected;
        Assert.True(Apply("Stereo=" + word));
        Assert.Equal(expected, Tuning.Stereo);
    }

    // RA01: numbers outside their range are pulled into it; IPD 0 (or below) means "the headset's own"; a silly headset size means "not known".
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Out_of_range_numbers_are_pulled_in()
    {
        Apply("PictureSize=99"); Assert.Equal(Tuning.MaxSize, Tuning.SizeScale);
        Apply("PictureSize=0"); Assert.Equal(Tuning.MinSize, Tuning.SizeScale);
        Apply("IpdMm=10"); Assert.Equal(Tuning.MinIpdMm, Tuning.IpdMm);
        Apply("IpdMm=500"); Assert.Equal(Tuning.MaxIpdMm, Tuning.IpdMm);
        Apply("IpdMm=0"); Assert.Equal(0f, Tuning.IpdMm);
        Apply("IpdMm=-5"); Assert.Equal(0f, Tuning.IpdMm);
        Apply("FrameLag=2.6"); Assert.Equal(3, Tuning.FrameLag);
        Apply("FrameLag=9"); Assert.Equal(Tuning.MaxFrameLag, Tuning.FrameLag);
        Apply("HeadsetWidth=100"); Assert.Equal(0, Tuning.HeadsetWidth);
        Apply("HeadsetWidth=2064"); Assert.Equal(2064, Tuning.HeadsetWidth);
        Apply("MenuWidth=1"); Assert.Equal(Tuning.MinMenuWidth, Tuning.MenuWidth);
    }

    // RA01: a PC set to a comma-decimal language (German, French...) still writes and reads "1.25", never "1,25". Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Comma_decimal_pc_writes_dots()
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Tuning.SizeScale = 1.25f;
        Tuning.StockStrength = 0.55f;
        string text = SaveText();
        Assert.Contains("PictureSize=1.25", text);
        Assert.Contains("StockStrength=0.55", text);
        Assert.DoesNotContain("=1,25", text);
        LoadText("PictureSize=1.5\n");
        Assert.Equal(1.5f, Tuning.SizeScale);
    }

    // RA01: no file yet: the defaults stay, nothing breaks. A file that cannot be read is treated the same. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Missing_file_keeps_defaults()
    {
        object defaults = Current;
        SettingsFile.Load();
        Assert.Equal(defaults, Current);
        Assert.False(File.Exists(path), "loading must not create the file");
    }

    // RA01: the first differing line of two files, for a readable failure message. Cost - CPU: extremely low; GPU: none.
    static string Diff(string expected, string actual)
    {
        string[] e = expected.Split('\n'), a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
            if (i >= e.Length || i >= a.Length || e[i] != a[i]) return $"  line {i + 1}: expected \"{(i < e.Length ? e[i].Trim() : "")}\", got \"{(i < a.Length ? a[i].Trim() : "")}\"";
        return "  (same)";
    }
}
