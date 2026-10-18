using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: the Windows and DirectX functions and structures the Core needs, kept in one place.
// Cost - CPU: extremely low (the class holds only declarations and three tiny COM helpers; the real cost is that of each Windows call it declares, see the notes on each group); GPU: none (declarations only).
internal static unsafe class Win32
{
    // RA01: D3D12_COMMAND_QUEUE_DESC. All zero means a normal direct queue.
    // Cost - CPU: extremely low (a plain data layout with no logic, 16 bytes, filled in once at start-up and when a queue is described);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct CommandQueueDesc
    {
        public int Type, Priority, Flags;
        public uint NodeMask;
    }

    // RA01: DXGI_SWAP_CHAIN_DESC1, in the same field order as the Windows SDK.
    // Cost - CPU: extremely low (a plain data layout with no logic, 48 bytes, filled in once at start-up); GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct SwapChainDesc1
    {
        public uint Width, Height, Format, Stereo, SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    // RA01: DXGI_SWAP_CHAIN_DESC, in the same field order as the Windows SDK (the two nested structs are laid out flat).
    // Cost - CPU: extremely low (a plain data layout with no logic, a small structure, filled in when a swapchain is described);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct SwapChainDesc
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanlineOrdering, Scaling, SampleCount, SampleQuality, BufferUsage, BufferCount;
        public nint OutputWindow;
        public int Windowed;
        public uint SwapEffect, Flags;
    }

    // RA01: D3D12_HEAP_PROPERTIES.
    // Cost - CPU: extremely low (a plain data layout with no logic, 20 bytes, filled in when a GPU memory heap is created);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct HeapProperties
    {
        public int Type, CpuPageProperty, MemoryPoolPreference;
        public uint CreationNodeMask, VisibleNodeMask;
    }

    // RA01: D3D12_RESOURCE_DESC, with the nested sample description laid out flat.
    // Cost - CPU: extremely low (a plain data layout with no logic, a few dozen bytes, filled in when a GPU texture or buffer is created);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceDesc
    {
        public int Dimension;
        public ulong Alignment, Width;
        public uint Height;
        public ushort DepthOrArraySize, MipLevels;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Layout, Flags;
    }

    // RA01: D3D12_RESOURCE_BARRIER, only the transition kind of its union, laid out flat.
    // Cost - CPU: extremely low (a plain data layout with no logic, 32 bytes, filled in whenever a copy needs a resource to change state);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct ResourceBarrier
    {
        public int Type, Flags;
        public nint Resource;
        public uint Subresource;
        public int StateBefore, StateAfter;
    }

    // RA01: M4 - MEMORY_BASIC_INFORMATION (64-bit layout): what VirtualQuery tells about a block of memory.
    // Cost - CPU: extremely low (a plain data layout with no logic, 48 bytes, filled in by each VirtualQuery call); GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect, PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type, Padding;
    }

    // RA01: M4 - THREADENTRY32: one line of the list of the game's threads.
    // Cost - CPU: extremely low (a plain data layout with no logic, 28 bytes, filled in for each thread while the thread list is walked at start-up);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadEntry
    {
        public uint Size, Usage, ThreadId, OwnerProcessId;
        public int BasePriority, DeltaPriority;
        public uint Flags;
    }

    // RA01: M5a-3 - the settings window (UI_settingsMenus): a rectangle, a point, the header of a bitmap, and where a copy into a texture reads from and goes to.
    // Cost - CPU: extremely low (a plain data layout with no logic, Rect, Point and CursorInfo below are small structures of a few integers each);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    // RA01: POINT: an x and y position.
    // Cost - CPU: extremely low (a plain data layout with no logic, 8 bytes); GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    // RA01: CURSORINFO: whether the mouse cursor is shown, which cursor it is and where.
    // Cost - CPU: extremely low (a plain data layout with no logic, 24 bytes, filled in by each GetCursorInfo call); GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)] public struct CursorInfo { public int Size, Flags; public nint Cursor; public Point Position; } // RA01: Flags bit 1 = the cursor is shown

    // RA01: BITMAPINFOHEADER, 40 bytes. A 32 bit bitmap with no colour table needs nothing after it.
    // Cost - CPU: extremely low (a plain data layout with no logic, 40 bytes, filled in when the settings window's bitmap is created);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    // RA01: D3D12_TEXTURE_COPY_LOCATION (48 bytes), laid out flat: a texture is named by its subresource number, a buffer by where its rows start (offset) and how they are laid out (the footprint).
    // Cost - CPU: extremely low (a plain data layout with no logic, 48 bytes, filled in whenever a texture copy is recorded);
    // GPU: none (a CPU-side structure, any GPU cost belongs to the Windows or D3D12 call it is passed to).
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct TextureCopyLocation
    {
        [FieldOffset(0)] public nint Resource;
        [FieldOffset(8)] public int Type;               // RA01: 0 = a subresource of a texture, 1 = a placed footprint in a buffer
        [FieldOffset(16)] public uint SubresourceIndex; // RA01: (texture)
        [FieldOffset(16)] public ulong Offset;          // RA01: (buffer) the footprint: where it starts, the format, and the size and row pitch of one image
        [FieldOffset(24)] public uint Format;
        [FieldOffset(28)] public uint Width;
        [FieldOffset(32)] public uint Height;
        [FieldOffset(36)] public uint Depth;
        [FieldOffset(40)] public uint RowPitch;
    }

    // RA01: the window functions for the mouse takeover (MenuInput) and the drawing functions for the window itself (MenuDraw).
    // Cost - CPU: low (each call is a Windows call whose cost is its own: the window and raw-input calls are a few microseconds, the drawing calls (FillRect, DrawTextW, Ellipse, Polygon) draw into a memory
    // bitmap on the CPU and cost more the bigger the area;
    // used only while the settings window is handled or drawn); GPU: none (this drawing is done by the CPU into system memory, not on the GPU).
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] public static extern nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(nint rawInput, uint command, void* data, uint* size, uint headerSize);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(CursorInfo* info);   // RA01: M5a-5 - whether the mouse cursor is shown, and which cursor
    [DllImport("user32.dll")] public static extern bool GetClipCursor(Rect* rect);         // RA01: M5a-5 - the area the mouse is kept inside (the whole screen when it is not kept in)
    [DllImport("user32.dll")] public static extern int FillRect(nint dc, Rect* rect, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(nint dc, string text, int length, Rect* rect, uint format);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, nint* bits, nint section, uint offset);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] public static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern bool Polygon(nint dc, Point* points, int count);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    // RA01: d3d12.dll and dxgi.dll are already loaded in the game, so these resolve to the game's own copies.
    // Cost - CPU: medium (D3D12CreateDevice builds a whole device, tens of milliseconds, and CreateDXGIFactory1 a few milliseconds, both only once at start-up in PresentHook.Install);
    // GPU: extremely low (creates a throwaway device object, no GPU work is submitted).
    [DllImport("d3d12.dll")] public static extern int D3D12CreateDevice(nint adapter, int minimumFeatureLevel, Guid* riid, nint* device);
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(Guid* riid, nint* factory);

    // RA01: M20-12 - the Performance tab's gauges: process and system CPU timers (kernel32) and Windows' performance counters (pdh.dll, built into Windows).
    // Cost - CPU: extremely low to low (each is a quick Windows query; the counter collection is the largest, well under a millisecond, once a second at most); GPU: none.
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(nint process, long* creation, long* exit, long* kernel, long* user);
    // RA01: M20-13 - the PC's memory (MEMORYSTATUSEX, 64 bytes: length +0, load % +4, total +8, available +16) and the game's own (PROCESS_MEMORY_COUNTERS, 72 bytes:
    // working set +16). Cost - CPU: extremely low (two quick Windows queries); GPU: none.
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(byte* status);
    [DllImport("kernel32.dll")] public static extern bool K32GetProcessMemoryInfo(nint process, byte* counters, uint size);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(long* idle, long* kernel, long* user);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string? source, nint userData, nint* query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, nint* counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, uint* bufferSize, uint* itemCount, void* items);

    // RA01: the calls that make and remove a window (used for the throwaway window at start-up).
    // Cost - CPU: low (the cost is that of the Windows call itself: creating or destroying a window is up to about a millisecond, done rarely); GPU: none (a CPU-side window, nothing is drawn).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint window);

    // RA01: module lookup, memory protection and thread id calls.
    // Cost - CPU: extremely low (the cost is that of the Windows call itself, about a microsecond each: GetCurrentThreadId runs on every Present, the others only at start-up); GPU: none (CPU-side lookups).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll")] public static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll")] public static extern uint GetModuleFileNameW(nint module, char* buffer, uint size);
    [DllImport("kernel32.dll")] public static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    // RA01: M67 - giving the game's window the keyboard back: which window is in front and whose thread owns it, borrowing that thread's input state for a
    // moment (Windows only lets a window come to the front from the thread that has the input), and bringing a window to the front.
    // Cost - CPU: extremely low (the cost of the Windows calls themselves, once per VR session at most); GPU: none.
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, nint processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint window);

    // RA01: M52 - Windows' own simple sound player (winmm.dll, part of Windows): plays a .wav file. SND_ASYNC returns at once and plays in the background.
    // Cost - CPU: extremely low (one Windows call; Windows decodes and plays the file on its own thread); GPU: none.
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern bool PlaySoundW(string? sound, nint module, uint flags);
    public const uint SndAsync = 0x1, SndNoDefault = 0x2, SndFilename = 0x20000; // RA01: M52 - SND_ASYNC, SND_NODEFAULT (no Windows beep if the file is missing), SND_FILENAME

    // RA01: M2 - needed to load openxr_loader.dll by its full path and to look up the functions inside it.
    // Cost - CPU: low (LoadLibraryW loads a DLL from disk, a few milliseconds, once; GetProcAddress is a lookup of a microsecond or so, called a few dozen times, all at start-up); GPU: none (loading and lookup only).
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] public static extern nint GetProcAddress(nint module, string name);

    // RA01: M4 - used to find, place and write the camera hook (see CameraHook.cs).
    // Cost - CPU: low (the cost is that of each Windows call itself: VirtualQuery, VirtualAlloc and CloseHandle are a microsecond or so, the thread snapshot calls walk every thread of the game, up to about a
    // millisecond in total;
    // used only when the camera hook is found and placed at start-up, and VirtualQuery also serves ExeScan); GPU: none (memory and thread bookkeeping only).
    [DllImport("kernel32.dll")] public static extern nuint VirtualQuery(nint address, MemoryBasicInformation* info, nuint length);
    [DllImport("kernel32.dll")] public static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    // RA01: M6-9 - used by BoneFinder to read the heap safely: unlike dereferencing a pointer straight into the game's memory, a bad address here just makes this one call return
    // false instead of crashing the process - Windows checks the memory is really there and copies it out as a single atomic step, with nothing in between that a bad
    // timing could catch it out on.
    [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, nuint* bytesRead);
    // RA01: M7-2 - the write side of the same idea: WriteProcessMemory checks the memory is really there and copies into it as one atomic step, so a bad address just fails
    // this one call instead of corrupting something or crashing the game, the same guarantee ReadProcessMemory gives on the way in.
    [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(nint process, nint address, void* buffer, nuint size, nuint* bytesWritten);
    [DllImport("kernel32.dll")] public static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll")] public static extern bool Thread32First(nint snapshot, ThreadEntry* entry);
    [DllImport("kernel32.dll")] public static extern bool Thread32Next(nint snapshot, ThreadEntry* entry);
    [DllImport("kernel32.dll")] public static extern nint OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);

    // RA01: tells the processor to reread code that was just written.
    // Cost - CPU: extremely low (the cost is that of the Windows call itself, a microsecond or so, called after code bytes are written at start-up); GPU: none (CPU-side only).
    [DllImport("kernel32.dll")] public static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    // RA01: M4b-1 - GetCurrentThreadStackLimits says where the running thread's own stack is (to ignore temporary cameras built on it).
    // M4b-2 - GetAsyncKeyState reads a key straight from the keyboard state, for every key binding (see controls_mapping\Keys.cs).
    // Cost - CPU: extremely low (a microsecond or less per call, direct calls with no hand-over to the .NET runtime: GetCurrentThreadStackLimits runs on each camera build, about 200 times a second,
    // GetAsyncKeyState once per key checked); GPU: none (CPU-side only).
    [DllImport("kernel32.dll"), SuppressGCTransition] public static extern void GetCurrentThreadStackLimits(nuint* low, nuint* high);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);

    // RA01: M4 - these three run while all the game's other threads are paused, so they must never wait for anything.
    // SuppressGCTransition turns them into plain direct calls with no hand-over to the .NET runtime.
    // Cost - CPU: low (each call is a few microseconds but it is repeated for every game thread, so about a millisecond in total; used only when the camera hook is written, once at start-up); GPU: none (CPU-side only).
    [DllImport("kernel32.dll"), SuppressGCTransition] public static extern uint SuspendThread(nint thread);
    [DllImport("kernel32.dll"), SuppressGCTransition] public static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll"), SuppressGCTransition] public static extern int GetThreadContext(nint thread, byte* context);

    // RA01: COM helpers shared by every class that talks to DirectX. A COM object starts with a pointer to its function table.
    // the function at position 'index' in a COM object's function table.
    // Cost - CPU: extremely low (two pointer reads, a few nanoseconds, runs before every COM call the mod makes); GPU: none (it only looks up an address).
    public static nint Slot(nint comObject, int index) => ((nint*)*(nint*)comObject)[index];

    // RA01: adds one to a COM object's use count, so the object is not freed while we still need it.
    // Cost - CPU: extremely low (one COM call, a few nanoseconds, only when an object is kept); GPU: none (a counter change, no GPU work).
    public static void AddRef(nint comObject)
    {
        if (comObject != 0) ((delegate* unmanaged<nint, uint>)Slot(comObject, 1))(comObject); // RA01: AddRef is slot 1 of every COM object
    }

    // RA01: takes one off a COM object's use count. The object is freed when the count reaches zero.
    // Cost - CPU: extremely low (one COM call, a few nanoseconds, unless it frees the object, which is the driver's cost; runs when a temporary object is dropped);
    // GPU: none (a counter change, freeing a GPU object is done by the driver).
    public static void Release(nint comObject)
    {
        if (comObject != 0) ((delegate* unmanaged<nint, uint>)Slot(comObject, 2))(comObject); // RA01: Release is slot 2 of every COM object
    }

    // RA01: a negative HRESULT is a failure.
    // Cost - CPU: extremely low (one compare, and a message is only built when a call failed); GPU: none (a check on a number).
    public static void Check(int hresult, string what)
    {
        if (hresult < 0) throw new InvalidOperationException($"{what} failed, HRESULT 0x{hresult:X8}");
    }
}
