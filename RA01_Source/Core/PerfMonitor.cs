using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M20-12 - THE PERFORMANCE TAB'S NUMBERS: game frame rate, CPU, GPU busy and GPU memory, so a player can tune their settings from inside the headset
// instead of watching Task Manager. Everything comes from Windows itself and is only READ:
//  - frame rate: our own Present count (PresentHook.Frames);
//  - CPU: Windows' process and system timers (GetProcessTimes / GetSystemTimes) - how much of the whole CPU the game used since the last look;
//  - GPU busy: Windows' performance counters (pdh.dll, the same numbers Task Manager shows): every process's 3D engine use, added up;
//  - GPU memory: the card's dedicated memory in use by everything (performance counters) against the card's size (DXGI), and the game's own share
//    (IDXGIAdapter3::QueryVideoMemoryInfo - asked from inside the game, so it answers for the game);
//  - RAM (M20-13): the PC's memory in use against its size (GlobalMemoryStatusEx, the same as Task Manager's Memory), and the game's own share (its working set).
// Like a car's dashboard gauges: they read the engine, they never touch it. Sampled once a second while the F10 window is open, and every LogEvery
// seconds for one log line (so a log shows whether memory was full when something went wrong). Any failure just leaves that number blank.
// Cost - CPU: low (a few Windows queries once a second while F10 is open - well under a millisecond; extremely low otherwise: one sample every 30 s);
// GPU: none (reads counters Windows already keeps).
internal static unsafe class PerfMonitor
{
    const int TickMs = 1000, LogEvery = 30;
    const uint PdhFmtDouble = 0x200, PdhMoreData = 0x800007D2;
    static int started;
    static volatile string frameText = "measuring...", cpuText = "measuring...", gpuText = "measuring...", memoryText = "measuring...", ramText = "measuring...", adviceText = "";
    static nint adapter3;                                  // RA01: the game's graphics card (IDXGIAdapter3), 0 if not found
    static ulong cardBytes;                                // RA01: its dedicated memory size
    static nint query, gpuCounter, memoryCounter;          // RA01: the performance counter query and its two counters (0 = unavailable)
    static long lastFrames, lastTick, lastProcess, lastSysIdle, lastSysBusy;

    // RA01: the tab's rows. Cost - CPU: extremely low (a string read); GPU: none.
    public static string Frames => frameText;
    public static string Cpu => cpuText;
    public static string Gpu => gpuText;
    public static string Memory => memoryText;
    public static string Ram => ramText;
    public static string Advice => adviceText;

    // RA01: starts the sampling thread. Call once at start-up. Cost - CPU: extremely low (starts one background thread); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref started, 1) == 1) return;
        new Thread(Loop) { IsBackground = true, Name = "RA01 performance" }.Start();
    }

    // RA01: once a second: sample if the F10 window is open or a log line is due; otherwise sleep. Any error is logged once and the gauges go blank.
    // Cost - CPU: see the class comment; GPU: none.
    static void Loop()
    {
        try
        {
            Open();
            int sinceLog = LogEvery - 5;                   // RA01: the first log line comes a few seconds in
            while (true)
            {
                Thread.Sleep(TickMs);
                bool log = ++sinceLog >= LogEvery;
                if (!SettingsMenu.Open && !log) { Sample(false); continue; } // RA01: keep the CPU baselines fresh cheaply, no counters
                Sample(true);
                if (log)
                {
                    sinceLog = 0;
                    Log.Write($"RA01 performance: {frameText}; CPU {cpuText}; GPU {gpuText}; GPU memory {memoryText}; PC RAM {ramText}{(adviceText.Length > 0 ? $" - {adviceText}" : "")}");
                }
            }
        }
        catch (Exception e) { frameText = cpuText = gpuText = memoryText = ramText = "unavailable"; Log.Write($"RA01 performance: stopped ({e.Message})"); }
    }

    // RA01: finds the game's card (the one with the most dedicated memory) and opens the two performance counters. Logs what it found.
    // Cost - CPU: low (once: lists the graphics cards and opens a counter query, a few milliseconds); GPU: none.
    static void Open()
    {
        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"), adapter3Id = new("645967a4-1392-4310-a798-8053ce3e93fd");
        nint factory = 0;
        if (CreateDXGIFactory1(&factoryId, &factory) >= 0)
        {
            byte* desc = stackalloc byte[320];
            for (uint i = 0; ; i++)
            {
                nint candidate = 0;
                if (((delegate* unmanaged<nint, uint, nint*, int>)Slot(factory, 12))(factory, i, &candidate) < 0) break; // RA01: IDXGIFactory1::EnumAdapters1
                ulong dedicated = 0;
                if (((delegate* unmanaged<nint, byte*, int>)Slot(candidate, 10))(candidate, desc) >= 0) dedicated = *(ulong*)(desc + 272); // RA01: GetDesc1 -> DedicatedVideoMemory
                nint three = 0;
                if (dedicated > cardBytes && ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(candidate, 0))(candidate, &adapter3Id, &three) >= 0)
                {
                    Release(adapter3);
                    adapter3 = three;
                    cardBytes = dedicated;
                }
                Release(candidate);
            }
            Release(factory);
        }
        nint q = 0, gpu = 0, memory = 0;
        if (PdhOpenQueryW(null, 0, &q) == 0)
        {
            if (PdhAddEnglishCounterW(q, "\\GPU Engine(*engtype_3D)\\Utilization Percentage", 0, &gpu) != 0) gpu = 0;
            if (PdhAddEnglishCounterW(q, "\\GPU Adapter Memory(*)\\Dedicated Usage", 0, &memory) != 0) memory = 0;
            PdhCollectQueryData(q);                        // RA01: a rate counter needs a first sample to compare against
            query = q; gpuCounter = gpu; memoryCounter = memory;
        }
        Log.Write($"RA01 performance: graphics card {(adapter3 != 0 ? $"found, {cardBytes / 1073741824.0:F1} GB" : "NOT found")}; GPU counters {(gpu != 0 ? "on" : "off")}, memory counters {(memory != 0 ? "on" : "off")}");
    }

    // RA01: one look at every gauge ('full' false: only the CPU baselines and frame count move on, so the first look after opening F10 is a real one-second figure).
    // Cost - CPU: low when full (one counter collection and a few small Windows calls), extremely low otherwise; GPU: none.
    static void Sample(bool full)
    {
        long now = Environment.TickCount64, frames = PresentHook.Frames;
        long create, exit, kernel, user, idle, sysKernel, sysUser;
        GetProcessTimes(GetCurrentProcess(), &create, &exit, &kernel, &user);
        GetSystemTimes(&idle, &sysKernel, &sysUser);
        long process = kernel + user, sysBusy = sysKernel + sysUser;    // RA01: Windows counts idle time inside the kernel time
        if (full && lastTick != 0 && now > lastTick)
        {
            double seconds = (now - lastTick) / 1000.0, fps = (frames - lastFrames) / seconds;
            frameText = fps > 0.5 ? $"{fps:F0} fps ({1000.0 / fps:F1} ms a frame)" : "no frames (a loading screen or a pause)";
            long sysAll = sysBusy - lastSysBusy, sysIdle = idle - lastSysIdle;
            double game = sysAll > 0 ? 100.0 * (process - lastProcess) / sysAll : 0, whole = sysAll > 0 ? 100.0 * (sysAll - sysIdle) / sysAll : 0;
            cpuText = $"Cyberpunk {Math.Clamp(game, 0, 100):F0}% - whole PC {Math.Clamp(whole, 0, 100):F0}%";
            double gpuBusy = -1, cardUsed = -1;
            if (query != 0 && PdhCollectQueryData(query) == 0)
            {
                if (gpuCounter != 0) gpuBusy = Math.Min(Sum(gpuCounter, false), 100);
                if (memoryCounter != 0) cardUsed = Sum(memoryCounter, true);
            }
            gpuText = gpuBusy >= 0 ? $"{gpuBusy:F0}% busy" : "unavailable";
            ulong gameBytes = 0, budget = 0;
            if (adapter3 != 0)
            {
                ulong* info = stackalloc ulong[4];       // RA01: DXGI_QUERY_VIDEO_MEMORY_INFO: Budget, CurrentUsage, AvailableForReservation, CurrentReservation
                if (((delegate* unmanaged<nint, uint, int, ulong*, int>)Slot(adapter3, 14))(adapter3, 0, 0, info) >= 0) { budget = info[0]; gameBytes = info[1]; } // RA01: QueryVideoMemoryInfo, local (dedicated) memory
            }
            double size = cardBytes > 0 ? cardBytes : budget, used = cardUsed >= 0 ? cardUsed : gameBytes;
            double share = size > 0 ? 100.0 * used / size : -1;
            memoryText = size > 0 ? $"{used / 1073741824.0:F1} of {size / 1073741824.0:F1} GB ({share:F0}%) - the game's share {gameBytes / 1073741824.0:F1} GB" : "unavailable";
            double ramShare = SampleRam();
            adviceText = share >= 90 ? "GPU memory nearly full - lower Game resolution or texture quality (a full card stops the world loading)"
                       : ramShare >= 90 ? "PC RAM nearly full - close background programs (browsers, launchers); Windows starts swapping to disk and the game hitches"
                       : gpuBusy >= 97 && fps > 0 && fps < 80 ? "the GPU is the limit - lower Game resolution, DLSS quality or graphics presets"
                       : game >= 90 || whole >= 95 ? "the CPU is the limit - lower crowd density or close background programs"
                       : "headroom left";
        }
        lastTick = now; lastFrames = frames; lastProcess = process; lastSysIdle = idle; lastSysBusy = sysBusy;
    }

    // RA01: M20-13 - the RAM gauge: the PC's memory in use of its size, as Task Manager shows it, plus the game's own share. Returns the percentage in use (-1 if unreadable).
    // Cost - CPU: extremely low (two quick Windows queries, once a second while F10 is open); GPU: none.
    static double SampleRam()
    {
        byte* status = stackalloc byte[64];
        *(uint*)status = 64;                               // RA01: MEMORYSTATUSEX.dwLength must be set
        if (!GlobalMemoryStatusEx(status)) { ramText = "unavailable"; return -1; }
        ulong total = *(ulong*)(status + 8), available = *(ulong*)(status + 16);
        if (total == 0) { ramText = "unavailable"; return -1; }
        double used = total - available, percent = 100.0 * used / total;
        byte* counters = stackalloc byte[72];
        *(uint*)counters = 72;                             // RA01: PROCESS_MEMORY_COUNTERS.cb
        string game = K32GetProcessMemoryInfo(GetCurrentProcess(), counters, 72) ? $" - the game's share {*(ulong*)(counters + 16) / 1073741824.0:F1} GB" : "";
        ramText = $"{used / 1073741824.0:F1} of {total / 1073741824.0:F1} GB ({percent:F0}%), {available / 1073741824.0:F1} GB free{game}";
        return percent;
    }

    // RA01: adds up every instance of a counter (all processes' 3D engines, or all cards' memory - the largest card wins for memory). Returns -1 if unreadable.
    // Cost - CPU: extremely low (two counter-array calls and a short loop); GPU: none.
    static double Sum(nint counter, bool largest)
    {
        uint bytes = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, &bytes, &count, null) != PdhMoreData || bytes == 0) return -1;
        byte* buffer = stackalloc byte[(int)Math.Min(bytes, 262144)];
        if (bytes > 262144 || PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, &bytes, &count, buffer) != 0) return -1;
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            byte* item = buffer + i * 24;                  // RA01: PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then status (4 + 4 padding) and the double
            if (*(uint*)(item + 8) > 1) continue;          // RA01: a bad reading (status other than valid / new data)
            double value = *(double*)(item + 16);
            total = largest ? Math.Max(total, value) : total + value;
        }
        return total;
    }
}
