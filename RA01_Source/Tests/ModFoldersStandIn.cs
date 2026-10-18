namespace RA01.Core;

// RA01: M47 - the tests' own ModFolders (the real Core\ModFolders.cs is left out of the test build). The real one finds RA01.dll's folder and MOVES old files in
// from the launch folder and %LOCALAPPDATA%\RA01 - a test run must never do that on your PC. This one points Logs and Settings at a fresh folder under the
// system temp folder, one per test run, so the tests write their files there and nowhere else.
// Cost - CPU: extremely low (two folder names and two folder creations, once per test run); GPU: none.
internal static class ModFolders
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), $"RA01_Tests_{Environment.ProcessId}_{DateTime.Now:HHmmss}");
    public static readonly string Logs = Path.Combine(Root, "Logs");
    public static readonly string Settings = Path.Combine(Root, "Settings");

    // RA01: creates the two folders. Cost - CPU: extremely low (two folder creations, once); GPU: none.
    static ModFolders()
    {
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Settings);
    }
}
