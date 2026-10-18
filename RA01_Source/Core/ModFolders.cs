namespace RA01.Core;

// RA01: M20-4 - WHERE THE MOD KEEPS ITS FILES: everything in one place beside RA01.dll (the launch folder), nothing in the player's AppData, so a player
// troubleshooting can zip one folder:
//   <launch folder>\Logs\      RA01_<category>.txt (M41: one file per log category, see Log.cs), RA01_blackbox.bin
//   <launch folder>\Settings\  RA01_Settings.ini, RA01_GunCalibration.ini, RA01_ScopeParts.ini, RA01_FistPose.txt
// The first time, files from the old places (the launch folder itself, and %LOCALAPPDATA%\RA01 for the fist pose) are MOVED in, so nothing is lost.
// The old log and black box in %LOCALAPPDATA%\RA01 are left where they are (history only).
// Cost - CPU: extremely low (two folder names worked out once; at most four file moves, once ever); GPU: none.
internal static unsafe class ModFolders
{
    public static readonly string Logs, Settings;

    // RA01: works the two folders out, creates them and moves old files in. Never throws (a failure falls back to the launch folder itself).
    // RA01: Cost - CPU: extremely low (once, at the first use); GPU: none.
    static ModFolders()
    {
        string root = "";
        try
        {
            Win32.GetModuleHandleExW(0x6, (nint)(delegate* unmanaged<nint, uint>)&Entry.Start, out nint self); // RA01: the module that owns our own entry point = RA01.dll
            char* buffer = stackalloc char[1024];
            root = Path.GetDirectoryName(new string(buffer, 0, (int)Win32.GetModuleFileNameW(self, buffer, 1024))) ?? "";
        }
        catch { }
        Logs = Path.Combine(root, "Logs");
        Settings = Path.Combine(root, "Settings");
        try { Directory.CreateDirectory(Logs); } catch { Logs = root; }
        try { Directory.CreateDirectory(Settings); } catch { Settings = root; return; }
        foreach (string name in new[] { "RA01_Settings.ini", "RA01_GunCalibration.ini", "RA01_ScopeParts.ini" })
            MoveIn(Path.Combine(root, name), Path.Combine(Settings, name));
        MoveIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RA01", "RA01_FistPose.txt"), Path.Combine(Settings, "RA01_FistPose.txt"));
    }

    // RA01: moves one old file to its new place if it exists there and not here yet. Cost - CPU: extremely low (two existence checks, maybe one move); GPU: none.
    static void MoveIn(string from, string to)
    {
        try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); } catch { }
    }
}
