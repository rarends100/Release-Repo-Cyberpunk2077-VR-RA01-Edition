using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RA01.Core;

// RA01: M66 - THE RUNTIME REPORT (log only, nothing is changed): once at start, right after the "OpenXR runtime:" line, a few lines in the Core VR log that say
// everything about the VR setup around RA01 - like the inspection sticker inside a machine's door listing every part it was built with, so a strange case
// (SteamVR through Virtual Desktop's left eye, 2026-10-12) can be pinned down from the log instead of by guessing:
//  - every OpenXR extension the runtime offers (name and version);
//  - the OpenXR runtime Windows has set as active, every runtime it knows, and any override in the XR_RUNTIME_JSON environment variable;
//  - every OpenXR API layer installed (implicit ones load into every OpenXR app by themselves: OpenXR Toolkit and the like) and whether each is on;
//  - which VR programs are running (SteamVR's server, compositor and dashboard, Virtual Desktop, the Oculus/Meta service, Pimax, ALVR, Mixed Reality);
//  - SteamVR's own settings file (steamvr.vrsettings), every setting in it except the headset's serial number and Steam's install number.
// Any part that can't be read says so in its line and the rest carry on. Windows only (the game is).
// Cost - CPU: low, once at start (a few registry reads, one process list, one small file read: a few milliseconds); GPU: none.
[SupportedOSPlatform("windows")] internal static unsafe class RuntimeReport
{
    const string Khronos = @"SOFTWARE\Khronos\OpenXR\1";
    static readonly string[] VrPrograms = { "vrserver", "vrcompositor", "vrmonitor", "vrdashboard", "vrstartup", "VirtualDesktop.Streamer", "VirtualDesktop.Service",
        "OVRServer_x64", "OculusClient", "PimaxClient", "alvr_dashboard", "MixedRealityPortal", "steam" };

    // RA01: writes the whole report. Called once by XrSession after the runtime's name is known.
    // Cost - CPU: low (see the class comment); GPU: none.
    public static void Write()
    {
        Part("extensions", Extensions);
        Part("runtimes", Runtimes);
        Part("API layers", Layers);
        Part("VR programs", Programs);
        Part("SteamVR settings", SteamVrSettings);
    }

    // RA01: runs one part of the report; a failure is one line, never a stop. Cost - CPU: extremely low (a try around the part); GPU: none.
    static void Part(string what, Func<string> part)
    {
        try { Log.Write("RA01 VR setup (M66): " + part()); }
        catch (Exception e) { Log.Write($"RA01 VR setup (M66): {what} could not be read ({e.Message})"); }
    }

    // RA01: every extension the runtime offers, with its version. Cost - CPU: extremely low (two runtime calls, once); GPU: none.
    static string Extensions()
    {
        if (Xr.EnumerateInstanceExtensionProperties == null) return "extensions - the loader has no xrEnumerateInstanceExtensionProperties";
        uint count;
        if (Xr.EnumerateInstanceExtensionProperties(null, 0, &count, null) < 0 || count > 1024) return "extensions - the runtime would not list them";
        var list = new Xr.ExtensionProperties[count];
        for (int i = 0; i < list.Length; i++) list[i].Type = Xr.TypeExtensionProperties;
        var names = new List<string>();
        fixed (Xr.ExtensionProperties* first = list)
        {
            if (count > 0 && Xr.EnumerateInstanceExtensionProperties(null, count, &count, first) < 0) return "extensions - the runtime would not list them";
            for (int i = 0; i < count && i < list.Length; i++) names.Add($"{Xr.GetText(first[i].ExtensionName)} v{first[i].ExtensionVersion}");
        }
        names.Sort(StringComparer.Ordinal);
        return $"{names.Count} OpenXR extensions offered: {string.Join(", ", names)}";
    }

    // RA01: the active runtime (machine-wide, and a per-user one if set), every runtime Windows knows (on = 0), and the environment override.
    // Cost - CPU: extremely low (a few registry reads, once); GPU: none.
    static string Runtimes()
    {
        string active = Registry.LocalMachine.OpenSubKey(Khronos)?.GetValue("ActiveRuntime") as string ?? "none set";
        string user = Registry.CurrentUser.OpenSubKey(Khronos)?.GetValue("ActiveRuntime") as string ?? "";
        string env = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") ?? "";
        return $"active OpenXR runtime {active}{(user.Length > 0 ? $"; per-user {user}" : "")}{(env.Length > 0 ? $"; XR_RUNTIME_JSON overrides it: {env}" : "")}; "
            + $"runtimes Windows knows: {Values(Registry.LocalMachine, Khronos + @"\AvailableRuntimes")}";
    }

    // RA01: every API layer, implicit and explicit, machine-wide and per-user, each "on" or "off" (Windows' value 0 means on). Cost - CPU: extremely low; GPU: none.
    static string Layers()
    {
        string env = Environment.GetEnvironmentVariable("XR_ENABLE_API_LAYERS") ?? "";
        return $"implicit (load by themselves) machine: {Values(Registry.LocalMachine, Khronos + @"\ApiLayers\Implicit")}; user: {Values(Registry.CurrentUser, Khronos + @"\ApiLayers\Implicit")}; "
            + $"explicit machine: {Values(Registry.LocalMachine, Khronos + @"\ApiLayers\Explicit")}; user: {Values(Registry.CurrentUser, Khronos + @"\ApiLayers\Explicit")}"
            + (env.Length > 0 ? $"; XR_ENABLE_API_LAYERS={env}" : "");
    }

    // RA01: a registry key's values as "name (on)" / "name (off)" - OpenXR's lists keep a file path as the name and 0 for on. Cost - CPU: extremely low; GPU: none.
    static string Values(RegistryKey root, string path)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key == null) return "none";
        string[] names = key.GetValueNames();
        if (names.Length == 0) return "none";
        return string.Join(", ", names.Select(n => $"{n} ({(key.GetValue(n) is int v && v == 0 ? "on" : "off")})"));
    }

    // RA01: which of the known VR programs are running, and how many of each. Cost - CPU: low (one process list per name, once); GPU: none.
    static string Programs()
    {
        var running = new List<string>();
        foreach (string name in VrPrograms)
        {
            Process[] found = Process.GetProcessesByName(name);
            if (found.Length > 0) running.Add(found.Length > 1 ? $"{name} x{found.Length}" : name);
            foreach (Process p in found) p.Dispose();
        }
        return "running VR programs: " + (running.Count > 0 ? string.Join(", ", running) : "none of the known ones");
    }

    // RA01: SteamVR's settings file as it is on disk, one "section.key = value" per setting; the serial number and install number are left out.
    // Cost - CPU: extremely low (one small file, once); GPU: none.
    static string SteamVrSettings()
    {
        string steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string ?? "";
        if (steam.Length == 0) return "SteamVR settings - Steam isn't installed (no SteamPath)";
        string file = Path.Combine(steam, "config", "steamvr.vrsettings");
        if (!File.Exists(file)) return $"SteamVR settings - no {file}";
        var pairs = new List<string>();
        string section = "";
        foreach (string raw in File.ReadAllLines(file))
        {
            string line = raw.Trim().TrimEnd(',');
            if (line.EndsWith("{") && line.Contains('"')) { section = line.Split('"')[1]; continue; }
            if (!line.StartsWith("\"") || !line.Contains(':')) continue;
            string name = line.Split('"')[1];
            if (name.Contains("Serial", StringComparison.OrdinalIgnoreCase) || name.Equals("installID", StringComparison.OrdinalIgnoreCase)) continue;
            pairs.Add($"{section}.{name} = {line[(line.IndexOf(':') + 1)..].Trim()}");
        }
        return $"SteamVR settings ({file}): {string.Join("; ", pairs)}";
    }
}
