using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RA01.Core;

// RA01: M5a-2, M5a-3, M5a-4 - keeps the adjustable values (Tuning) in a plain text file, RA01_Settings.ini, in the folder RA01.dll was loaded from (next to RA01.Launcher.exe).
// it is read once at start, and written a moment after the values stop changing, so nothing is lost and a slider being dragged does not write the file hundreds of times.
// nothing in here may ever throw into the game: a file that cannot be read or written is logged and the built-in defaults are used.
// Cost - CPU: low (reads the small ini file once at start and rewrites it a moment after a change; per headset frame only a compare of the Values bundle above); GPU: none (no GPU work).
internal static class SettingsFile
{
    const string FileName = "RA01_Settings.ini";
    static readonly long SaveAfter = Stopwatch.Frequency / 2;  // RA01: half a second without a change before the file is written

    // RA01: the folder the file lives in. Null = the folder RA01.dll was loaded from (the tests point it somewhere else).
    public static string? Folder = null;

    // RA01: every value that is saved, in one comparable bundle, so a change is noticed with one comparison and no allocation.
    // M5a-12 - the resolution and AutoFov settings used to sit outside this bundle (they were settings-file only, changed by hand in the ini between game runs, so they did not need live change-detection). Now that they all have an F10 menu row too, they are dragged and clicked like everything else and belong in here, so a change is noticed and saved the same half-second-later way.
    // Cost - CPU: extremely low (a plain bundle of numbers on the stack, compared once per headset frame); GPU: none (no GPU work).
    readonly record struct Values(float Size, float Prediction, float Ipd, int Lag, bool Stereo, float Fov, float ShiftX, float ShiftY, float MenuFov, int MenuW, int MenuH, int MenuX, int MenuY,
        bool AutoFov, float ResPercent, int HeadsetW, int HeadsetH, float EyeLX, float EyeLY, float EyeRX, float EyeRY,
        bool EyeTrimsZeroed, float SavedEyeLX, float SavedEyeLY, float SavedEyeRX, float SavedEyeRY, float BodyTurnDeadZoneDeg, bool StickPitch, bool PhysicalCrouch, float CrouchDepth, bool PhysicalAiming, bool PhysicalMelee, float SwingSpeed, int DesktopEye, bool ChestFollowsHead, bool UprightSpine, bool LeanWithHead, bool BodyFollowEnabled, bool ArmsFollow, float UserArmSpan, bool RestShoulders, bool ArmSizeAuto, float ArmSizePercent, bool ForearmTwist, bool HudPanel, float HudDistance, float HudWidth, float HudHeight, int HudFollow, float CamFwd, float CamUp, float WLX, float WLY, float WLZ, float WLW, float WRX, float WRY, float WRZ, float WRW, float MenuUp, bool PerHand, bool Scope, bool AimStick, bool ShoulderScope, bool TwoHand, bool Stock, float StockStrength, bool TwoHandMelee, bool OffHandFingers, bool Steering, float WheelLock, float BarLock, float SteerSpeed, float SteerReturn, int SteerFeel, float SteerSoftness, float CarView, float CarViewFwd, bool KeepLevel, bool StandIn, int VrikScenes, int PictureMode, float ViewCoverage, float GameWidthLimit, float HudSide, bool DistanceFix, bool Swim, float SwimForward, float SwimSide, float SwimDeadZone, bool Throw, float ThrowStrength, bool KnifeThrow, float KnifeSpeed, float KnifeStrength, float KnifeAimHelp, float KnifeFlight, int HudEyes, bool FlatDevices, float ScreenBack,
        bool RemoteView, bool Ladder, float LadderStrength, bool Laser, int LaserColour, float LaserBrightness, float LaserThickness, float LaserLength, bool LagAuto, bool ExactPairing, bool Gestures, bool PowerAttacks, bool Weight, float WeightStrength, bool Reload, bool LeftHanded, bool MenuOnMonitor, bool PouchGlow, int PouchColour, float PouchSize, bool ReloadSounds, int DroppedRounds, bool NoAimZoom, bool MonowireX, int LogMask, float GameMenuRight); // RA01: M24, M25, M26, M27, M35, M38 (M27-5c: the pouch glow; M27-6g: its size; M27-6k: reload sounds)
    // RA01: the current value of every saved setting, read from Tuning.
    // RA01: Cost - CPU: extremely low (a field read per value, no allocation, once per headset frame); GPU: none (no GPU work).
    static Values Current => new(Tuning.SizeScale, Tuning.PredictionMs, Tuning.IpdMm, Tuning.FrameLag, Tuning.Stereo, Tuning.FovScale, Tuning.ShiftRightDeg, Tuning.ShiftUpDeg, Tuning.GameMenuFov, Tuning.MenuWidth, Tuning.MenuHeight, Tuning.MenuX, Tuning.MenuY,
        Tuning.AutoFov, Tuning.GameResolutionPercent, Tuning.HeadsetWidth, Tuning.HeadsetHeight, Tuning.LeftEyeShiftRightDeg, Tuning.LeftEyeShiftUpDeg, Tuning.RightEyeShiftRightDeg, Tuning.RightEyeShiftUpDeg,
        Tuning.EyeTrimsZeroed, Tuning.SavedLeftEyeShiftRightDeg, Tuning.SavedLeftEyeShiftUpDeg, Tuning.SavedRightEyeShiftRightDeg, Tuning.SavedRightEyeShiftUpDeg, Tuning.BodyTurnDeadZoneDeg, Tuning.StickPitch, Tuning.PhysicalCrouch, Tuning.CrouchDepthCm, Tuning.PhysicalAiming, Tuning.PhysicalMelee, Tuning.SwingSpeed, Tuning.DesktopEye, Tuning.ChestFollowsHead, Tuning.UprightSpine, Tuning.LeanWithHead, Tuning.BodyFollowEnabled, Tuning.ArmsFollowControllers, Tuning.UserArmSpan, Tuning.RestShoulders, Tuning.ArmSizeAuto, Tuning.ArmSizePercent, Tuning.ForearmTwist, Tuning.HudPanel, Tuning.HudDistance, Tuning.HudWidth, Tuning.HudHeight, Tuning.HudFollow, Tuning.CameraForwardCm, Tuning.CameraUpCm,
        Tuning.WristLX, Tuning.WristLY, Tuning.WristLZ, Tuning.WristLW, Tuning.WristRX, Tuning.WristRY, Tuning.WristRZ, Tuning.WristRW, Tuning.GameMenuUpDeg, Tuning.PerHandCyberware, Tuning.ScopeMagnifier, Tuning.AimStickDpad, Tuning.ShoulderScope, Tuning.TwoHandGrip, Tuning.VirtualStock, Tuning.StockStrength, Tuning.TwoHandMelee, Tuning.OffHandFingers, Tuning.PhysicalSteering, Tuning.WheelLockDeg, Tuning.BarLockDeg, Tuning.SteerSpeed, Tuning.SteerReturn, Tuning.SteerFeel, Tuning.SteerSoftness, Tuning.CarViewHeightCm, Tuning.CarViewForwardCm, Tuning.KeepViewLevel, Tuning.FollowStandIn, Tuning.VrikInScenes, Tuning.PictureMode, Tuning.ViewCoverage, Tuning.GameWidthLimitDeg, Tuning.HudSide, Tuning.WideViewDistanceFix, Tuning.PhysicalSwim, Tuning.SwimForwardStrength, Tuning.SwimSideStrength, Tuning.SwimTurnDeadZoneDeg, Tuning.PhysicalThrow, Tuning.ThrowStrength, Tuning.PhysicalKnifeThrow, Tuning.KnifeThrowSpeed, Tuning.KnifeThrowStrength, Tuning.KnifeAimHelp, Tuning.KnifeFlight, Tuning.HudEyePairing, Tuning.FlatAtDevices, Tuning.DeviceScreenBackCm,
        Tuning.RemoteViewControls, Tuning.PhysicalLadder, Tuning.LadderStrength, Tuning.LaserSight, Tuning.LaserColour, Tuning.LaserBrightness, Tuning.LaserThicknessMm, Tuning.LaserLengthM, Tuning.FrameLagAuto, Tuning.ExactPairing, Tuning.GestureMode, Tuning.PhysicalPowerAttacks, Tuning.WeaponWeight, Tuning.WeaponWeightStrength, Tuning.PhysicalReload, Tuning.LeftHanded, Tuning.MenuOnMonitor, Tuning.PouchGlow, Tuning.PouchColour, Tuning.PouchSize, Tuning.ReloadSounds, Tuning.DroppedRounds, Tuning.NoAimZoom, Tuning.MonowireX, Tuning.LogMask, Tuning.GameMenuRightDeg);

    static bool sawSteerFeel;       // RA01: M19-4 - the file had a SteerFeel line
    static Values seen;             // RA01: the values at the last look
    static long changedAt;          // RA01: Stopwatch time of the last change that is not saved yet (0 = nothing waiting)
    static bool saveFailLogged;     // RA01: a file that cannot be written is said once, not on every change

    // RA01: the full path of the settings file.
    // RA01: Cost - CPU: extremely low (one string join, only when the file is read or written); GPU: none (no GPU work).
    static string FilePath => System.IO.Path.Combine(Folder ?? ModFolders.Settings, FileName); // RA01: M20-4 - the launch folder's Settings folder

    // RA01: reads the file, if there is one. A missing file leaves the defaults; a line that makes no sense, or a number outside its range, is ignored or pulled into the range.
    // Cost - CPU: low (once at start-up; reads a file of about 30 lines (about a millisecond of disk time) and writes log lines); GPU: none (no GPU work).
    public static void Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                Log.Write($"RA01 settings: no {FileName} yet, using the defaults (it is created the first time something is changed)");
            }
            else
            {
                foreach (string raw in File.ReadAllLines(path))
                    if (!Apply(raw.Trim())) Log.Write($"RA01 settings: ignored the line \"{raw.Trim()}\"");
                if (!sawSteerFeel) Tuning.SetSteerFeel(0); // RA01: M19-4 - a file from before the steering feel: start on Performance (the old 180-degree wheel felt stiff)
                Log.Write($"RA01 settings: read {FileName}: picture size x{Tuning.SizeScale:F2}, head prediction {Tuning.PredictionMs:F0} ms, frame lag {Tuning.FrameLag}, {(Tuning.IpdMm == 0 ? "IPD the headset's own" : $"IPD {Tuning.IpdMm:F1} mm")}, stereo {(Tuning.Stereo ? "on" : "off")}, game field of view x{Tuning.FovScale:F2}, picture moved {Tuning.ShiftRightDeg:F1} right / {Tuning.ShiftUpDeg:F1} up, game menu size {Tuning.GameMenuFov:F0}, F10 window {Tuning.MenuWidth}x{Tuning.MenuHeight}");
            }
        }
        catch (Exception e)
        {
            Log.Write($"RA01 settings: could not read {FileName} ({e.Message}), using the defaults");
        }
        seen = Current; // RA01: what was just loaded is not a change
        changedAt = 0;
    }

    // RA01: once per headset frame. Notices a change, and writes the file once the values have been still for half a second.
    // Cost - CPU: extremely low (about 90 times a second; one compare of 13 numbers, and the Save cost only once half a second after a change); GPU: none (no GPU work).
    public static void Poll(long now)
    {
        DistanceScaleFix.Sync();    // RA01: M21-9c - a loaded or reset value reaches the distance fix's cap
        Values values = Current;
        if (values != seen)
        {
            seen = values;
            changedAt = now;
        }
        else if (changedAt != 0 && now - changedAt >= SaveAfter)
        {
            changedAt = 0;
            Save();
        }
    }

    // RA01: at shutdown: a change made in the last half second is written now instead of being lost.
    // Cost - CPU: extremely low (once at shutdown; one check, plus a Save (low) only if a change is waiting); GPU: none (no GPU work).
    public static void Flush()
    {
        if (changedAt == 0) return;
        changedAt = 0;
        Save();
    }

    // RA01: one line "Name=value". False = the line is not a setting (comments and empty lines are fine and count as understood).
    // Cost - CPU: extremely low (a string split and a switch per line, about 15 lines, at start-up only); GPU: none (no GPU work).
    static bool Apply(string line)
    {
        if (line.Length == 0 || line[0] == '#' || line[0] == ';') return true;
        int equals = line.IndexOf('=');
        if (equals <= 0) return false;
        string name = line[..equals].Trim(), value = line[(equals + 1)..].Trim();
        switch (name.ToLowerInvariant())
        {
            case "picturesize": return Number(value, v => Tuning.SizeScale = Math.Clamp(v, Tuning.MinSize, Tuning.MaxSize));
            case "headpredictionms": return Number(value, v => Tuning.PredictionMs = Math.Clamp(v, 0f, Tuning.MaxPredictionMs));
            case "ipdmm": return Number(value, v => Tuning.IpdMm = v <= 0 ? 0 : Math.Clamp(v, Tuning.MinIpdMm, Tuning.MaxIpdMm));
            case "framelag": return Number(value, v => Tuning.FrameLag = (int)Math.Round(Math.Clamp(v, 0f, Tuning.MaxFrameLag)));
            case "stereo": return Switch(value, v => Tuning.Stereo = v);
            case "wideviewdistancefix": return Switch(value, v => Tuning.WideViewDistanceFix = v); // RA01: M21-9c
            case "gamewidthlimit": return Number(value, v => Tuning.GameWidthLimitDeg = Math.Clamp(v, Tuning.MinGameWidthDeg, Tuning.MaxGameWidthDeg)); // RA01: M5b-2b
            case "viewcoverage": return Number(value, v => Tuning.ViewCoverage = Math.Clamp(v, Tuning.MinViewCoverage, Tuning.MaxViewCoverage)); // RA01: M5b-2
            case "picturemode": return Number(value, v => Tuning.PictureMode = Math.Clamp((int)MathF.Round(v), 0, 1)); // RA01: M5b-1
            case "swapeyes": return true; // RA01: M5a-9 - the eye swap is no longer saved (a stray key would stick for the next start); an old file's line is understood and ignored
            case "fovscale": return Number(value, v => Tuning.FovScale = Math.Clamp(v, Tuning.MinFovScale, Tuning.MaxFovScale));
            case "pictureshiftrightdeg": return Number(value, v => Tuning.ShiftRightDeg = Math.Clamp(v, -Tuning.MaxShiftDeg, Tuning.MaxShiftDeg));
            case "pictureshiftupdeg": return Number(value, v => Tuning.ShiftUpDeg = Math.Clamp(v, -Tuning.MaxShiftDeg, Tuning.MaxShiftDeg));
            case "gamemenusizedeg": return Number(value, v => Tuning.GameMenuFov = Math.Clamp(v, Tuning.MinGameMenuFov, Tuning.MaxGameMenuFov));
            case "gamemenuupdeg": return Number(value, v => Tuning.GameMenuUpDeg = Math.Clamp(v, -Tuning.MaxGameMenuUpDeg, Tuning.MaxGameMenuUpDeg)); // RA01: M13-63
            case "gamemenurightdeg": return Number(value, v => Tuning.GameMenuRightDeg = Math.Clamp(v, -Tuning.MaxGameMenuRightDeg, Tuning.MaxGameMenuRightDeg)); // RA01: M38
            case "menuwidth": return Number(value, v => Tuning.MenuWidth = (int)Math.Round(Math.Clamp(v, Tuning.MinMenuWidth, Tuning.MaxMenuWidth)));
            case "menuheight": return Number(value, v => Tuning.MenuHeight = (int)Math.Round(Math.Clamp(v, Tuning.MinMenuHeight, Tuning.MaxMenuHeight)));
            case "menux": return Number(value, v => Tuning.MenuX = (int)Math.Round(Math.Clamp(v, 0f, Tuning.MaxMenuWidth)));
            case "menuy": return Number(value, v => Tuning.MenuY = (int)Math.Round(Math.Clamp(v, 0f, Tuning.MaxMenuHeight)));
            case "gameresolutionpercent": return Number(value, v => Tuning.GameResolutionPercent = Math.Clamp(v, Tuning.MinGameResolutionPercent, Tuning.MaxGameResolutionPercent)); // RA01: M12-2
            case "headsetwidth": return Number(value, v => Tuning.HeadsetWidth = v >= 256 && v <= 16384 ? (int)v : 0);
            case "headsetheight": return Number(value, v => Tuning.HeadsetHeight = v >= 256 && v <= 16384 ? (int)v : 0);
            case "resolutionboostpercent": return true; // RA01: M12-2 - the old boost (M12-0b) is gone; an old file's line is understood and ignored
            case "autofov": return Switch(value, v => Tuning.AutoFov = v);
            case "lefteyeshiftrightdeg": return Number(value, v => Tuning.LeftEyeShiftRightDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "lefteyeshiftupdeg": return Number(value, v => Tuning.LeftEyeShiftUpDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "righteyeshiftrightdeg": return Number(value, v => Tuning.RightEyeShiftRightDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "righteyeshiftupdeg": return Number(value, v => Tuning.RightEyeShiftUpDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            // RA01: M5a-14 - loaded as plain fields, not through Tuning.SetEyeTrimsZeroed: the file already holds a consistent snapshot (the four eye-trim lines above are whatever they were left at, zeroed or not, and these four lines are the remembered values), so re-running the snapshot/zero logic here would clobber one with the other depending on line order.
            case "eyetrimszeroed": return Switch(value, v => Tuning.EyeTrimsZeroed = v);
            case "savedlefteyeshiftrightdeg": return Number(value, v => Tuning.SavedLeftEyeShiftRightDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "savedlefteyeshiftupdeg": return Number(value, v => Tuning.SavedLeftEyeShiftUpDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "savedrighteyeshiftrightdeg": return Number(value, v => Tuning.SavedRightEyeShiftRightDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "savedrighteyeshiftupdeg": return Number(value, v => Tuning.SavedRightEyeShiftUpDeg = Math.Clamp(v, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg));
            case "bodyturndeadzonedeg": return Number(value, v => Tuning.BodyTurnDeadZoneDeg = Math.Clamp(v, Tuning.MinBodyTurnDeadZoneDeg, Tuning.MaxBodyTurnDeadZoneDeg)); // RA01: M9-8
            case "stickpitch": return Switch(value, v => Tuning.StickPitch = v);
            case "keepviewlevel": return Switch(value, v => Tuning.KeepViewLevel = v); // RA01: M20-5
            case "followstandin": return Switch(value, v => Tuning.FollowStandIn = v); // RA01: M20-6
            case "devicescreenbackcm": return Number(value, v => Tuning.DeviceScreenBackCm = Math.Clamp(v, 0f, 40f)); // RA01: M23-2c
            case "flatatdevices": return Switch(value, v => Tuning.FlatAtDevices = v); // RA01: M23-2
            case "hudeyepairing": return Number(value, v => Tuning.HudEyePairing = Math.Clamp((int)MathF.Round(v), 0, 2)); // RA01: M23-0
            case "vrikinscenes": return Number(value, v => Tuning.VrikInScenes = Math.Clamp((int)MathF.Round(v), 0, 2)); // RA01: M20-10
            case "physicalaiming": return Switch(value, v => Tuning.PhysicalAiming = v); // RA01: M13-2
            case "physicalmelee": return Switch(value, v => Tuning.PhysicalMelee = v); // RA01: M13-9
            case "physicalgrenadethrow": return Switch(value, v => Tuning.PhysicalThrow = v); // RA01: M22-4
            case "throwstrength": return Number(value, v => Tuning.ThrowStrength = Math.Clamp(v, Tuning.MinThrowStrength, Tuning.MaxThrowStrength)); // RA01: M22-4
            case "physicalknifethrow": return Switch(value, v => Tuning.PhysicalKnifeThrow = v); // RA01: M22-5
            case "knifethrowspeed": return Number(value, v => Tuning.KnifeThrowSpeed = Math.Clamp(v, Tuning.MinKnifeThrowSpeed, Tuning.MaxKnifeThrowSpeed)); // RA01: M22-5
            case "knifethrowstrength": return Number(value, v => Tuning.KnifeThrowStrength = Math.Clamp(v, Tuning.MinThrowStrength, Tuning.MaxThrowStrength)); // RA01: M22-5b
            case "knifeaimhelp": return Number(value, v => Tuning.KnifeAimHelp = Math.Clamp(v, 0f, 1f)); // RA01: M22-5b
            case "knifeflight": return Number(value, v => Tuning.KnifeFlight = Math.Clamp(v, 0f, 1f)); // RA01: M22-5c
            case "swingspeed": return Number(value, v => Tuning.SwingSpeed = Math.Clamp(v, Tuning.MinSwingSpeed, Tuning.MaxSwingSpeed)); // RA01: M13-9
            case "fistswingspeed": case "fingertracking": return true; // RA01: M13-18 - removed for now (fists and finger tracking come back later); an old file's line is understood and ignored
            case "physicalcrouch": return Switch(value, v => Tuning.PhysicalCrouch = v); // RA01: M11-2
            case "physicalsteering": return Switch(value, v => Tuning.PhysicalSteering = v); // RA01: M19-1
            case "wheellockdeg": return Number(value, v => Tuning.WheelLockDeg = Math.Clamp(v, 60f, 360f));
            case "steerfeel": sawSteerFeel = true; return Number(value, v => Tuning.SteerFeel = Math.Clamp((int)MathF.Round(v), 0, 2)); // RA01: M19-4 - only the label; the values come from their own lines
            case "carviewheightcm": return Number(value, v => Tuning.CarViewHeightCm = Math.Clamp(v, -20f, 10f)); // RA01: M20-1
            case "carviewforwardcm": return Number(value, v => Tuning.CarViewForwardCm = Math.Clamp(v, -10f, 20f)); // RA01: M46
            case "steersoftness": return Number(value, v => Tuning.SteerSoftness = Math.Clamp(v, 0f, 0.9f));
            case "barlockdeg": return Number(value, v => Tuning.BarLockDeg = Math.Clamp(v, 15f, 60f));
            case "steerspeed": return Number(value, v => Tuning.SteerSpeed = Math.Clamp(v, 1f, 20f));
            case "steerreturn": return Number(value, v => Tuning.SteerReturn = Math.Clamp(v, 0.5f, 8f));
            case "physicalswimming": return Switch(value, v => Tuning.PhysicalSwim = v); // RA01: M22-1
            case "swimforwardstrength": return Number(value, v => Tuning.SwimForwardStrength = Math.Clamp(v, Tuning.MinSwimStrength, Tuning.MaxSwimForwardStrength)); // RA01: M22-1b
            case "swimturndeadzonedeg": return Number(value, v => Tuning.SwimTurnDeadZoneDeg = Math.Clamp(v, Tuning.MinSwimTurnDeadZoneDeg, Tuning.MaxSwimTurnDeadZoneDeg)); // RA01: M22-1e
            case "swimsidestrength": return Number(value, v => Tuning.SwimSideStrength = Math.Clamp(v, Tuning.MinSwimStrength, Tuning.MaxSwimSideStrength)); // RA01: M22-1b
            case "weaponweight": return Switch(value, v => Tuning.WeaponWeight = v); // RA01: M27-4
            case "physicalreload": return Switch(value, v => Tuning.PhysicalReload = v); // RA01: M27-5
            case "noaimzoom": return Switch(value, v => Tuning.NoAimZoom = v); // RA01: M35
            case "logcorevr": return Switch(value, v => Tuning.LogOn[Log.CoreVr] = v); // RA01: M41
            case "logweapons": return Switch(value, v => Tuning.LogOn[Log.Weapons] = v);
            case "logvrik": return Switch(value, v => Tuning.LogOn[Log.Vrik] = v);
            case "logcontrols": return Switch(value, v => Tuning.LogOn[Log.Controls] = v);
            case "logvehicles": return Switch(value, v => Tuning.LogOn[Log.Vehicles] = v);
            case "loggamedata": return Switch(value, v => Tuning.LogOn[Log.GameData] = v);
            case "logmenus": return Switch(value, v => Tuning.LogOn[Log.Menus] = v);
            case "monowirelook": if (value.Equals("x", StringComparison.OrdinalIgnoreCase)) Tuning.MonowireX = true; else if (value.Equals("straight", StringComparison.OrdinalIgnoreCase)) Tuning.MonowireX = false; else return false; return true; // RA01: M38-1d - x / straight
            case "pouchglow": return Switch(value, v => Tuning.PouchGlow = v); // RA01: M27-5c
            case "droppedrounds": return Number(value, v => Tuning.DroppedRounds = Math.Clamp((int)v, 0, 1)); // RA01: M27-7
            case "reloadsounds": return Switch(value, v => Tuning.ReloadSounds = v); // RA01: M27-6k
            case "pouchsize": return Number(value, v => Tuning.PouchSize = Math.Clamp(v, Tuning.MinPouchSize, Tuning.MaxPouchSize)); // RA01: M27-6g
            case "pouchcolour": return Number(value, v => Tuning.PouchColour = Math.Clamp((int)v, 0, PouchGlow.Colours.Length - 1)); // RA01: M27-5c
            case "lefthanded": return Switch(value, v => Tuning.LeftHanded = v); // RA01: M28-3
            case "menuonmonitor": return Switch(value, v => Tuning.MenuOnMonitor = v); // RA01: M29-1
            case "lefthandguns": return Switch(value, v => Tuning.LeftHanded = v); // RA01: M28-3 - a file from the short-lived M28-1/2 split settings: its gun hand becomes the mode
            case "lefthandmelee": case "swapsticks": return true;              // RA01: M28-3 - retired (one switch now); quietly dropped on the next save
            case "weaponweightstrength": return Number(value, v => Tuning.WeaponWeightStrength = Math.Clamp(v, 0f, 2f));
            case "physicalpowerattacks": return Switch(value, v => Tuning.PhysicalPowerAttacks = v); // RA01: M27-2
            case "gesturemode": return Switch(value, v => Tuning.GestureMode = v); // RA01: M27-1
            case "exactpairing": return Switch(value, v => Tuning.ExactPairing = v); // RA01: M26-1
            case "framelagauto": return Switch(value, v => Tuning.FrameLagAuto = v); // RA01: M25-1
            case "remoteviewcontrols": return Switch(value, v => Tuning.RemoteViewControls = v); // RA01: M24-1
            case "physicalladder": return Switch(value, v => Tuning.PhysicalLadder = v); // RA01: M24-3
            case "ladderstrength": return Number(value, v => Tuning.LadderStrength = Math.Clamp(v, Tuning.MinLadderStrength, Tuning.MaxLadderStrength));
            case "lasersight": return Switch(value, v => Tuning.LaserSight = v); // RA01: M24-4
            case "lasercolour": return Number(value, v => Tuning.LaserColour = Math.Clamp((int)v, 0, HandStrip.Colours.Length - 1));
            case "laserbrightness": return Number(value, v => Tuning.LaserBrightness = Math.Clamp(v, 0.2f, 1f));
            case "laserthicknessmm": return Number(value, v => Tuning.LaserThicknessMm = Math.Clamp(v, Tuning.MinLaserThicknessMm, Tuning.MaxLaserThicknessMm));
            case "laserlengthm": return Number(value, v => Tuning.LaserLengthM = Math.Clamp(v, Tuning.MinLaserLengthM, Tuning.MaxLaserLengthM));
            case "crouchdepthcm": return Number(value, v => Tuning.CrouchDepthCm = Math.Clamp(v, Tuning.MinCrouchDepthCm, Tuning.MaxCrouchDepthCm)); // RA01: M11-3b
            case "crouchdepthpercent": return true; // RA01: M11-3b - the old percent line is understood and ignored
            case "chestfollowshead": return Switch(value, v => Tuning.ChestFollowsHead = v);
            case "uprightspine": return Switch(value, v => Tuning.UprightSpine = v); // RA01: M11-1
            case "leanwithhead": return Switch(value, v => Tuning.LeanWithHead = v);
            case "bodyfollowenabled": return Switch(value, v => Tuning.BodyFollowEnabled = v);
            case "armsfollowcontrollers": return Switch(value, v => Tuning.ArmsFollowControllers = v); // RA01: M8-3
            case "desktopeye": return Number(value, v => Tuning.DesktopEye = Math.Clamp((int)MathF.Round(v), 0, 2)); // RA01: M13-16
            case "userarmspan": return Number(value, v => Tuning.UserArmSpan = v >= 1.0f && v <= 2.6f ? v : 0); // RA01: M9-2 - anything outside a human span means "not measured"
            case "restshoulders": return Switch(value, v => Tuning.RestShoulders = v);
            case "armsizeauto": return Switch(value, v => Tuning.ArmSizeAuto = v); // RA01: M9-6
            case "hudpanel": return Switch(value, v => Tuning.HudPanel = v); // RA01: M10-1
            case "huddistance": return Number(value, v => Tuning.HudDistance = Math.Clamp(v, Tuning.MinHudDistance, Tuning.MaxHudDistance));
            case "hudwidth": return Number(value, v => Tuning.HudWidth = Math.Clamp(v, Tuning.MinHudWidth, Tuning.MaxHudWidth));
            case "hudside": return Number(value, v => Tuning.HudSide = Math.Clamp(v, -Tuning.MaxHudHeight, Tuning.MaxHudHeight)); // RA01: M21-5
            case "hudheight": return Number(value, v => Tuning.HudHeight = Math.Clamp(v, -Tuning.MaxHudHeight, Tuning.MaxHudHeight));
            case "hudfollow": return Number(value, v => Tuning.HudFollow = v >= 0.5f ? 1 : 0);
            case "forearmtwist": return Switch(value, v => Tuning.ForearmTwist = v); // RA01: M9-7
            case "perhandcyberware": return Switch(value, v => Tuning.PerHandCyberware = v); // RA01: M14-1b
            case "shoulderaim": return true; // RA01: M16-E - shoulder to aim was removed; an older settings file's line is quietly skipped
            case "scopemagnifier": return Switch(value, v => Tuning.ScopeMagnifier = v); // RA01: M16-A
            case "aimstickdpad": return Switch(value, v => Tuning.AimStickDpad = v); // RA01: M16-B
            case "shoulderscope": return Switch(value, v => Tuning.ShoulderScope = v); // RA01: M17-1
            case "twohandgrip": return Switch(value, v => Tuning.TwoHandGrip = v); // RA01: M18-0
            case "virtualstock": return Switch(value, v => Tuning.VirtualStock = v); // RA01: M18-1
            case "twohandmelee": return Switch(value, v => Tuning.TwoHandMelee = v); // RA01: M18-2
            case "offhandfingers": return Switch(value, v => Tuning.OffHandFingers = v); // RA01: M18-4
            case "stockstrength": return Number(value, v => Tuning.StockStrength = Math.Clamp(v, 0f, 1f)); // RA01: M18-1
            case "armsizepercent": return Number(value, v => Tuning.ArmSizePercent = Math.Clamp(v, Tuning.MinArmSizePercent, Tuning.MaxArmSizePercent));
            case "cameraforwardcm": return Number(value, v => Tuning.CameraForwardCm = Math.Clamp(v, -Tuning.MaxCameraShiftCm, Tuning.MaxCameraShiftCm));
            case "cameraupcm": return Number(value, v => Tuning.CameraUpCm = Math.Clamp(v, -Tuning.MaxCameraShiftCm, Tuning.MaxCameraShiftCm));
            case "wristlx": return Number(value, v => Tuning.WristLX = v);
            case "wristly": return Number(value, v => Tuning.WristLY = v);
            case "wristlz": return Number(value, v => Tuning.WristLZ = v);
            case "wristlw": return Number(value, v => Tuning.WristLW = v);
            case "wristrx": return Number(value, v => Tuning.WristRX = v);
            case "wristry": return Number(value, v => Tuning.WristRY = v);
            case "wristrz": return Number(value, v => Tuning.WristRZ = v);
            case "wristrw": return Number(value, v => Tuning.WristRW = v);
            default: return false;
        }
    }

    // RA01: a decimal number written the same way whatever the PC's language is (a dot); NaN and infinity are not numbers here.
    // Cost - CPU: extremely low (one number parse per line at start-up); GPU: none (no GPU work).
    static bool Number(string text, Action<float> set)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v)) return false;
        set(v);
        return true;
    }

    // RA01: on / off, also accepted as true / false, yes / no and 1 / 0.
    // Cost - CPU: extremely low (one small string compare per line at start-up); GPU: none (no GPU work).
    static bool Switch(string text, Action<bool> set)
    {
        switch (text.ToLowerInvariant())
        {
            case "on": case "true": case "yes": case "1": set(true); return true;
            case "off": case "false": case "no": case "0": set(false); return true;
            default: return false;
        }
    }

    // RA01: writes the whole file to a temporary name first and then swaps it in, so a crash half way cannot leave a broken settings file.
    // Cost - CPU: low (rare: only half a second after a setting stopped changing; builds about 2 KB of text and does a file write and swap, roughly a millisecond of disk time); GPU: none (no GPU work).
    static void Save()
    {
        try
        {
            // RA01: formats a number with a dot as the decimal mark, up to three decimals.
            // RA01: Cost - CPU: extremely low (one small string format, about 15 per save); GPU: none (no GPU work).
            string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            StringBuilder text = new();
            text.AppendLine("# RA01 settings. Saved by itself when you change something in the game. You can also edit it while the game is closed.");
            text.AppendLine("# Picture size: 1 = true to the game's own field of view. Bigger = a bigger screen in front of you. Range 0.3 to 3.");
            text.AppendLine($"PictureSize={F(Tuning.SizeScale)}");
            text.AppendLine("# Head prediction in milliseconds: how far ahead of now your head is guessed. Range 0 to 80.");
            text.AppendLine($"HeadPredictionMs={F(Tuning.PredictionMs)}");
            text.AppendLine("# Frame lag, 0 to 3: how many of the game's camera builds back a picture was really made from. Use the value where nothing looks doubled.");
            text.AppendLine($"FrameLag={Tuning.FrameLag}");
            text.AppendLine("# IPD, the distance between your eyes, in millimetres. 0 = use your headset's own. Range 40 to 90. A bigger number makes the world look smaller.");
            text.AppendLine($"IpdMm={F(Tuning.IpdMm)}");
            text.AppendLine("# 3D depth: on = each eye gets its own picture, off = one flat picture for both eyes.");
            text.AppendLine($"Stereo={(Tuning.Stereo ? "on" : "off")}");
            text.AppendLine($"WideViewDistanceFix={(Tuning.WideViewDistanceFix ? "on" : "off")}"); // RA01: M21-9c - on = rooms stay at wide VR views (the game's distance stretch capped)
            text.AppendLine($"GameWidthLimit={F(Tuning.GameWidthLimitDeg)}"); // RA01: M5b-2b - degrees; the game's view is never drawn wider than this in Projection mode
            text.AppendLine($"ViewCoverage={F(Tuning.ViewCoverage)}"); // RA01: M5b-2 - percent of the headset's view the game draws in Projection mode
            text.AppendLine($"PictureMode={Tuning.PictureMode}"); // RA01: M5b-1 - 0 Projection (exact), 1 Flat screen (old)
            text.AppendLine("# Game field of view: 1 = the game's own. Bigger = the game draws a wider view, so the picture fills more of the headset and the black border shrinks. Range 0.5 to 2.5.");
            text.AppendLine($"FovScale={F(Tuning.FovScale)}");
            text.AppendLine("# Move the picture to the right (negative = left), in degrees of your view, if it looks off centre. Range -20 to 20.");
            text.AppendLine($"PictureShiftRightDeg={F(Tuning.ShiftRightDeg)}");
            text.AppendLine("# Move the picture up (negative = down), in degrees. Range -20 to 20.");
            text.AppendLine($"PictureShiftUpDeg={F(Tuning.ShiftUpDeg)}");
            text.AppendLine("# How tall the picture is, in degrees of your view, while the game shows a menu or a loading screen. Range 20 to 100.");
            text.AppendLine($"GameMenuSizeDeg={F(Tuning.GameMenuFov)}");
            text.AppendLine("# M13-63 - Move the game's menus, loading and pause screens up (negative = down), in degrees of your view. Gameplay is not moved. Range -40 to 40.");
            text.AppendLine($"GameMenuUpDeg={F(Tuning.GameMenuUpDeg)}");
            text.AppendLine($"GameMenuRightDeg={F(Tuning.GameMenuRightDeg)}"); // RA01: M38
            text.AppendLine("# The F10 window's width in pixels (drag its edges in the game). Range 520 to 1280.");
            text.AppendLine($"MenuWidth={Tuning.MenuWidth}");
            text.AppendLine("# The F10 window's height in pixels. Range 380 to 960.");
            text.AppendLine($"MenuHeight={Tuning.MenuHeight}");
            text.AppendLine("# The F10 window's left edge, in pixels of the 1280 x 960 panel it is drawn on (drag its title bar in the game). It is always kept inside the panel.");
            text.AppendLine($"MenuX={Tuning.MenuX}");
            text.AppendLine("# The F10 window's top edge, in the same pixels.");
            text.AppendLine($"MenuY={Tuning.MenuY}");
            text.AppendLine("# Game resolution (M12-2): the game's own resolution, as a percent of the headset's per-eye size (its own shape), made the only one the game can pick (the launcher and doorman deliver it at game start). Longer side never past 3840 (4K). More costs much more GPU power. Range 25 to 140.");
            text.AppendLine($"GameResolutionPercent={F(Tuning.GameResolutionPercent)}");
            text.AppendLine("# The headset's per-eye size, remembered from the last session (written by the mod, read by the launcher before the game starts; 0 = not seen yet).");
            text.AppendLine($"HeadsetWidth={Tuning.HeadsetWidth}");
            text.AppendLine($"HeadsetHeight={Tuning.HeadsetHeight}");
            text.AppendLine("# Auto field of view (M5a-11). On = Game field of view above is set once, automatically, to widen the game's own view to roughly match your headset's. M5a-13: it no longer changes the picture frame's shape. Takes hold live.");
            text.AppendLine($"AutoFov={(Tuning.AutoFov ? "on" : "off")}");
            text.AppendLine("# M5a-12 - a small extra nudge for the LEFT eye's picture only, on top of the picture left/right and up/down above, in degrees. Range -6 to 6.");
            text.AppendLine($"LeftEyeShiftRightDeg={F(Tuning.LeftEyeShiftRightDeg)}");
            text.AppendLine($"LeftEyeShiftUpDeg={F(Tuning.LeftEyeShiftUpDeg)}");
            text.AppendLine("# The same, for the RIGHT eye's picture only. Range -6 to 6.");
            text.AppendLine($"RightEyeShiftRightDeg={F(Tuning.RightEyeShiftRightDeg)}");
            text.AppendLine($"RightEyeShiftUpDeg={F(Tuning.RightEyeShiftUpDeg)}");
            text.AppendLine("# M5a-14 - the F10 \"Mirror eye trims\" row. On = the four eye-trim lines above are held at 0; the four lines below are what they were the moment this was last turned on. Off = the four lines above are those remembered values.");
            text.AppendLine($"EyeTrimsZeroed={(Tuning.EyeTrimsZeroed ? "on" : "off")}");
            text.AppendLine($"SavedLeftEyeShiftRightDeg={F(Tuning.SavedLeftEyeShiftRightDeg)}");
            text.AppendLine($"SavedLeftEyeShiftUpDeg={F(Tuning.SavedLeftEyeShiftUpDeg)}");
            text.AppendLine($"SavedRightEyeShiftRightDeg={F(Tuning.SavedRightEyeShiftRightDeg)}");
            text.AppendLine($"SavedRightEyeShiftUpDeg={F(Tuning.SavedRightEyeShiftUpDeg)}");
            text.AppendLine("# M9-8 - VR Controls tab: how far your head can turn from V's body (degrees, each side) before her body turns to follow; whether the right stick's up/down tilts the view; VRIK tab: whether V's chest turns part of the way with your head.");
            text.AppendLine($"BodyTurnDeadZoneDeg={F(Tuning.BodyTurnDeadZoneDeg)}");
            text.AppendLine($"StickPitch={(Tuning.StickPitch ? "on" : "off")}");
            text.AppendLine($"KeepViewLevel={(Tuning.KeepViewLevel ? "on" : "off")}"); // RA01: M20-5 - level the game's camera after cutscenes
            text.AppendLine($"FollowStandIn={(Tuning.FollowStandIn ? "on" : "off")}"); // RA01: M20-6 - VRIK drives a stand-in body that has the camera
            text.AppendLine($"FlatAtDevices={(Tuning.FlatAtDevices ? "on" : "off")}"); // RA01: M23-2
            text.AppendLine($"DeviceScreenBackCm={F(Tuning.DeviceScreenBackCm)}"); // RA01: M23-2c
            text.AppendLine($"HudEyePairing={Tuning.HudEyePairing}"); // RA01: M23-0 - 0 per eye, 1 per eye swapped, 2 one eye for both
            text.AppendLine($"VrikInScenes={Tuning.VrikInScenes}"); // RA01: M20-10 - 0 game animates V in cutscenes (tiers 4-5), 1 in all scenes (3-5), 2 never
            text.AppendLine("# M11-2 - VR Controls tab: crouching for real crouches V (and the view stays at your real height); how far your head must drop below standing to crouch V, in cm (15 to 60).");
            text.AppendLine($"PhysicalCrouch={(Tuning.PhysicalCrouch ? "on" : "off")}");
            text.AppendLine($"CrouchDepthCm={F(Tuning.CrouchDepthCm)}");
            text.AppendLine($"PhysicalSwimming={(Tuning.PhysicalSwim ? "on" : "off")}"); // RA01: M22-1
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"SwimForwardStrength={Tuning.SwimForwardStrength:0.00}")); // RA01: M22-1b
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"SwimSideStrength={Tuning.SwimSideStrength:0.00}"));
            text.AppendLine($"SwimTurnDeadZoneDeg={F(Tuning.SwimTurnDeadZoneDeg)}"); // RA01: M22-1e
            text.AppendLine("# M24 - VR Controls tab: braindance & camera look; physical ladder climbing and its strength. Weapons tab: the laser sight (colour 0-5, brightness 0.2-1, thickness mm, depth m).");
            text.AppendLine($"WeaponWeight={(Tuning.WeaponWeight ? "on" : "off")}"); // RA01: M27-4
            text.AppendLine($"PhysicalReload={(Tuning.PhysicalReload ? "on" : "off")}"); // RA01: M27-5
            text.AppendLine($"NoAimZoom={(Tuning.NoAimZoom ? "on" : "off")}"); // RA01: M35
            text.AppendLine($"MonowireLook={(Tuning.MonowireX ? "x" : "straight")}"); // RA01: M38-1d - x / straight
            text.AppendLine("# M41 - Log control tab: which log files are written in Logs\\ (Crash & start-up is always written).");
            text.AppendLine($"LogCoreVR={(Tuning.LogOn[Log.CoreVr] ? "on" : "off")}");
            text.AppendLine($"LogWeapons={(Tuning.LogOn[Log.Weapons] ? "on" : "off")}");
            text.AppendLine($"LogVRIK={(Tuning.LogOn[Log.Vrik] ? "on" : "off")}");
            text.AppendLine($"LogControls={(Tuning.LogOn[Log.Controls] ? "on" : "off")}");
            text.AppendLine($"LogVehicles={(Tuning.LogOn[Log.Vehicles] ? "on" : "off")}");
            text.AppendLine($"LogGameData={(Tuning.LogOn[Log.GameData] ? "on" : "off")}");
            text.AppendLine($"LogMenus={(Tuning.LogOn[Log.Menus] ? "on" : "off")}");
            text.AppendLine($"PouchGlow={(Tuning.PouchGlow ? "on" : "off")}"); // RA01: M27-5c
            text.AppendLine($"PouchColour={Tuning.PouchColour}"); // RA01: M27-5c - 0-7 (Core\PouchGlow.cs's Colours)
            text.AppendLine($"PouchSize={F(Tuning.PouchSize)}"); // RA01: M27-6g - 0.5-2.5 (1 = 100%)
            text.AppendLine($"DroppedRounds={Tuning.DroppedRounds}"); // RA01: M27-7 - 0 = back to your reserve, 1 = lost with the magazine
            text.AppendLine($"ReloadSounds={(Tuning.ReloadSounds ? "on" : "off")}"); // RA01: M27-6k - which sounds: Settings\\RA01_GunSounds.txt
            text.AppendLine($"LeftHanded={(Tuning.LeftHanded ? "on" : "off")}"); // RA01: M28-3
            text.AppendLine($"MenuOnMonitor={(Tuning.MenuOnMonitor ? "on" : "off")}"); // RA01: M29-1
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"WeaponWeightStrength={Tuning.WeaponWeightStrength:0.00}"));
            text.AppendLine($"PhysicalPowerAttacks={(Tuning.PhysicalPowerAttacks ? "on" : "off")}"); // RA01: M27-2
            text.AppendLine($"GestureMode={(Tuning.GestureMode ? "on" : "off")}"); // RA01: M27-1
            text.AppendLine($"ExactPairing={(Tuning.ExactPairing ? "on" : "off")}"); // RA01: M26-1
            text.AppendLine($"FrameLagAuto={(Tuning.FrameLagAuto ? "on" : "off")}"); // RA01: M25-1
            text.AppendLine($"RemoteViewControls={(Tuning.RemoteViewControls ? "on" : "off")}");
            text.AppendLine($"PhysicalLadder={(Tuning.PhysicalLadder ? "on" : "off")}");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"LadderStrength={Tuning.LadderStrength:0.00}"));
            text.AppendLine($"LaserSight={(Tuning.LaserSight ? "on" : "off")}");
            text.AppendLine($"LaserColour={Tuning.LaserColour}");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"LaserBrightness={Tuning.LaserBrightness:0.00}"));
            text.AppendLine($"LaserThicknessMm={F(Tuning.LaserThicknessMm)}");
            text.AppendLine($"LaserLengthM={F(Tuning.LaserLengthM)}");
            text.AppendLine("# M19-1 - VR Controls tab: physical steering; wheel lock and handlebar lock (degrees for full steering); steering speed (full travels a second); steering return (eased self-centring: 0.5 slow to 8 quick).");
            text.AppendLine($"PhysicalSteering={(Tuning.PhysicalSteering ? "on" : "off")}");
            text.AppendLine("# M19-4 - Vehicle tab: steering feel (0 Performance, 1 Comfort, 2 Custom) and centre softness (0 direct to 0.9 very soft).");
            text.AppendLine($"SteerFeel={Tuning.SteerFeel}");
            text.AppendLine("# M20-1 - Vehicle tab: car / truck / tank view height from the driver's eye point, cm (-20 to 10); never rises above it.");
            text.AppendLine($"CarViewHeightCm={F(Tuning.CarViewHeightCm)}");
            text.AppendLine($"CarViewForwardCm={F(Tuning.CarViewForwardCm)}"); // RA01: M46
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"SteerSoftness={Tuning.SteerSoftness:0.00}"));
            text.AppendLine($"WheelLockDeg={F(Tuning.WheelLockDeg)}");
            text.AppendLine($"BarLockDeg={F(Tuning.BarLockDeg)}");
            text.AppendLine($"SteerSpeed={F(Tuning.SteerSpeed)}");
            text.AppendLine($"SteerReturn={F(Tuning.SteerReturn)}");
            text.AppendLine("# M13-2 - Weapons tab: aim with the gun itself (the HUD crosshair is hidden while a gun is drawn).");
            text.AppendLine($"PhysicalAiming={(Tuning.PhysicalAiming ? "on" : "off")}");
            text.AppendLine("# M13-9 - Weapons tab: guard poses block and fast swings strike; how fast a swing must be to strike, in metres a second (1 to 4).");
            text.AppendLine($"PhysicalMelee={(Tuning.PhysicalMelee ? "on" : "off")}");
            text.AppendLine($"SwingSpeed={F(Tuning.SwingSpeed)}");
            text.AppendLine($"PhysicalGrenadeThrow={(Tuning.PhysicalThrow ? "on" : "off")}"); // RA01: M22-4
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"ThrowStrength={Tuning.ThrowStrength:0.00}"));
            text.AppendLine($"PhysicalKnifeThrow={(Tuning.PhysicalKnifeThrow ? "on" : "off")}"); // RA01: M22-5
            text.AppendLine($"KnifeThrowSpeed={F(Tuning.KnifeThrowSpeed)}");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"KnifeThrowStrength={Tuning.KnifeThrowStrength:0.00}")); // RA01: M22-5b
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"KnifeAimHelp={Tuning.KnifeAimHelp:0.00}"));
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"KnifeFlight={Tuning.KnifeFlight:0.00}")); // RA01: M22-5c
            text.AppendLine($"ChestFollowsHead={(Tuning.ChestFollowsHead ? "on" : "off")}");
            text.AppendLine($"UprightSpine={(Tuning.UprightSpine ? "on" : "off")}");
            text.AppendLine($"LeanWithHead={(Tuning.LeanWithHead ? "on" : "off")}");
            text.AppendLine("# M6-2 - VR Controls tab: the on/off row for \"body follows head\" itself. Off = only the physical stick turns your character.");
            text.AppendLine($"BodyFollowEnabled={(Tuning.BodyFollowEnabled ? "on" : "off")}");
            text.AppendLine("# M8-3 - VRIK tab: whether V's arms follow the motion controllers.");
            text.AppendLine($"ArmsFollowControllers={(Tuning.ArmsFollowControllers ? "on" : "off")}");
            text.AppendLine("# M13-16 - VR Video tab: what the desktop monitor shows in 3D - 0 both eyes in turn, 1 left eye only, 2 right eye only.");
            text.AppendLine($"DesktopEye={Tuning.DesktopEye}");
            text.AppendLine("# M9-2/M9-3 - VRIK tab: your arm span from the T-pose calibration (metres, 0 = not measured), V's arm & hand size (percent), where the camera sits on V (cm), and the two wrist turns the T-pose captured (all 0 = not captured).");
            text.AppendLine($"UserArmSpan={F(Tuning.UserArmSpan)}");
            text.AppendLine($"RestShoulders={(Tuning.RestShoulders ? "on" : "off")}");
            text.AppendLine($"ArmSizeAuto={(Tuning.ArmSizeAuto ? "on" : "off")}");
            text.AppendLine($"ArmSizePercent={F(Tuning.ArmSizePercent)}");
            text.AppendLine($"ForearmTwist={(Tuning.ForearmTwist ? "on" : "off")}");
            text.AppendLine("# M14-1b - VRIK tab: Mantis Blades / Gorilla Arms stay folded until that hand strikes (on), or behave as the game's (off).");
            text.AppendLine($"PerHandCyberware={(Tuning.PerHandCyberware ? "on" : "off")}");
            text.AppendLine($"ScopeMagnifier={(Tuning.ScopeMagnifier ? "on" : "off")}");
            text.AppendLine($"AimStickDpad={(Tuning.AimStickDpad ? "on" : "off")}");
            text.AppendLine($"ShoulderScope={(Tuning.ShoulderScope ? "on" : "off")}");
            text.AppendLine($"TwoHandGrip={(Tuning.TwoHandGrip ? "on" : "off")}");
            text.AppendLine($"VirtualStock={(Tuning.VirtualStock ? "on" : "off")}");
            text.AppendLine($"TwoHandMelee={(Tuning.TwoHandMelee ? "on" : "off")}");
            text.AppendLine($"OffHandFingers={(Tuning.OffHandFingers ? "on" : "off")}");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"StockStrength={Tuning.StockStrength:0.00}"));
            text.AppendLine("# M10-1 - HUD tab: the HUD on its own panel, how far ahead (m), how wide (m), moved up or down (m), and follow (0 = your head, smoothly; 1 = V's body).");
            text.AppendLine($"HudPanel={(Tuning.HudPanel ? "on" : "off")}");
            text.AppendLine($"HudDistance={F(Tuning.HudDistance)}");
            text.AppendLine($"HudWidth={F(Tuning.HudWidth)}");
            text.AppendLine($"HudHeight={F(Tuning.HudHeight)}");
            text.AppendLine($"HudSide={F(Tuning.HudSide)}"); // RA01: M21-5 - metres, + right
            text.AppendLine($"HudFollow={Tuning.HudFollow}");
            text.AppendLine($"CameraForwardCm={F(Tuning.CameraForwardCm)}");
            text.AppendLine($"CameraUpCm={F(Tuning.CameraUpCm)}");
            text.AppendLine($"WristLX={F(Tuning.WristLX)}"); text.AppendLine($"WristLY={F(Tuning.WristLY)}"); text.AppendLine($"WristLZ={F(Tuning.WristLZ)}"); text.AppendLine($"WristLW={F(Tuning.WristLW)}");
            text.AppendLine($"WristRX={F(Tuning.WristRX)}"); text.AppendLine($"WristRY={F(Tuning.WristRY)}"); text.AppendLine($"WristRZ={F(Tuning.WristRZ)}"); text.AppendLine($"WristRW={F(Tuning.WristRW)}");

            string path = FilePath, temporary = path + ".tmp";
            File.WriteAllText(temporary, text.ToString());
            File.Move(temporary, path, true);
            saveFailLogged = false; // RA01: it works again, so a later failure is worth saying again
            Log.Write($"RA01 settings: saved {FileName}");
        }
        catch (Exception e)
        {
            try { File.Delete(FilePath + ".tmp"); } catch { } // RA01: no half-written leftover
            if (!saveFailLogged) Log.Write($"RA01 settings: could not save {FileName} ({e.Message}), the changes last only until the game closes");
            saveFailLogged = true;
        }
    }
}
