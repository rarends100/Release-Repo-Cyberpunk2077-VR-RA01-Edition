using System.Collections;
using System.Numerics;
using RA01.Core;
using Xunit;

namespace RA01.Tests;

// RA01: M47 - the small parts: the banner (Core\Banner.cs), Auto tune's headset profiles (Core\UI_SettingsMenus\AutoTune.cs) and the gun calibration file
// (Core\GunCalibration.cs). Builder picture: the small fittings on the job - hinges, labels and the spirit level - each tried once by hand.
// Cost - CPU: extremely low (a few small file writes and string searches); GPU: none.
public sealed class SmallPartsTests : IDisposable
{
    const string Gun = "Test pistol, iron sights";
    readonly Action restoreTuning = Kit.Remember(typeof(Tuning)), restoreTune = Kit.Remember(typeof(AutoTune)), restoreFile = Kit.Remember(typeof(SettingsFile));

    // RA01: gun calibration reads and writes its file in a fresh folder (it follows SettingsFile.Folder), starting empty. Cost - CPU: extremely low; GPU: none.
    public SmallPartsTests()
    {
        SettingsFile.Folder = Kit.NewFolder("guns");
        ForgetGuns();
        Tuning.RecalibrateGuns = false;
    }

    // RA01: everything back as it was. Cost - CPU: extremely low; GPU: none.
    public void Dispose()
    {
        ForgetGuns();
        restoreFile(); restoreTune(); restoreTuning();
    }

    // RA01: GunCalibration as at a fresh start: nothing known, the file not read yet. Cost - CPU: extremely low; GPU: none.
    static void ForgetGuns()
    {
        Kit.Get<IDictionary>(typeof(GunCalibration), "sights").Clear();
        Kit.Set(typeof(GunCalibration), "loaded", 0);
        Kit.Set(typeof(GunCalibration), "measuringKey", "");
        Kit.Set(typeof(GunCalibration), "samples", 0);
    }

    // RA01: a banner shows for its time, then not; each Show is a new version (so the headset repaints it). Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Banner_shows_for_its_time()
    {
        Banner.TryCurrent(out _, out int before);
        Banner.Show("TEST BANNER", 60_000);
        Assert.True(Banner.TryCurrent(out string text, out int version));
        Assert.Equal(("TEST BANNER", before + 1), (text, version));
        Banner.Show("GONE", -1);
        Assert.False(Banner.TryCurrent(out _, out _));
    }

    // RA01: the headset's name picks its profile; the longest matching name part wins ("Quest 3S" over "Quest 3"); an unknown name picks none.
    // Cost - CPU: extremely low; GPU: none.
    [Theory]
    [InlineData("Meta Quest 3", "Meta Quest 3")]
    [InlineData("Oculus Quest 3S", "Meta Quest 3S")]
    [InlineData("Quest 2", "Meta Quest 2")]
    [InlineData("Vive Pro 2", "HTC Vive Pro 2")]
    [InlineData("HTC VIVE Pro", "HTC Vive / Vive Pro")]
    [InlineData("Valve Index", "Valve Index")]
    [InlineData("HP Reverb G2", "HP Reverb G2")]
    [InlineData("Pimax Crystal", "Pimax Crystal")]
    [InlineData("Pimax 8KX", "Pimax 5K / 8K / 12K (wide)")]
    [InlineData("Bigscreen Beyond 2", "Bigscreen Beyond")]
    public void Auto_tune_knows_headsets(string reported, string profile)
    {
        AutoTune.Detect(reported);
        Assert.StartsWith(profile + " (detected)", AutoTune.Text());
    }

    // RA01: an unknown headset is said so, and the picker starts on the first profile. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Auto_tune_unknown_headset()
    {
        AutoTune.Detect("SteamVR/OpenXR : lighthouse");
        Assert.Contains("not detected", AutoTune.Text());
        Assert.Equal(0f, AutoTune.Choice);
    }

    // RA01: every profile's numbers are inside the sliders they set, and Apply sets them (the resolution in 5% steps, from the headset's own size).
    // Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Auto_tune_profiles_fit_their_sliders()
    {
        Tuning.HeadsetHeight = 2688;
        for (int i = 0; i <= (int)AutoTune.Last; i++)
        {
            AutoTune.Choice = i;
            AutoTune.Apply();
            Assert.InRange(Tuning.ViewCoverage, Tuning.MinViewCoverage, Tuning.MaxViewCoverage);
            Assert.InRange(Tuning.GameWidthLimitDeg, Tuning.MinGameWidthDeg, Tuning.MaxGameWidthDeg);
            Assert.InRange(Tuning.GameResolutionPercent, Tuning.MinGameResolutionPercent, Tuning.MaxGameResolutionPercent);
            Assert.True(Tuning.GameResolutionPercent % 5f == 0f, $"profile {i}: resolution {Tuning.GameResolutionPercent}% is not on a 5% step");
        }
        var profiles = Kit.Get<Array>(typeof(AutoTune), "Profiles").Cast<object>().ToArray();
        foreach (object p in profiles)
        {
            float coverage = (float)p.GetType().GetProperty("Coverage")!.GetValue(p)!, width = (float)p.GetType().GetProperty("WidthDeg")!.GetValue(p)!;
            Assert.True(coverage >= Tuning.MinViewCoverage && coverage <= Tuning.MaxViewCoverage && width >= Tuning.MinGameWidthDeg && width <= Tuning.MaxGameWidthDeg, $"profile {p} is outside the sliders' ranges (Apply would clamp it)");
        }
    }

    // RA01: a gun is learned from 5 steady measurements (averaged), saved to RA01_GunCalibration.ini, read back after a restart, and said once on the banner.
    // Cost - CPU: extremely low (one small file write and read); GPU: none.
    [Fact]
    public void Gun_is_learned_saved_and_read_back()
    {
        Assert.True(GunCalibration.Needed(Gun));
        for (int i = 0; i < 4; i++) GunCalibration.Measure(Gun, new Vector3(0, 0.01f * (i + 1), 0.05f), 0.30f, 0.5f);
        Assert.False(GunCalibration.TryGetEntry(Gun, out _), "4 measurements are not enough");
        GunCalibration.Measure(Gun, new Vector3(0, 0.05f, 0.05f), 0.40f, 0.5f);
        Assert.True(GunCalibration.TryGetEntry(Gun, out GunCalibration.Entry e));
        Kit.Near(new Vector3(0, 0.03f, 0.05f), e.Sight, 1e-5f, "averaged sight line");
        Kit.Near(0.32f, e.EyeAhead, 1e-5f, "averaged eye distance");
        Assert.Equal((GunCalibration.DefaultLensMetres, GunCalibration.DefaultLensAhead), (e.LensMetres, e.LensAhead));
        Assert.False(GunCalibration.Needed(Gun));
        Assert.True(Banner.TryCurrent(out string banner, out _) && banner.Contains("GUN LEARNED"), "the first learn says so on the banner");

        ForgetGuns();   // RA01: as after a restart: only the file is left
        Assert.True(GunCalibration.TryGetEntry(Gun, out GunCalibration.Entry back), "read back from RA01_GunCalibration.ini");
        Kit.Near(e.Sight, back.Sight, 1e-4f, "sight line after reading back");
        Kit.Near(e.EyeAhead, back.EyeAhead, 1e-4f, "eye distance after reading back");
    }

    // RA01: shaky measurements (sights more than 1.5 degrees off the barrel, too far, or an odd eye distance) are skipped. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Shaky_measurements_are_skipped()
    {
        for (int i = 0; i < 5; i++) GunCalibration.Measure(Gun, new Vector3(0, 0.03f, 0.05f), 0.3f, 5f);
        for (int i = 0; i < 5; i++) GunCalibration.Measure(Gun, new Vector3(0, 0.5f, 0f), 0.3f, 0.5f);
        for (int i = 0; i < 5; i++) GunCalibration.Measure(Gun, new Vector3(0, 0.03f, 0.05f), 3f, 0.5f);
        Assert.False(GunCalibration.TryGetEntry(Gun, out _));
    }

    // RA01: the scope lens sliders change a learned gun's lens, pulled into their ranges; an unknown gun is refused. Cost - CPU: extremely low; GPU: none.
    [Fact]
    public void Lens_sliders_are_clamped()
    {
        Assert.False(GunCalibration.SetLens("unknown gun", 0.05f, 0.1f));
        for (int i = 0; i < 5; i++) GunCalibration.Measure(Gun, new Vector3(0, 0.03f, 0.05f), 0.3f, 0.5f);
        Assert.True(GunCalibration.SetLens(Gun, 5f, 0f));
        GunCalibration.TryGetEntry(Gun, out GunCalibration.Entry e);
        Assert.Equal((GunCalibration.MaxLensMetres, GunCalibration.MinLensAhead), (e.LensMetres, e.LensAhead));
    }
}
