using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")] // RA01: M12-1 - Windows-only (the registry read below); silences the cross-platform warnings

// RA01: the launcher is a one-time program: it runs once from top to bottom and exits; nearly all of its time is spent sleeping while it waits for the game.
// RA01: M40 - it first shows its window (LauncherWindow.cs: SETUP and CONTROLS & FEATURES tabs, in the RA01 look); everything below runs when the player
// RA01: presses LAUNCH (Launch), with its messages on the window's status line and in Logs\RA01_launcher_log.txt. It is built as one self-contained exe
// RA01: (NativeAOT, like RA01.dll), so players need nothing installed. It finds the game on Steam, GOG or Epic, or where the player points it, and can start
// RA01: Cyberpunk2077.exe directly or through Steam.
// RA01: M12-1 - it also places the doorman (xinput9_1_0.dll, see Launcher\Doorman\Doorman.cs) in the game's bin\x64 folder before the game starts, so Windows loads it at the
// RA01: game's start, then stays open (asleep) until the game exits and removes it again. A leftover from a crash is removed at the next start. An xinput9_1_0.dll that
// RA01: isn't ours (another mod's) is never touched - the doorman just sits that session out.
// RA01: M13-20 - it also writes RA01_LodFix.ini into the game's engine\config\platform\pc folder before the game starts (see PlaceLodFix), removed the same way.
// RA01: M12-2 - beside the doorman it writes RA01_Resolution.txt: the game resolution the doorman delivers, worked out from RA01_Settings.ini (GameResolutionPercent of
// RA01: the remembered HeadsetWidth x HeadsetHeight); removed with the doorman.
// RA01: M32-1 - THE VR GAME SETTINGS: before the game starts, the player's own UserSettings.json (the game's whole settings file) is set aside and their
// RA01: VR copy is put in its place; when the game closes they swap back - renames only, in the game's own settings folder (see SwapSettings). Their flat game
// RA01: and their VR tweaks each stay exactly as they left them.
// RA01: Cost - CPU: extremely low (about 1 s polling for the game (up to 2 minutes), 0.5 s polling for the renderer (up to 2 minutes), then a fixed 5 s settle wait; a few Windows calls after that;
// RA01: then asleep until the game exits); GPU: none (no GPU work).

// RA01: Steam app id for Cyberpunk 2077, the game's process name, and the Core DLL details.
const string SteamUri = "steam://rungameid/1091500";
const string GameProcess = "Cyberpunk2077";
const string CoreDll = "RA01.dll";
const string CoreEntry = "RA01_Start";
const int SettleMs = 5000;
const string DoormanDll = "xinput9_1_0.dll";    // RA01: M12-1 - the doorman's file name (the XInput file the game imports)
const string DoormanMarker = "RA01_Doorman";    // RA01: M12-1 - the export that tells our doorman apart from anyone else's xinput9_1_0.dll
const string SizeFile = "RA01_Resolution.txt";  // RA01: M12-2 - the game resolution the doorman delivers, beside it
const string SettingsFileName = "RA01_Settings.ini";
const string LodFile = "RA01_LodFix.ini";      // RA01: M13-20 - the LOD fix, in the game's engine\config\platform\pc folder while the game runs
const string LodText = "; RA01_LodFix - placed by the RA01 launcher while the game runs, removed when it closes\r\n[Streaming/Culling/AutoHideDistanceNear]\r\nEnabled = false\r\n"; // RA01: M20-14b - the VisibilityQuery test line is gone: it didn't help, and the vanishing rooms turned out to be the game's ~100-degree limit (fixed in the mod by the 96-degree view width cap)
const string GameSettings = "UserSettings.json";                 // RA01: M32-1 - the game's settings file (graphics, controls, keybinds, language - all of it)
const string FlatAside = GameSettings + ".RA01_notinuse";          // RA01: M32-1 - the player's own settings, set aside while VR runs (the game ignores it)
const string VrAside = GameSettings + ".RA01_VR_notinuse";         // RA01: M32-1 - the player's VR settings, kept while they play flat
const string NoFlatMark = GameSettings + ".RA01_none";             // RA01: M32-1 - "the player had no settings file of their own" (a fresh install)
const string BaseSettingsFolder = "base_game_json_settings";      // RA01: M32-1 - Rob's VR settings, shipped beside the launcher: the first VR copy is made from it
const int MaxSide = 3840;                       // RA01: M12-2 - same rule as RA01.dll's ResolutionHook.Size: the longer side never past 4K's width
const string LauncherSettingsFile = "RA01_Launcher.ini";  // RA01: M40 - the launcher's own settings (game folder, start mode, VR runtime), in Settings\ - RA01.dll rewrites RA01_Settings.ini from what it knows, so these live apart
const int DirectFallbackSeconds = 20;           // RA01: M40 - a direct start that shows no game process by then asks Steam instead

// RA01: the Core DLL is expected next to the launcher, unless a path is passed as the first argument.
// RA01: M49-3 - ONE RA01 LAUNCHER AT A TIME (Rob 2026-10-11): two launchers both injected RA01 into the same game (09:03). A named lock that only the first
// RA01: launcher can hold, checked before anything else (even the log, so a second copy can't overwrite it); a second copy says so and closes. The lock goes
// RA01: with the first launcher when it exits. Cost - CPU: extremely low (one Windows call at start); GPU: none.
using Mutex oneLauncher = new(true, @"Local\RA01_Launcher_OneAtATime", out bool firstLauncher);
if (!firstLauncher)
{
    Out.Box("RA01 is already running - only one RA01 window can be open at a time.\n\nUse the one that is already open (while the game runs it may be hidden; it closes by itself when the game has closed).");
    return 1;
}

string dllPath = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, CoreDll));
string modFolder = Path.GetDirectoryName(dllPath)!;
Out.Start(Path.Combine(modFolder, "Logs", "RA01_launcher_log.txt"));
if (!File.Exists(dllPath))
{
    Out.Say($"RA01: cannot find {dllPath}");
    Out.Box($"RA01.dll wasn't found next to the launcher:\n{dllPath}\n\nKeep the launcher in the RA01 folder.");
    return 1;
}

// RA01: M40 - the launcher window first: the player's launch settings (RA01_Launcher.ini), the resolution (RA01_Settings.ini) and the game folder, then LAUNCH.
string launcherIni = Path.Combine(modFolder, "Settings", LauncherSettingsFile);
string? savedFolder = LoadLauncherSettings(launcherIni);
LauncherWindow.ResolutionPercent = ReadResolution(SettingsPath(modFolder));
var (foundFolder, foundSource) = FindGameFolder(savedFolder);
LauncherWindow.GameFolder = foundFolder ?? "";
LauncherWindow.FolderSource = foundSource;
LauncherWindow.GameBuild = GameBuildOf(foundFolder);   // RA01: M40b
LauncherWindow.MetaLinkActive = MetaLinkActive();       // RA01: M70
LauncherWindow.CheckPicked = picked => { if (GameBinFrom(picked) is not string bin) return (null, ""); LauncherWindow.GameBuild = GameBuildOf(bin); return (bin, "your choice"); };
LauncherWindow.OnSettingsChanged = () => { SaveLauncherSettings(launcherIni); WriteResolution(modFolder, LauncherWindow.ResolutionPercent); };
int exitCode = 1;
bool restart = false;   // RA01: M57 - the game ran and has closed: start a fresh launcher
LauncherWindow.OnLaunch = () =>
{
    SaveLauncherSettings(launcherIni);
    WriteResolution(modFolder, LauncherWindow.ResolutionPercent);
    new Thread(() =>
    {
        bool direct = LauncherWindow.StartDirect;
        if (!direct && MetaLinkActive()) { Out.Say("RA01: Meta Quest Link is the active VR runtime - starting the game directly, because starting it through Steam crashes with Link (M70)."); direct = true; }
        try { exitCode = Launch(dllPath, LauncherWindow.GameFolder.Length > 0 ? LauncherWindow.GameFolder : null, direct, out bool gameClosed); Volatile.Write(ref restart, gameClosed); }
        catch (Exception e) { Out.Say($"RA01: the launch stopped ({e.Message})."); }
        LauncherWindow.LaunchFinished(Volatile.Read(ref restart));
    }) { IsBackground = false }.Start();
};
LauncherWindow.Run();

// RA01: M57 - RESTART AFTER THE GAME (Rob 2026-10-12): once the game has closed and everything is put back, the launcher closes and opens again, fresh and
// RA01: ready for the next game - like a till that prints the receipt and resets for the next customer. Only when the game really ran and closed; a launch
// RA01: that failed keeps its window and message as before. The one-at-a-time lock (M49-3) is let go and CLOSED first - the new launcher checks whether the lock
// RA01: exists at all, so with it still open the new one would say "already running" and quit. Cost - CPU: extremely low (one process start, once); GPU: none.
if (Volatile.Read(ref restart))
{
    try
    {
        oneLauncher.ReleaseMutex();
        oneLauncher.Dispose();
        var again = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (string a in args) again.ArgumentList.Add(a);
        Out.Say("RA01: the game has closed - the launcher restarts, ready for the next game (M57)."); // RA01: said BEFORE the new launcher opens the same log file
        Process.Start(again);
    }
    catch (Exception e) { Out.Say($"RA01: the launcher couldn't restart itself ({e.Message}) - open it again by hand."); }
}
return exitCode;

// RA01: M40 - THE LAUNCH (was the launcher's whole top-level flow before the window; unchanged apart from the start and the game folder coming in):
// place the doorman, the LOD fix and the VR settings, start the game (M40: directly - Cyberpunk2077.exe itself - or through Steam; a direct start
// that shows no game in 20 s falls back to Steam once), inject RA01.dll, then wait for the game to close and put everything back.
// Cost - CPU: extremely low (polling once a second for the game, then asleep until it closes); GPU: none.
static int Launch(string dllPath, string? gameFolder, bool direct, out bool gameClosed)
{
    gameClosed = false;   // RA01: M57 - set at the end: true only when the game was there and has now exited
    string? doormanAt = gameFolder == null ? null : Path.Combine(gameFolder, DoormanDll);
    string? lodAt = gameFolder == null ? null : Path.GetFullPath(Path.Combine(gameFolder, "..", "..", "engine", "config", "platform", "pc", LodFile)); // RA01: M13-20
    bool doormanPlaced = false, lodPlaced = false, settingsSwapped = false;

    // RA01: use the game if it is already running, otherwise place the doorman, start the game and wait for it to appear.
    Process? game = FindGame();
    if (game == null)
    {
        doormanPlaced = PlaceDoorman(Path.Combine(AppContext.BaseDirectory, DoormanDll), doormanAt);
        if (doormanPlaced) WriteSize(SettingsPath(Path.GetDirectoryName(dllPath)!), Path.Combine(gameFolder!, SizeFile));
        lodPlaced = PlaceLodFix(lodAt); // RA01: M13-20
        settingsSwapped = SwapSettings(true); // RA01: M32-1 - the VR settings in, the player's own aside
        bool startedDirect = direct && gameFolder != null && StartDirect(gameFolder);
        if (!startedDirect) StartThroughSteam();
        for (int i = 0; i < 120 && game == null; i++)
        {
            Thread.Sleep(1000);
            game = FindGame();
            if (game == null && startedDirect && i == DirectFallbackSeconds)
            {
                if (MetaLinkActive()) { Out.Say("RA01: the game didn't appear after a direct start - not asking Steam, because that crashes with Meta Quest Link (M70); check the game folder."); startedDirect = false; }
                else { Out.Say("RA01: the game didn't appear after a direct start - asking Steam instead."); StartThroughSteam(); startedDirect = false; }
            }
        }
    }
    else Out.Say("RA01: the game is already running, so the doorman can't help this session (it has to be there when the game starts).");

    try
    {
        return Inject(game, dllPath);
    }
    finally
    {
        // RA01: M12-1 - the game holds the doorman open while it runs: wait (asleep) for it to exit, then remove it.
        // RA01: M13-20 - and the same for the LOD fix file.
        if ((doormanPlaced && doormanAt != null) || lodPlaced || settingsSwapped)
        {
            if (game != null && !game.HasExited)
            {
                Out.Say("RA01: game running - keep this window open (or minimised): when the game closes it removes the doorman and the LOD fix and puts your own game settings back."); // RA01: M32-1
                game.WaitForExit();
            }
            if (doormanPlaced && doormanAt != null)
            {
                RemoveDoorman(doormanAt);
                try { File.Delete(Path.Combine(gameFolder!, SizeFile)); } catch { } // RA01: M12-2 - the size file goes with the doorman
            }
            if (lodPlaced) RemoveLodFix(lodAt!);
            if (settingsSwapped) SwapSettings(false); // RA01: M32-1 - the player's own settings back; their VR tweaks kept aside for next time
        }
        gameClosed = game != null && game.HasExited; // RA01: M57 - the launcher restarts itself after this
        Out.Say(gameClosed ? "RA01: all done - the game has closed and everything is back as it was. The launcher restarts now." : "RA01: all done - the game has closed and everything is back as it was. You can close this window.");
    }
}

// RA01: M40 - starts Cyberpunk2077.exe itself from its bin\x64 folder (works for Steam - with Steam running - GOG and Epic copies). False if Windows refused.
// Cost - CPU: extremely low (one process start); GPU: none.
static bool StartDirect(string gameFolder)
{
    try
    {
        Out.Say("RA01: starting Cyberpunk2077.exe directly...");
        Process.Start(new ProcessStartInfo(Path.Combine(gameFolder, GameProcess + ".exe")) { UseShellExecute = false, WorkingDirectory = gameFolder });
        return true;
    }
    catch (Exception e) { Out.Say($"RA01: couldn't start the game directly ({e.Message}) - asking Steam instead."); return false; }
}

// RA01: M70 - is Meta's OpenXR runtime (Quest Link / Air Link) the active one? Windows keeps the active runtime's file in the registry (machine-wide, and a
// per-user override); Meta's is oculus_openxr_64.json. Rob 2026-10-13 02:31: with Link active, starting the game THROUGH STEAM crashed every time - the
// black box recorded a stack overflow in KERNELBASE.dll during RA01's start-up (breadcrumb "start-up: HudLayer"): Steam's in-game overlay, Meta's runtime
// and RA01's HUD layer all hook the same D3D12 functions, and together they call round in a circle. A direct start doesn't load Steam's overlay, so it works.
// Cost - CPU: extremely low (two registry reads); GPU: none.
static bool MetaLinkActive()
{
    try
    {
        string user = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime", null) as string ?? "";
        string active = user.Length > 0 ? user : Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime", null) as string ?? "";
        return active.Contains("oculus", StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}

// RA01: asks Steam to start the game (the launcher's way before M40). Cost - CPU: extremely low; GPU: none.
static void StartThroughSteam()
{
    Out.Say("RA01: starting Cyberpunk 2077 through Steam...");
    try { Process.Start(new ProcessStartInfo(SteamUri) { UseShellExecute = true }); }
    catch (Exception e) { Out.Say($"RA01: Steam couldn't be asked to start the game ({e.Message})."); }
}

// RA01: waits for the game's renderer, then loads RA01.dll into the game and starts it. Returns the launcher's exit code (0 = injected).
// Cost - CPU: extremely low (0.5 s polling for the renderer, a 5 s settle wait, then a few Windows calls, once); GPU: none (no GPU work).
static int Inject(Process? game, string dllPath)
{
    if (game == null)
    {
        Out.Say("RA01: the game process never appeared.");
        return 1;
    }

    // RA01: wait until the renderer (d3d12.dll) is loaded, then a short settle time, so we do not inject mid-startup.
    Out.Say("RA01: waiting for the game to finish starting up...");
    for (int i = 0; i < 240 && !game.HasExited && FindModule(game, "d3d12.dll") == 0; i++)
        Thread.Sleep(500);
    if (game.HasExited || FindModule(game, "d3d12.dll") == 0)
    {
        Out.Say("RA01: the game exited or never reached the renderer.");
        return 1;
    }
    Thread.Sleep(SettleMs);

    nint proc = Win32.OpenProcess(0x043A, false, game.Id); // RA01: create thread, query, and read/write/operate on memory only.
    if (proc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

    // RA01: step 1 - copy the DLL path into the game, then run LoadLibraryW there so the game loads our DLL.
    byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
    nint remotePath = Win32.VirtualAllocEx(proc, 0, (nuint)pathBytes.Length, 0x3000, 0x04);
    if (remotePath == 0 || !Win32.WriteProcessMemory(proc, remotePath, pathBytes, (nuint)pathBytes.Length, out _))
        throw new Win32Exception(Marshal.GetLastWin32Error());
    nint loadLibrary = Win32.GetProcAddress(Win32.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
    RunRemote(proc, loadLibrary, remotePath);
    Win32.VirtualFreeEx(proc, remotePath, 0, 0x8000);

    // RA01: step 2 - find where the DLL landed inside the game, and where RA01_Start sits within it.
    // RA01: the launcher maps its own copy without running any of its code, only to measure the offset.
    nint remoteBase = FindModule(game, CoreDll);
    if (remoteBase == 0)
    {
        Out.Say("RA01: the game did not load the Core DLL.");
        return 1;
    }
    nint localBase = Win32.LoadLibraryExW(dllPath, 0, 0x1); // RA01: DONT_RESOLVE_DLL_REFERENCES
    nint localStart = Win32.GetProcAddress(localBase, CoreEntry);
    if (localStart == 0)
    {
        Out.Say($"RA01: {CoreEntry} is not exported by {CoreDll}.");
        return 1;
    }
    RunRemote(proc, remoteBase + (localStart - localBase), 0);
    Win32.FreeLibrary(localBase);
    Win32.CloseHandle(proc);

    Out.Say($"RA01: injected - have fun. Logs (if switched on in F10 > Log control) are in {Path.Combine(Path.GetDirectoryName(dllPath)!, "Logs")}"); // RA01: M20-4, M41 - one file per log category now
    return 0;
}

// RA01: M40 - the game's bin\x64 folder, first match wins: the player's saved choice; the launcher sitting inside the game folder (Vortex or a manual
// RA01: install there); Steam (its install folder from the registry plus every library in libraryfolders.vdf); GOG (its registry entry); Epic (its install
// RA01: manifests). Returns the folder (null if none) and where it came from, for the window. Builder picture: checking the usual parking spots in order.
// RA01: Cost - CPU: extremely low (a registry read or two and a few small text files, once); GPU: none (no GPU work).
static (string? folder, string source) FindGameFolder(string? saved)
{
    if (GameBinFrom(saved) is string chosen) return (chosen, "your choice");
    for (string? up = AppContext.BaseDirectory; up != null; up = Path.GetDirectoryName(up.TrimEnd('\\')))
        if (GameBinFrom(up) is string inside) return (inside, "next to the launcher");
    try
    {
        if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
        {
            List<string> libraries = new() { steam };
            string list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(list))
                foreach (Match m in Regex.Matches(File.ReadAllText(list), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (string library in libraries)
                if (GameBinFrom(Path.Combine(library, "steamapps", "common", "Cyberpunk 2077")) is string bin) return (bin, "Steam");
        }
    }
    catch (Exception e) { Out.Say($"RA01: couldn't read Steam's library list ({e.Message})."); }
    try
    {
        foreach (string key in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1423049311", @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\1423049311" })
            if (Microsoft.Win32.Registry.GetValue(key, "path", null) is string gog && GameBinFrom(gog) is string bin) return (bin, "GOG");
    }
    catch { }
    try
    {
        string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifests))
            foreach (string item in Directory.GetFiles(manifests, "*.item"))
            {
                string text = File.ReadAllText(item);
                if (!text.Contains("Cyberpunk 2077")) continue;
                Match m = Regex.Match(text, "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success && GameBinFrom(m.Groups[1].Value.Replace(@"\\", @"\")) is string bin) return (bin, "Epic");
            }
    }
    catch { }
    return (null, "");
}

// RA01: M40 - the game's bin\x64 folder from a path the player or a store gave: the game folder itself, its bin\x64, or Cyberpunk2077.exe. Null if the game isn't there.
// Cost - CPU: extremely low (up to three file checks); GPU: none.
static string? GameBinFrom(string? path)
{
    if (string.IsNullOrWhiteSpace(path)) return null;
    try
    {
        string full = Path.GetFullPath(path.Trim().Trim('"'));
        if (File.Exists(full) && Path.GetFileName(full).Equals(GameProcess + ".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(full);
        if (File.Exists(Path.Combine(full, GameProcess + ".exe"))) return full;
        string bin = Path.Combine(full, "bin", "x64");
        if (File.Exists(Path.Combine(bin, GameProcess + ".exe"))) return bin;
    }
    catch { }
    return null;
}

// RA01: M40b - the game's build ("2.31"), read from Cyberpunk2077.exe's own version stamp (its ProductVersion; the FileVersion, e.g. 3.0.5294808, if that is
// empty). No game start needed. "" if the folder or the stamp is missing. Logged once. Cost - CPU: extremely low (one Windows version read, once); GPU: none.
static string GameBuildOf(string? bin)
{
    if (bin == null) return "";
    try
    {
        var info = FileVersionInfo.GetVersionInfo(Path.Combine(bin, GameProcess + ".exe"));
        string build = (string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion)?.Trim() ?? "";
        Out.Say($"RA01: game build {build} (exe file version {info.FileVersion}) in {bin}");
        return build;
    }
    catch (Exception e) { Out.Say($"RA01: couldn't read the game's build ({e.Message})."); return ""; }
}

// RA01: M40 - reads RA01_Launcher.ini into the window (start mode, VR runtime) and returns the saved game folder (null if none).
// Cost - CPU: extremely low (one small file, once); GPU: none.
static string? LoadLauncherSettings(string path)
{
    string? folder = null;
    try
    {
        if (!File.Exists(path)) return null;
        foreach (string line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.TrimStart().StartsWith('#')) continue;
            string key = line[..eq].Trim().ToLowerInvariant(), value = line[(eq + 1)..].Trim();
            if (key == "gamefolder" && value.Length > 0) folder = value;
            else if (key == "startmode") LauncherWindow.StartDirect = !value.Equals("steam", StringComparison.OrdinalIgnoreCase);
            else if (key == "vrruntime" && value.Length > 0) LauncherWindow.Runtime = value;
        }
    }
    catch (Exception e) { Out.Say($"RA01: couldn't read {LauncherSettingsFile} ({e.Message}) - using the defaults."); }
    return folder;
}

// RA01: M40 - writes RA01_Launcher.ini from the window. Cost - CPU: extremely low (one tiny file, on each change); GPU: none.
static void SaveLauncherSettings(string path)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# RA01 launcher settings - written by RA01.Launcher.exe (its SETUP tab)\r\n" +
            $"GameFolder={LauncherWindow.GameFolder}\r\nStartMode={(LauncherWindow.StartDirect ? "direct" : "steam")}\r\nVrRuntime={LauncherWindow.Runtime}\r\n");
    }
    catch (Exception e) { Out.Say($"RA01: couldn't save {LauncherSettingsFile} ({e.Message})."); }
}

// RA01: M40 - GameResolutionPercent from RA01_Settings.ini (50 if missing), as the window's 25-140% in 5% steps. Cost - CPU: extremely low; GPU: none.
static int ReadResolution(string settings)
{
    try
    {
        if (File.Exists(settings))
            foreach (string line in File.ReadAllLines(settings))
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals("GameResolutionPercent", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(line[(eq + 1)..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p))
                    return Math.Clamp((int)MathF.Round(p / 5f) * 5, 25, 140);
            }
    }
    catch { }
    return 50;
}

// RA01: M40 - writes GameResolutionPercent into RA01_Settings.ini: only that one line is changed (or added); every other line stays exactly as RA01.dll wrote it.
// A new file goes in Settings\ (where RA01.dll keeps it). Cost - CPU: extremely low (one small file read and written, on each change); GPU: none.
static void WriteResolution(string modFolder, int percent)
{
    try
    {
        string path = SettingsPath(modFolder);
        if (!File.Exists(path)) { path = Path.Combine(modFolder, "Settings", SettingsFileName); Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new();
        string value = $"GameResolutionPercent={percent}";
        int at = lines.FindIndex(l => l.Contains('=') && l[..l.IndexOf('=')].Trim().Equals("GameResolutionPercent", StringComparison.OrdinalIgnoreCase));
        if (at >= 0) { if (lines[at] == value) return; lines[at] = value; } else lines.Add(value);
        File.WriteAllLines(path, lines);
    }
    catch (Exception e) { Out.Say($"RA01: couldn't save the game resolution ({e.Message})."); }
}

// RA01: M12-1 - copies the doorman into the game folder. A leftover of ours (from a crash) is replaced; anyone else's xinput9_1_0.dll is left alone. True = placed.
// Cost - CPU: extremely low (a file check, maybe a marker look-up, and a copy of a small file, once); GPU: none (no GPU work).
static bool PlaceDoorman(string source, string? target)
{
    if (target == null) { Out.Say("RA01: the game folder isn't known - no doorman this session (set it on the SETUP tab)."); return false; }
    if (!File.Exists(source)) { Out.Say($"RA01: {DoormanDll} isn't next to the launcher - no doorman this session."); return false; }
    try
    {
        if (File.Exists(target) && !IsOurDoorman(target))
        {
            Out.Say($"RA01: another {DoormanDll} is already in the game folder (another mod?) - left alone, no doorman this session.");
            return false;
        }
        File.Copy(source, target, true);
        Out.Say($"RA01: doorman placed in {Path.GetDirectoryName(target)}");
        return true;
    }
    catch (Exception e) { Out.Say($"RA01: couldn't place the doorman ({e.Message})."); return false; }
}

// RA01: M13-20 - writes the LOD fix (LodText) into the game's engine\config\platform\pc folder, where the game reads every .ini at start-up. It switches off
// the game's "near auto-hide" (Streaming/Culling/AutoHideDistanceNear), which hides buildings and streets it guesses are outside a normal flat-screen view -
// wrong for the much wider view RA01 asks for, so things popped in and out. Our own file name, so no other mod's file is touched; a leftover from a crash is
// simply written over. True = placed.
// Cost - CPU: extremely low (one tiny file written, once); GPU: none here (in game: the game may draw a few more far-off objects it used to hide - low).
static bool PlaceLodFix(string? target)
{
    if (target == null) { Out.Say("RA01: the game folder isn't known - no LOD fix this session (set it on the SETUP tab)."); return false; }
    try
    {
        if (!Directory.Exists(Path.GetDirectoryName(target))) { Out.Say($"RA01: {Path.GetDirectoryName(target)} isn't there - no LOD fix this session."); return false; }
        File.WriteAllText(target, LodText);
        Out.Say($"RA01: LOD fix placed in {Path.GetDirectoryName(target)}");
        return true;
    }
    catch (Exception e) { Out.Say($"RA01: couldn't place the LOD fix ({e.Message})."); return false; }
}

// RA01: M32-1 - THE SETTINGS SWAP, in the game's settings folder (%LOCALAPPDATA%\CD Projekt Red\Cyberpunk 2077). Three names (FlatAside, VrAside, NoFlatMark):
//   playing flat: UserSettings.json = the player's own, VrAside = their VR settings waiting;
//   playing VR:   UserSettings.json = their VR settings, FlatAside = their own set aside (or NoFlatMark if they had none).
// 'toVr' true (before the game starts):
//   1. CRASH RECOVERY: FlatAside or NoFlatMark still there = last session never swapped back (the launcher window was closed, a crash) - first the
//      VR file in use goes back to VrAside and the player's own comes back, as at a normal exit;
//   2. no VrAside yet (the first VR start) = made from Rob's base_game_json_settings\UserSettings.json; neither there = nothing swapped (logged);
//   3. UserSettings.json -> FlatAside (or NoFlatMark written), then VrAside -> UserSettings.json. If that last step fails, the player's own goes back.
// 'toVr' false (the game has closed): UserSettings.json (with any tweaks made in VR) -> VrAside, then FlatAside -> UserSettings.json / NoFlatMark removed.
// Renames only, so nothing is ever deleted except our own marker; the player's own file is never written to. True = the VR settings are in.
// Builder picture: two sets of tools on one bench - the set not in use goes in its labelled drawer, never in the bin.
// Cost - CPU: extremely low (a few file renames, and one copy the first time); GPU: none.
static bool SwapSettings(bool toVr)
{
    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CD Projekt Red", "Cyberpunk 2077");
    string live = Path.Combine(folder, GameSettings), flat = Path.Combine(folder, FlatAside), vr = Path.Combine(folder, VrAside), none = Path.Combine(folder, NoFlatMark);
    try
    {
        if (!toVr || File.Exists(flat) || File.Exists(none))        // RA01: the swap back (and step 1, the crash recovery)
        {
            if (toVr) Out.Say("RA01: the last VR session didn't put your game settings back (window closed or a crash) - doing it now.");
            if (File.Exists(live)) File.Move(live, vr, true);
            if (File.Exists(flat)) File.Move(flat, live);
            if (File.Exists(none)) File.Delete(none);
            if (!toVr) { Out.Say("RA01: your own game settings are back (your VR settings are kept for next time)."); return false; }
        }
        if (!File.Exists(vr))                                        // RA01: step 2 - the first VR start
        {
            string shipped = Path.Combine(AppContext.BaseDirectory, BaseSettingsFolder, GameSettings);
            if (!File.Exists(shipped)) { Out.Say($"RA01: no {BaseSettingsFolder}\\{GameSettings} beside the launcher - the game keeps your own settings this session."); return false; }
            Directory.CreateDirectory(folder);
            File.Copy(shipped, vr);
            Out.Say("RA01: made your VR game settings from the mod's recommended ones (your changes in VR are kept from now on).");
            if (File.Exists(live)) CarryLanguage(live, vr);          // RA01: M32-1b - the player's own languages, not Rob's
        }
        if (File.Exists(live)) File.Move(live, flat); else File.WriteAllText(none, "RA01: the player had no UserSettings.json of their own before VR - remove it with the VR one.");
        try { File.Move(vr, live); }
        catch { if (File.Exists(flat)) File.Move(flat, live); if (File.Exists(none)) File.Delete(none); throw; }   // RA01: step 3 failed - their own back at once
        Out.Say("RA01: your VR game settings are in; your own are set aside until the game closes.");
        return true;
    }
    catch (Exception e)
    {
        Out.Say($"RA01: couldn't swap the game settings ({e.Message}) - {(toVr ? "the game keeps whatever is in place" : "the next VR launcher start puts your own back")}.");
        return false;
    }
}

// RA01: M32-1b - Rob 12:03: the player's OWN languages go into their first VR copy (Rob's file holds his: a German player would get English). The game keeps
// them in one group, "/language" (VoiceOver, Subtitles, OnScreen, Platform - each a value like "en-us" plus its place in the game's list), so the whole
// group is copied over from 'from' into 'into'; everything else stays Rob's. If either file can't be read as the game's settings, 'into' is left as it was.
// Cost - CPU: extremely low (two ~100 KB files read and one written, once per player); GPU: none.
static void CarryLanguage(string from, string into)
{
    try
    {
        var own = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(from))?["data"]?.AsArray();
        var vrRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(into));
        var vrData = vrRoot?["data"]?.AsArray();
        var ownGroup = own?.FirstOrDefault(g => (string?)g?["group_name"] == "/language");
        int at = vrData == null ? -1 : vrData.ToList().FindIndex(g => (string?)g?["group_name"] == "/language");
        if (ownGroup == null || vrData == null || at < 0) { Out.Say("RA01: your language settings weren't found - your VR settings use the mod's languages (change them in the game's menu)."); return; }
        vrData[at] = ownGroup.DeepClone();
        using (var stream = File.Create(into))   // RA01: M40 - written with a plain JSON writer (no serializer options), which the self-contained launcher build needs
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            vrRoot!.WriteTo(writer);
        Out.Say("RA01: your own language settings were carried into your VR settings.");
    }
    catch (Exception e) { Out.Say($"RA01: couldn't carry your language settings over ({e.Message}) - your VR settings use the mod's languages."); }
}

// RA01: M13-20 - removes the LOD fix, only if it is still ours (starts with our marker line). If it can't, the next launcher start writes over it anyway.
// Cost - CPU: extremely low (reads and deletes one tiny file, once); GPU: none.
static void RemoveLodFix(string path)
{
    try
    {
        if (File.Exists(path) && File.ReadAllText(path).StartsWith("; RA01_LodFix")) File.Delete(path);
        Out.Say("RA01: LOD fix removed from the game folder.");
    }
    catch (Exception e) { Out.Say($"RA01: couldn't remove the LOD fix ({e.Message}) - the next launcher start writes over it."); }
}

// RA01: M12-1 - true if the file exports our marker (read without running any of its code).
// Cost - CPU: extremely low (maps one small DLL and looks up one name, once); GPU: none (no GPU work).
static bool IsOurDoorman(string path)
{
    nint module = Win32.LoadLibraryExW(path, 0, 0x1); // RA01: DONT_RESOLVE_DLL_REFERENCES
    if (module == 0) return false;
    bool ours = Win32.GetProcAddress(module, DoormanMarker) != 0;
    Win32.FreeLibrary(module);
    return ours;
}

// RA01: M12-2 - reads GameResolutionPercent, HeadsetWidth and HeadsetHeight from RA01_Settings.ini and writes the game resolution for the doorman: both sides of the
// headset's per-eye size times the percent (its own shape kept), the longer side never past MaxSide, even numbers. Without a remembered headset size (first
// run with a new headset) no file is written and the game keeps its own list this once; RA01.dll remembers the size for next time.
// Cost - CPU: extremely low (reads a small text file and writes a tiny one, once); GPU: none (no GPU work).
static void WriteSize(string settings, string target)
{
    try
    {
        float percent = 50f; int headsetWidth = 0, headsetHeight = 0;
        if (File.Exists(settings))
            foreach (string line in File.ReadAllLines(settings))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.TrimStart().StartsWith('#')) continue;
                string key = line[..eq].Trim().ToLowerInvariant(), value = line[(eq + 1)..].Trim();
                if (key == "gameresolutionpercent" && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p)) percent = Math.Clamp(p, 25f, 140f);
                else if (key == "headsetwidth") int.TryParse(value, out headsetWidth);
                else if (key == "headsetheight") int.TryParse(value, out headsetHeight);
            }
        if (File.Exists(target)) File.Delete(target);
        if (headsetWidth <= 0 || headsetHeight <= 0)
        {
            Out.Say("RA01: the headset's size isn't known yet (first run with it?) - the game keeps its own resolutions this time; from the next start it gets yours.");
            return;
        }
        float factor = MathF.Min(percent / 100f, (float)MaxSide / Math.Max(headsetWidth, headsetHeight));
        int width = Math.Max(2, (int)MathF.Round(headsetWidth * factor / 2f) * 2), height = Math.Max(2, (int)MathF.Round(headsetHeight * factor / 2f) * 2);
        File.WriteAllText(target, $"{width}x{height}");
        Out.Say($"RA01: game resolution {width}x{height} ({percent:F0}% of the headset's {headsetWidth}x{headsetHeight}) handed to the doorman.");
    }
    catch (Exception e) { Out.Say($"RA01: couldn't hand the game resolution to the doorman ({e.Message}) - the game keeps its own resolutions this time."); }
}

// RA01: M12-1 - deletes the doorman, retrying for a few seconds while Windows lets go of the file. If it still can't, the next launcher start replaces it.
// Cost - CPU: extremely low (up to ten delete attempts half a second apart, once); GPU: none (no GPU work).
static void RemoveDoorman(string path)
{
    for (int i = 0; i < 10; i++)
    {
        try
        {
            if (File.Exists(path) && IsOurDoorman(path)) File.Delete(path);
            Out.Say("RA01: doorman removed from the game folder.");
            return;
        }
        catch (IOException) { Thread.Sleep(500); }
        catch (UnauthorizedAccessException) { Thread.Sleep(500); }
    }
    Out.Say("RA01: couldn't remove the doorman yet - it will be replaced or removed at the next launcher start.");
}

// RA01: looks for the running game process by name; null if it is not running.
// Cost - CPU: low (one scan of the process list, about a millisecond, once a second while waiting for the game); GPU: none (no GPU work).
// M20-4 - RA01_Settings.ini now lives in the launch folder's Settings folder (the mod moves it there on its first run); before that first run it may
// still sit beside the DLL, so that place is the fallback.
// Cost - CPU: extremely low (one existence check, once at launch); GPU: none.
static string SettingsPath(string folder)
{
    string moved = Path.Combine(folder, "Settings", SettingsFileName);
    return File.Exists(moved) ? moved : Path.Combine(folder, SettingsFileName);
}

static Process? FindGame() => Process.GetProcessesByName(GameProcess).FirstOrDefault();

// RA01: returns the base address of a module inside the game, or 0 if it is not loaded (or not readable yet).
// Cost - CPU: low (lists the game's loaded modules (a few hundred), about a millisecond, every 0.5 s while waiting for the renderer); GPU: none (no GPU work).
static nint FindModule(Process p, string name)
{
    try
    {
        p.Refresh();
        foreach (ProcessModule m in p.Modules)
            if (m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return m.BaseAddress;
    }
    catch (Win32Exception) { }          // RA01: the module list cannot be read during early startup.
    catch (InvalidOperationException) { } // RA01: the process exited while we were looking.
    return 0;
}

// RA01: runs a function inside the game on a new thread and waits (up to 10 s) for it to finish.
// Cost - CPU: extremely low (one thread creation and a wait of up to 10 s that is spent asleep; called twice); GPU: none (no GPU work).
static void RunRemote(nint proc, nint start, nint argument)
{
    nint thread = Win32.CreateRemoteThread(proc, 0, 0, start, argument, 0, out _);
    if (thread == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    Win32.WaitForSingleObject(thread, 10000);
    Win32.CloseHandle(thread);
}

// RA01: M40 - the launcher's voice: each line goes to Logs\RA01_launcher_log.txt (fresh each start) and to the window's status line (it was the console before).
// Cost - CPU: extremely low (one short file append per line, a handful per launch); GPU: none.
static class Out
{
    static string? logFile;
    static readonly object gate = new();

    public static void Start(string path)
    {
        logFile = path;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, $"RA01 launcher log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"); } catch { logFile = null; }
    }

    public static void Say(string text)
    {
        lock (gate)
            try { if (logFile != null) File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss} {text}\r\n"); } catch { }
        LauncherWindow.SetStatus(text);
    }

    // RA01: a plain Windows message box, for the one problem the window can't show (RA01.dll missing). Cost - CPU: extremely low; GPU: none.
    public static void Box(string text) => MessageBoxW(0, text, "RA01 launcher", 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint window, string text, string caption, uint type);
}

// RA01: the handful of Windows functions the launcher needs.
// Cost - CPU: extremely low (only declarations of Windows functions; each is called once or twice by a one-time program); GPU: none (no GPU work).
static class Win32
{
    const string K = "kernel32.dll";

    [DllImport(K, SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport(K, SetLastError = true)] public static extern nint VirtualAllocEx(nint proc, nint address, nuint size, uint type, uint protect);
    [DllImport(K, SetLastError = true)] public static extern bool VirtualFreeEx(nint proc, nint address, nuint size, uint type);
    [DllImport(K, SetLastError = true)] public static extern bool WriteProcessMemory(nint proc, nint address, byte[] buffer, nuint size, out nuint written);
    [DllImport(K, SetLastError = true)] public static extern nint CreateRemoteThread(nint proc, nint attributes, nuint stackSize, nint start, nint argument, uint flags, out uint threadId);
    [DllImport(K)] public static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport(K)] public static extern bool CloseHandle(nint handle);
    [DllImport(K, CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport(K)] public static extern bool FreeLibrary(nint module);
    [DllImport(K, CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string name);
    [DllImport(K, CharSet = CharSet.Ansi, ExactSpelling = true)] public static extern nint GetProcAddress(nint module, string name);
}

