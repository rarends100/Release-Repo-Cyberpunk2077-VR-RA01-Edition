using System.Runtime.CompilerServices;
using System.Text;

namespace RA01.Core;

// RA01: writes lines of text to the mod's log files, one line per call.
// M41 - LOG CONTROL (Rob approved 2026-10-11): every line is sorted into a category by the code file that wrote it (the compiler hands Write that file's
// name for free - no call site changed), and each category has its own file in Logs\ and its own switch on the F10 window's "Log control" tab (all off by
// default; Tuning.LogOn). A switched-off category's lines are dropped before the file is touched. The Crash & start-up file is always on (it is tiny, and
// it is what a player sends when the game crashes). Each game start begins fresh files; the last run's file is kept as RA01_<category>_previous_<its date>.txt
// (only the newest previous one per category). Builder picture: every crate is already stamped with the room it came from; the mailroom sorts by stamp
// and only delivers to rooms whose door is open.
// Cost - CPU: extremely low for a switched-off line (one small look-up), low for a written one (a file append, 50-200 microseconds); GPU: none.
internal static class Log
{
    public const int Crash = 0, CoreVr = 1, Weapons = 2, Vrik = 3, Controls = 4, Vehicles = 5, GameData = 6, Menus = 7, Count = 8;
    public static readonly string[] Names = { "Crash & start-up", "Core VR", "Weapons", "VRIK", "Controls & movement", "Vehicles", "Game data", "Menus & settings" };
    static readonly string[] Files = { "RA01_Crash", "RA01_CoreVR", "RA01_Weapons", "RA01_VRIK", "RA01_Controls", "RA01_Vehicles", "RA01_GameData", "RA01_Menus" };

    // RA01: which category each code file's lines belong to (by file name, so moving files between folders never matters). Unlisted files go to Core VR.
    static readonly Dictionary<string, int> ByFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CrashWatch"] = Crash, ["Entry"] = Crash, ["NullPlayerGuard"] = Crash, // RA01: M56 - load safety goes beside the black box (always written)
        ["XrSession"] = CoreVr, ["OpenXR"] = CoreVr, ["FrameCopy"] = CoreVr, ["PresentHook"] = CoreVr, ["CameraHook"] = CoreVr, ["HeadPose"] = CoreVr, ["HudLayer"] = CoreVr,
        ["GameHud"] = CoreVr, ["EyeCheck"] = CoreVr, ["ResolutionHook"] = CoreVr, ["DistanceScaleFix"] = CoreVr, ["CullingLab"] = CoreVr, ["PerfMonitor"] = CoreVr,
        ["RenderCameraProbe"] = CoreVr, ["ModFolders"] = CoreVr, ["RuntimeReport"] = CoreVr, // RA01: M66 - the VR setup report
        ["UnarmedDot"] = CoreVr, // RA01: M76 - the unarmed dot
        ["WeaponProbe"] = Weapons, ["WeaponEventProbe"] = Weapons, ["AimProbe"] = Weapons, ["AimSwitchProbe"] = Weapons, ["AimZoomData"] = Weapons, ["CrosshairProbe"] = Weapons,
        ["ShotProbe"] = Weapons, ["ScopeLens"] = Weapons, ["ScopeSwitch"] = Weapons, ["GunCalibration"] = Weapons, ["GameCall_Ammo"] = Weapons, ["GameCall_Sound"] = Weapons,
        ["ThrowLever"] = Weapons, ["ThrowProbe"] = Weapons, ["PouchGlow"] = Weapons, ["FireArmAiming"] = Weapons, ["TwoHandGrip"] = Weapons, ["PhysicalReload"] = Weapons,
        ["GunSounds"] = Weapons, ["WeaponRules"] = Weapons, ["WeaponWeight"] = Weapons, ["PhysicalThrow"] = Weapons, ["MeleeActions"] = Weapons, ["PowerAttack"] = Weapons,
        ["ShoulderScope"] = Weapons, ["LaserSight"] = Weapons,
        ["VRIK"] = Vrik, ["CyberwarePose"] = Vrik, ["StrikeRecorder"] = Vrik, ["PlayerSkeleton"] = Vrik, ["PoseHook"] = Vrik, ["SkeletonHook"] = Vrik, ["SkeletonRadiate"] = Vrik,
        ["BoneFinder"] = Vrik, ["CameraBodyProbe"] = Vrik, ["HandTargets"] = Vrik, ["GameAnchor"] = Vrik, ["HandStrip"] = Vrik,
        ["ControllerMapping"] = Controls, ["Keys"] = Controls, ["MenuPointer"] = Controls, ["Controllers"] = Controls, ["XInputHook"] = Controls, ["PhysicalCrouch"] = Controls,
        ["PhysicalLadder"] = Controls, ["PhysicalSwim"] = Controls, ["GestureMode"] = Controls,
        ["VehicleProbe"] = Vehicles, ["VehicleState"] = Vehicles, ["PhysicalSteering"] = Vehicles,
        ["GameCall"] = GameData, ["BlackboardProbe"] = GameData, ["NodeProbe"] = GameData, ["ExeScan"] = GameData, ["PointerFinder"] = GameData, ["PointerTableDump"] = GameData,
        ["MenuSense"] = GameData, ["HookRegistry"] = GameData, ["AddressList"] = GameData, ["FieldNames"] = GameData,
        ["SettingsMenu"] = Menus, ["SettingsFile"] = Menus, ["MenuInput"] = Menus, ["MenuDraw"] = Menus, ["FlatMenu"] = Menus, ["AutoTune"] = Menus, ["Banner"] = Menus, ["Tuning"] = Menus,
    };
    static readonly Dictionary<string, int> seen = new();     // RA01: caller path -> category, worked out once per file
    static readonly object gate = new();
    static bool started, switchesRead;

    // RA01: adds one time-stamped line to its category's file, if that category is switched on (Crash & start-up always is). It never throws.
    // M13-11 - one writer at a time (the lock), and the file opened so others may still read and write it (two threads writing at once used to lose a line).
    // Cost - CPU: extremely low when the category is off (a dictionary look-up under a lock); low when on (an open, append and close, 50-200 microseconds);
    // GPU: none (file writing only).
    public static void Write(string message, [CallerFilePath] string caller = "")
    {
        try
        {
            int category;
            lock (gate)
            {
                if (!switchesRead) { switchesRead = true; ReadSwitches(); }
                if (!seen.TryGetValue(caller, out category)) seen[caller] = category = ByFile.TryGetValue(Path.GetFileNameWithoutExtension(caller), out int c) ? c : CoreVr;
                if (category != Crash && !Tuning.LogOn[category]) return;
                if (!started) { started = true; StartFresh(); }
            }
            byte[] line = Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [thread {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            lock (gate)
            {
                Directory.CreateDirectory(ModFolders.Logs);
                using FileStream file = new(Path.Combine(ModFolders.Logs, Files[category] + ".txt"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                file.Write(line, 0, line.Length);
            }
        }
        catch
        {
            // RA01: a logging failure must never be able to crash the game.
        }
    }

    // RA01: M41 - the Log control switches straight from RA01_Settings.ini on the very first line, because the full settings load (SettingsFile.Load, in
    // XrSession) comes later - otherwise every start-up line (the hooks going in) would be dropped even with its log switched on. Only the Log... lines are
    // read; SettingsFile.Load later sets the same values again. Cost - CPU: extremely low (one small text file read, once); GPU: none.
    static void ReadSwitches()
    {
        try
        {
            string path = Path.Combine(ModFolders.Settings, "RA01_Settings.ini");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 3 || !line.StartsWith("Log", StringComparison.OrdinalIgnoreCase)) continue;
                bool on = line[(eq + 1)..].Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                switch (line[..eq].Trim().ToLowerInvariant())
                {
                    case "logcorevr": Tuning.LogOn[CoreVr] = on; break;
                    case "logweapons": Tuning.LogOn[Weapons] = on; break;
                    case "logvrik": Tuning.LogOn[Vrik] = on; break;
                    case "logcontrols": Tuning.LogOn[Controls] = on; break;
                    case "logvehicles": Tuning.LogOn[Vehicles] = on; break;
                    case "loggamedata": Tuning.LogOn[GameData] = on; break;
                    case "logmenus": Tuning.LogOn[Menus] = on; break;
                }
            }
        }
        catch { }
    }

    // RA01: M41 - once per game start, before the first line: each category's last-run file becomes RA01_<category>_previous_<date>.txt (its own last-write time),
    // replacing any older previous one, so every category starts empty. Cost - CPU: extremely low (a few file checks and renames, once); GPU: none.
    static void StartFresh()
    {
        try
        {
            Directory.CreateDirectory(ModFolders.Logs);
            foreach (string name in Files)
            {
                string current = Path.Combine(ModFolders.Logs, name + ".txt");
                if (!File.Exists(current) || new FileInfo(current).Length == 0) continue;
                foreach (string old in Directory.GetFiles(ModFolders.Logs, name + "_previous_*.txt")) File.Delete(old);
                File.Move(current, Path.Combine(ModFolders.Logs, $"{name}_previous_{File.GetLastWriteTime(current):yyyy-MM-dd_HH-mm-ss}.txt"));
            }
        }
        catch { }
    }
}
