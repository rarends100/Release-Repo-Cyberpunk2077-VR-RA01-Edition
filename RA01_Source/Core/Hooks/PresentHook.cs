using System.Diagnostics;
using System.Runtime.InteropServices;
using static RA01.Core.Win32; // RA01: Slot, AddRef, Release and Check are shared COM helpers

namespace RA01.Core;

// RA01: watches every frame the game presents by swapping the Present and Present1 entries in the swapchain's function table.
// M1.5 - it also watches ID3D12CommandQueue::ExecuteCommandLists to find which queue the game renders on.
// M3 - every Present is also offered to FrameCopy, which copies the picture on for the headset.
// no machine code is patched and no hook library is used, so game patches cannot break it.
// Cost - CPU: extremely low (per Present it is an atomic add, a clock read and a compare, about a microsecond at most, on the game's own render thread;
// the FrameCopy and CameraHook calls it makes are rated in their own files; a short log report is written once a second); GPU: none (it only counts and forwards the game's calls, it records no GPU work of its own).
internal static unsafe class PresentHook
{
    // RA01: positions of the functions we need inside the COM function tables (vtables).
    const int SlotGetDevice = 7;                // RA01: IDXGISwapChain (from IDXGIDeviceSubObject)
    const int SlotPresent = 8;                  // RA01: IDXGISwapChain::Present
    const int SlotPresent1 = 22;                // RA01: IDXGISwapChain1::Present1
    const int SlotCreateCommandQueue = 8;       // RA01: ID3D12Device
    const int SlotCreateSwapChainForHwnd = 15;  // RA01: IDXGIFactory2
    const int SlotExecuteCommandLists = 10;     // RA01: ID3D12CommandQueue
    const int SlotGetDesc = 18;                 // RA01: ID3D12CommandQueue

    const string IidFactory = "50c83a1c-e072-4c48-87b0-3630fa36a6d0"; // RA01: IDXGIFactory2
    const string IidDevice = "189819f1-1db6-4b57-be54-1821339b85f7";  // RA01: ID3D12Device
    const string IidQueue = "0ec870a6-5d7e-4c22-8cfc-5baae07616ed";   // RA01: ID3D12CommandQueue

    static readonly string[] Names = { "Present", "Present1" };

    static int installed;
    static int rendererChecked;
    static int errorLogged;
    static nint originalPresent, originalPresent1;
    static readonly long[] calls = new long[2];      // RA01: index 0 = Present, 1 = Present1
    static long frames;                              // RA01: M20-12 - every Present since start, never reset (the Performance tab's frame rate)

    // RA01: M20-12 - the game's Presents since start. Cost - CPU: extremely low (one atomic read); GPU: none.
    public static long Frames => Interlocked.Read(ref frames);
    static readonly uint[] lastThread = new uint[2];
    static long lastReport, nextReport;
    static int reports;

    // RA01: every command queue seen so far. Entries are only ever added, so reading needs no lock.
    const int MaxQueues = 8;
    static readonly string[] QueueTypeNames = { "direct", "bundle", "compute", "copy", "video decode", "video process", "video encode" };
    static readonly nint[] queues = new nint[MaxQueues];
    static readonly int[] queueTypes = new int[MaxQueues];
    static readonly long[] queueCalls = new long[MaxQueues];
    static int queueCount;
    static nint originalExecute;
    static nint mainQueue; // RA01: the busiest direct queue. OpenXR is bound to this one.

    // RA01: M2 - the game's device (kept from the first frame) and how long the main queue has stayed the same.
    // RA01: once it has held steady for a few reports, OpenXR is started, once.
    const int StableReportsNeeded = 3;
    static nint gameDevice;
    static int stableReports;
    static bool xrStarted;

    // RA01: installs the hooks once at start-up: builds a throwaway window, device, queue and swapchain, reads Present, Present1 and ExecuteCommandLists out of their function tables,
    // swaps in our detours, then releases the throwaway objects.
    // Cost - CPU: medium (runs once at start-up; creating a throwaway D3D12 device and swapchain takes tens of milliseconds, the patching itself is microseconds);
    // GPU: extremely low (a tiny 16x16 throwaway swapchain and queue are created once, no GPU work is submitted).
    public static void Install()
    {
        // RA01: running the launcher twice would hook our own hook and loop forever, so only ever install once per game run.
        if (Interlocked.Exchange(ref installed, 1) == 1)
        {
            Log.Write("RA01 Present hook already installed, skipping");
            return;
        }

        nint window = 0, factory = 0, device = 0, queue = 0, swapChain = 0;
        try
        {
            // RA01: step 1 - build a throwaway hidden window, device, queue and swapchain, only to learn where Present lives.
            window = Win32.CreateWindowExW(0, "STATIC", "RA01", 0, 0, 0, 16, 16, 0, 0, 0, 0);
            if (window == 0) throw new InvalidOperationException("CreateWindowExW failed");

            Guid iidFactory = new(IidFactory);
            Check(Win32.CreateDXGIFactory1(&iidFactory, &factory), "CreateDXGIFactory1");

            Guid iidDevice = new(IidDevice);
            Check(Win32.D3D12CreateDevice(0, 0xB000, &iidDevice, &device), "D3D12CreateDevice"); // RA01: 0xB000 = feature level 11_0

            Win32.CommandQueueDesc queueDesc = default;
            Guid iidQueue = new(IidQueue);
            Check(((delegate* unmanaged<nint, Win32.CommandQueueDesc*, Guid*, nint*, int>)Slot(device, SlotCreateCommandQueue))
                (device, &queueDesc, &iidQueue, &queue), "CreateCommandQueue");

            // RA01: 28 = R8G8B8A8_UNORM, 0x20 = render target output, 4 = flip/discard, the same kind of swapchain a DX12 game uses.
            Win32.SwapChainDesc1 desc = new() { Width = 16, Height = 16, Format = 28, SampleCount = 1, BufferUsage = 0x20, BufferCount = 2, SwapEffect = 4 };
            Check(((delegate* unmanaged<nint, nint, nint, Win32.SwapChainDesc1*, nint, nint, nint*, int>)Slot(factory, SlotCreateSwapChainForHwnd))
                (factory, queue, window, &desc, 0, 0, &swapChain), "CreateSwapChainForHwnd");

            // RA01: step 2 - read Present and Present1 out of the swapchain's function table.
            nint table = *(nint*)swapChain;
            nint present = ((nint*)table)[SlotPresent];
            nint present1 = ((nint*)table)[SlotPresent1];

            // RA01: safety - only patch if both really live inside dxgi.dll. Otherwise the log says who owns them and we do nothing.
            bool presentOk = InsideModule(present, "Present", "dxgi.dll");
            bool present1Ok = InsideModule(present1, "Present1", "dxgi.dll");
            if (!presentOk || !present1Ok)
            {
                Log.Write("RA01 Present hook NOT installed: the function table entries are not inside dxgi.dll");
                HookRegistry.Off(HookRegistry.Present, "the swap chain's table isn't inside dxgi.dll");   // RA01: M43
                return;
            }

            // RA01: step 3 - swap in our detours, keeping the originals so they can be called.
            originalPresent = present;
            originalPresent1 = present1;
            lastReport = Stopwatch.GetTimestamp();
            nextReport = lastReport + Stopwatch.Frequency;
            Patch(table, SlotPresent, (nint)(delegate* unmanaged<nint, uint, uint, int>)&PresentDetour);
            Patch(table, SlotPresent1, (nint)(delegate* unmanaged<nint, uint, uint, nint, int>)&Present1Detour);
            Log.Write("RA01 Present hook installed");
            HookRegistry.Found(HookRegistry.Present, 0, "dxgi.dll swap chain table");   // RA01: M43 - not in the game's exe, so no place to show
            ResolutionHook.Install(table); // RA01: M5a-10, M12-0 - the same swapchain table also holds the resize calls the resolution boost works through

            // RA01: M1.5 - the same trick on the throwaway queue's function table. All queues of this kind share it,
            // RA01: including the game's, so every ExecuteCommandLists call now tells us which queue is doing the work.
            nint queueTable = *(nint*)queue;
            nint execute = ((nint*)queueTable)[SlotExecuteCommandLists];
            if (InsideModule(execute, "ExecuteCommandLists", "d3d12.dll", "d3d12core.dll"))
            {
                originalExecute = execute;
                Patch(queueTable, SlotExecuteCommandLists, (nint)(delegate* unmanaged<nint, uint, nint, void>)&ExecuteDetour);
                Log.Write("RA01 ExecuteCommandLists hook installed");
            }
            else
            {
                Log.Write("RA01 ExecuteCommandLists hook NOT installed: the function table entry is not inside d3d12.dll or D3D12Core.dll");
            }
        }
        catch (Exception e)
        {
            Log.Write($"RA01 Present hook failed: {e.Message}");
        }
        finally
        {
            // RA01: release the throwaway objects. The patched function table lives on in dxgi.dll.
            Release(swapChain);
            Release(queue);
            Release(device);
            Release(factory);
            if (window != 0) Win32.DestroyWindow(window);
        }
    }

    // RA01: what the game calls instead of Present. Count the frame, then hand over to the real Present.
    // Cost - CPU: extremely low (runs on every Present, roughly 100 to 400 times a second, on the game's render thread: it calls Tick and forwards to the real Present);
    // GPU: none (the game's own Present is forwarded unchanged, this adds no GPU work).
    [UnmanagedCallersOnly]
    static int PresentDetour(nint swapChain, uint syncInterval, uint flags)
    {
        Tick(0, swapChain);
        return ((delegate* unmanaged<nint, uint, uint, int>)originalPresent)(swapChain, syncInterval, flags);
    }

    // RA01: what the game calls instead of Present1 (the version of Present that takes extra parameters). Count the frame, then hand over to the real Present1.
    // Cost - CPU: extremely low (runs on every Present1, roughly 100 to 400 times a second, on the game's render thread: it calls Tick and forwards to the real Present1);
    // GPU: none (the game's own Present1 is forwarded unchanged, this adds no GPU work).
    [UnmanagedCallersOnly]
    static int Present1Detour(nint swapChain, uint syncInterval, uint flags, nint parameters)
    {
        Tick(1, swapChain);
        return ((delegate* unmanaged<nint, uint, uint, nint, int>)originalPresent1)(swapChain, syncInterval, flags, parameters);
    }

    // RA01: what the game calls instead of ExecuteCommandLists. Note which queue it is, then hand over to the real one.
    // Cost - CPU: extremely low (runs on every ExecuteCommandLists, a few times per frame on the game's render thread: it calls TickQueue and forwards to the real function);
    // GPU: none (the game's command lists are forwarded unchanged, this adds no GPU work).
    [UnmanagedCallersOnly]
    static void ExecuteDetour(nint queue, uint count, nint lists)
    {
        TickQueue(queue);
        HudLayer.OnExecute(queue, count, lists); // RA01: M10-0 - the GPU's running order, only while a capture is recording
        ((delegate* unmanaged<nint, uint, nint, void>)originalExecute)(queue, count, lists);
    }

    // RA01: runs on every Present. It must never throw into the game, so everything is wrapped.
    // Cost - CPU: extremely low for its own work (per Present, roughly 100 to 400 times a second on the render thread: two atomic adds, a thread id read, a clock read and a compare; the CameraHook.OnPresent
    // and FrameCopy.OnPresent calls it makes are rated in their own files);
    // GPU: none of its own (FrameCopy.OnPresent is the part that records GPU work and is rated in FrameCopy.cs).
    static void Tick(int which, nint swapChain)
    {
        try
        {
            Interlocked.Increment(ref calls[which]);
            Interlocked.Increment(ref frames);
            lastThread[which] = Win32.GetCurrentThreadId();

            if (Interlocked.Exchange(ref rendererChecked, 1) == 0)
                LogRenderer(swapChain);

            CameraHook.OnPresent();         // RA01: M4a - note how long ago the game built its latest camera. M5a-3: this also counts the Present, so it must come before the copy below
            RenderCameraProbe.OnPresent();  // RA01: M26-0/M26-1 - which camera build the render side really drew: the exact pose for the copy below (after the count above)
            FrameCopy.OnPresent(swapChain); // RA01: M3 - copy this frame for the headset (does nothing until the headset screen is ready)
            FrameCopy.StampMenu(swapChain, SettingsMenu.Open && (Tuning.MenuOnMonitor || !XrSession.Running)); // RA01: M29-1 - the open settings window onto the monitor, after the headset's grab
            HudLayer.OnPresent(swapChain);  // RA01: M10-0 - moves a one-frame draw-pass capture along (Dev Only Tools)
            ResolutionHook.OnPresent();     // RA01: M12-2 - tells the doorman a new game resolution once the slider settles

            // RA01: once per second, exactly one thread wins the right to write the report.
            long now = Stopwatch.GetTimestamp();
            long due = Volatile.Read(ref nextReport);
            if (now >= due && Interlocked.CompareExchange(ref nextReport, now + Stopwatch.Frequency, due) == due)
                Report(now);
        }
        catch (Exception e)
        {
            LogErrorOnce(e);
        }
    }

    // RA01: runs on every ExecuteCommandLists, so it stays cheap: find the queue, count the call.
    // Cost - CPU: extremely low (runs on every ExecuteCommandLists, a few times per frame on the render thread: a loop over at most 8 queues and one atomic add); GPU: none (bookkeeping only).
    static void TickQueue(nint queue)
    {
        try
        {
            int i = FindQueue(queue);
            if (i < 0) i = AddQueue(queue);
            if (i >= 0) Interlocked.Increment(ref queueCalls[i]);
        }
        catch (Exception e)
        {
            LogErrorOnce(e);
        }
    }

    // RA01: the position of 'queue' in the list of queues seen so far, or -1 if it has not been seen yet. Reading the list needs no lock.
    // Cost - CPU: extremely low (a loop of at most 8 pointer compares, runs once per ExecuteCommandLists call); GPU: none (bookkeeping only).
    static int FindQueue(nint queue)
    {
        int count = Volatile.Read(ref queueCount);
        for (int i = 0; i < count; i++)
            if (queues[i] == queue) return i;
        return -1;
    }

    // RA01: only reached the first time a queue is seen, so taking a lock here costs nothing per frame.
    // Cost - CPU: extremely low (a lock, one COM call and one log line, but it runs at most 8 times per game run, the first time each queue is seen); GPU: none (bookkeeping only).
    static int AddQueue(nint queue)
    {
        lock (queues)
        {
            int existing = FindQueue(queue); // RA01: another thread may have added it a moment ago
            if (existing >= 0) return existing;

            int index = queueCount;
            if (index == MaxQueues) return -1;
            queues[index] = queue;
            queueTypes[index] = GetQueueType(queue);
            Volatile.Write(ref queueCount, index + 1); // RA01: publish only once the entry is complete

            Log.Write($"RA01 new queue 0x{queue:X}: {QueueTypeName(queueTypes[index])}");
            return index;
        }
    }

    // RA01: GetDesc returns a struct by value. On x64 that arrives through a hidden pointer passed right after 'this'.
    // Cost - CPU: extremely low (one COM call, runs once for each new queue, at most 8 times per game run); GPU: none (only reads the queue's description).
    static int GetQueueType(nint queue)
    {
        Win32.CommandQueueDesc desc = default;
        ((delegate* unmanaged<nint, Win32.CommandQueueDesc*, Win32.CommandQueueDesc*>)Slot(queue, SlotGetDesc))(queue, &desc);
        return desc.Type;
    }

    // RA01: the readable name of a queue type number, for the log.
    // Cost - CPU: extremely low (an array lookup or a small string build, only when a log line is written); GPU: none (text only).
    static string QueueTypeName(int type) => type >= 0 && type < QueueTypeNames.Length ? QueueTypeNames[type] : $"type {type}";

    // RA01: a hook must never crash the game, so an error is written once and then ignored.
    // Cost - CPU: extremely low (one atomic exchange per error, and a single log line the first time only); GPU: none (logging only).
    static void LogErrorOnce(Exception e)
    {
        if (Interlocked.Exchange(ref errorLogged, 1) == 0)
            Log.Write($"RA01 hook error (logged once): {e.Message}");
    }

    // RA01: first frame only - ask the swapchain for its D3D12 device and queue. Success proves the game renders with DX12.
    // Cost - CPU: extremely low (runs once, on the first frame: two COM calls and one log line); GPU: none (it only asks the swapchain for its device and queue, no GPU work).
    static void LogRenderer(nint swapChain)
    {
        Guid iidDevice = new(IidDevice);
        Guid iidQueue = new(IidQueue);
        nint device = 0, queue = 0;
        var getDevice = (delegate* unmanaged<nint, Guid*, nint*, int>)Slot(swapChain, SlotGetDevice);
        int hrDevice = getDevice(swapChain, &iidDevice, &device);
        int hrQueue = getDevice(swapChain, &iidQueue, &queue);

        string verdict = hrDevice >= 0 || hrQueue >= 0 ? "DX12 confirmed" : "DX12 NOT confirmed";
        Log.Write($"RA01 first frame: swapchain 0x{swapChain:X}, device 0x{device:X} (hr 0x{hrDevice:X8}), queue 0x{queue:X} (hr 0x{hrQueue:X8}) - {verdict}");

        gameDevice = device; // RA01: keep this reference, OpenXR needs the device for the whole game run
        Release(queue);
        FrameCopy.SetTarget(swapChain); // RA01: M3 - note the back buffer size and format for the headset copy
    }

    // RA01: once per second - how many calls happened, the average time between them, and on which thread.
    // Cost - CPU: low (runs once a second on the render thread: a few log lines, each a small file append of tens to a few hundred microseconds, plus the FrameCopy and CameraHook log calls; the queue loop is
    // at most 8 steps; XrSession.Start, called once, is rated in its own file);
    // GPU: none (counting and logging only).
    static void Report(long now)
    {
        double seconds = (now - lastReport) / (double)Stopwatch.Frequency;
        lastReport = now;
        for (int i = 0; i < 2; i++)
        {
            long count = Interlocked.Exchange(ref calls[i], 0);
            if (count > 0)
                Log.Write($"RA01 {Names[i]}: {count} calls in {seconds:F2}s = {seconds * 1000 / count:F2} ms/call (thread {lastThread[i]})");
        }
        FrameCopy.LogSecond();
        CameraHook.LogSecond(); // RA01: M4a

        // RA01: how busy each queue was this second. The busiest direct (graphics) queue is our best guess for the main one.
        // RA01: M4a - the per-queue lines are only written every 10th second, to keep the log short. The counting itself never stops.
        bool writeQueueLines = reports++ % 10 == 0;
        int queueTotal = Volatile.Read(ref queueCount);
        long busiest = 0;
        nint previous = mainQueue;
        nint best = previous;
        for (int i = 0; i < queueTotal; i++)
        {
            long executed = Interlocked.Exchange(ref queueCalls[i], 0);
            if (executed == 0) continue;
            if (writeQueueLines)
                Log.Write($"RA01 queue 0x{queues[i]:X} ({QueueTypeName(queueTypes[i])}): {executed} ExecuteCommandLists calls");
            if (queueTypes[i] == 0 && executed > busiest) { busiest = executed; best = queues[i]; }
        }
        if (best != previous)
        {
            mainQueue = best;
            stableReports = 0;
            Log.Write($"RA01 main queue is now 0x{mainQueue:X}");
        }
        else if (best != 0)
        {
            stableReports++;
        }

        // RA01: M2 - the main queue has held steady, so the game is really rendering. Hand its device and queue to OpenXR.
        if (stableReports >= StableReportsNeeded && gameDevice != 0 && !xrStarted)
        {
            xrStarted = true;
            AddRef(mainQueue); // RA01: keep the queue alive while OpenXR uses it, released with the process
            Log.Write($"RA01 main queue stable, starting OpenXR (device 0x{gameDevice:X}, queue 0x{mainQueue:X})");
            FrameCopy.SetGame(gameDevice, mainQueue); // RA01: M29-1 - the monitor stamp works with or without the headset
            FlatMenu.Start();                          // RA01: M29-1 - F10 on the monitor whenever the headset's loop isn't running
            XrSession.Start(gameDevice, mainQueue);
        }
    }

    // RA01: true if the address belongs to one of the allowed modules. The owning module is written to the log either way.
    // Cost - CPU: extremely low (a few Windows lookups and one log line, runs only a few times at start-up while the hooks are installed); GPU: none (module lookup only).
    internal static bool InsideModule(nint address, string what, params string[] allowed)
    {
        Win32.GetModuleHandleExW(0x6, address, out nint owner); // RA01: 0x6 = look up by address, do not add a reference
        string name = "unknown";
        if (owner != 0)
        {
            char* buffer = stackalloc char[260];
            name = new string(buffer, 0, (int)Win32.GetModuleFileNameW(owner, buffer, 260));
        }
        Log.Write($"RA01 {what} = 0x{address:X} inside {name}");
        foreach (string module in allowed)
            if (owner != 0 && owner == Win32.GetModuleHandleW(module)) return true;
        return false;
    }

    // RA01: M10-0 - the quiet version of InsideModule for D3D12 (no log line): true if the address is inside d3d12.dll or D3D12Core.dll.
    // Cost - CPU: extremely low (two Windows module lookups); GPU: none.
    internal static bool InsideD3D12(nint address)
    {
        Win32.GetModuleHandleExW(0x6, address, out nint owner);
        return owner != 0 && (owner == Win32.GetModuleHandleW("d3d12.dll") || owner == Win32.GetModuleHandleW("d3d12core.dll"));
    }

    // RA01: overwrite one entry of a function table. The table sits in read-only memory, so unlock it just for the write.
    // Cost - CPU: extremely low (two VirtualProtect calls and one pointer write, about ten microseconds, once per hooked function at start-up); GPU: none (it changes a CPU-side table only).
    internal static void Patch(nint table, int index, nint replacement)
    {
        nint* slot = (nint*)table + index;
        if (!Win32.VirtualProtect((nint)slot, (nuint)sizeof(nint), 0x04, out uint oldProtect)) // RA01: 0x04 = read/write
            throw new InvalidOperationException("VirtualProtect failed");
        Interlocked.Exchange(ref *slot, replacement);
        Win32.VirtualProtect((nint)slot, (nuint)sizeof(nint), oldProtect, out _);
    }
}
