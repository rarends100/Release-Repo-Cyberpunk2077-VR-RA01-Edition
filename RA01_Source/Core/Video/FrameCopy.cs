using System.Diagnostics;
using static RA01.Core.Win32; // RA01: Slot, Release and Check are shared COM helpers

namespace RA01.Core;

// RA01: M3 - carries the game's finished picture into the headset with two GPU copies, both recorded on the game's own direct queue:
// 1) at every Present (game thread): the back buffer is copied into one of a few private "mirror" textures.
// 2) every headset frame (OpenXR thread): a mirror is copied into the headset's swapchain image.
// the mirrors decouple the two rates (game and headset do not run in step) and the two threads.
// M5a - there are several mirrors, used in turn, each remembering when it was grabbed, so the headset thread can pick the left eye's and the right eye's picture separately.
// nothing here ever waits for the GPU: if it is still busy, that copy is skipped. Any real error switches the whole feature off for good.
// M13-16 - the MONITOR EYE (VR Video tab "Monitor shows"): in 3D the game draws the eyes in turn, so the desktop window (and a recording of it) shakes
// between two viewpoints. With one eye picked, every picture of the OTHER eye is covered, just before it is shown on the monitor, by the newest mirror of the
// picked eye - one more whole-frame copy on the game's queue for half the frames. The headset never reads the back buffer (it reads the mirrors, grabbed
// first), so it is not affected.
// Cost - CPU: low (per Present about 100-400 a second but only up to 240 grabs a second do real work, a few tens of microseconds each, plus about 180 headset copies a second);
// GPU: medium (whole game frame copies in video memory, one per grab and one per eye per headset frame, megabytes each, so it grows with the game's resolution).
internal static unsafe class FrameCopy
{
    // RA01: positions of the functions used inside the COM function tables (vtables), checked against Microsoft's d3d12.h and dxgi headers.
    const int SlotQueryInterface = 0;              // RA01: IDXGISwapChain::QueryInterface
    const int SlotGetBuffer = 9;                   // RA01: IDXGISwapChain::GetBuffer
    const int SlotSwapChainGetDesc = 12;           // RA01: IDXGISwapChain::GetDesc
    const int SlotGetCurrentBackBufferIndex = 36;  // RA01: IDXGISwapChain3::GetCurrentBackBufferIndex
    const int SlotCreateCommandAllocator = 9;      // RA01: ID3D12Device::CreateCommandAllocator
    const int SlotCreateCommandList = 12;          // RA01: ID3D12Device::CreateCommandList
    const int SlotCreateCommittedResource = 27;    // RA01: ID3D12Device::CreateCommittedResource
    const int SlotCreateFence = 36;                // RA01: ID3D12Device::CreateFence
    const int SlotExecuteCommandLists = 10;        // RA01: ID3D12CommandQueue::ExecuteCommandLists
    const int SlotSignal = 14;                     // RA01: ID3D12CommandQueue::Signal
    const int SlotAllocatorReset = 8;              // RA01: ID3D12CommandAllocator::Reset
    const int SlotListClose = 9;                   // RA01: ID3D12GraphicsCommandList::Close
    const int SlotListReset = 10;                  // RA01: ID3D12GraphicsCommandList::Reset
    const int SlotCopyTextureRegion = 16;          // RA01: ID3D12GraphicsCommandList::CopyTextureRegion (M5a-3)
    const int SlotCopyResource = 17;               // RA01: ID3D12GraphicsCommandList::CopyResource
    const int SlotResourceBarrier = 26;            // RA01: ID3D12GraphicsCommandList::ResourceBarrier
    const int SlotGetCompletedValue = 8;           // RA01: ID3D12Fence::GetCompletedValue
    const int SlotMap = 8;                         // RA01: ID3D12Resource::Map (M5a-3)

    const string IidSwapChain3 = "94d99bdb-f1f8-4ab0-b236-7da0170edab1";        // RA01: IDXGISwapChain3
    const string IidResource = "696442be-a72e-4059-bc79-5b5c98040fad";          // RA01: ID3D12Resource
    const string IidCommandAllocator = "6102dee4-af59-4b09-b999-b44d73f09b24";  // RA01: ID3D12CommandAllocator
    const string IidCommandList = "5b160d0f-ac1b-4185-8ba8-b3ae42a5a455";       // RA01: ID3D12GraphicsCommandList
    const string IidFence = "0a753dcf-c4d8-4b91-adf6-be5a60d95a76";             // RA01: ID3D12Fence

    // RA01: what a texture is currently "ready for". A copy needs COPY_SOURCE or COPY_DEST, so we switch and switch back.
    const int StatePresent = 0x0;         // RA01: D3D12_RESOURCE_STATE_PRESENT
    const int StateRenderTarget = 0x4;    // RA01: D3D12_RESOURCE_STATE_RENDER_TARGET
    const int StateCopyDest = 0x400;      // RA01: D3D12_RESOURCE_STATE_COPY_DEST
    const int StateCopySource = 0x800;    // RA01: D3D12_RESOURCE_STATE_COPY_SOURCE
    const int StateGenericRead = 0xAC3;   // RA01: D3D12_RESOURCE_STATE_GENERIC_READ, what an upload buffer must start in and stay in

    const int HeapTypeDefault = 1;        // RA01: D3D12_HEAP_TYPE_DEFAULT
    const int HeapTypeUpload = 2;         // RA01: D3D12_HEAP_TYPE_UPLOAD: memory the CPU writes and the GPU reads
    const int HeapTypeReadback = 3;       // RA01: M25-1 - D3D12_HEAP_TYPE_READBACK: memory the GPU writes and the CPU reads
    const int DimensionBuffer = 1;        // RA01: D3D12_RESOURCE_DIMENSION_BUFFER
    const int LayoutRowMajor = 1;         // RA01: D3D12_TEXTURE_LAYOUT_ROW_MAJOR (what a buffer must have)
    const int HeapFlagNone = 0;           // RA01: D3D12_HEAP_FLAG_NONE
    const int DimensionTexture2D = 3;     // RA01: D3D12_RESOURCE_DIMENSION_TEXTURE2D
    const int LayoutUnknown = 0;          // RA01: D3D12_TEXTURE_LAYOUT_UNKNOWN
    const int ResourceFlagNone = 0;       // RA01: D3D12_RESOURCE_FLAG_NONE
    const int BarrierTransition = 0;      // RA01: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION
    const int BarrierFlagNone = 0;        // RA01: D3D12_RESOURCE_BARRIER_FLAG_NONE
    const uint AllSubresources = 0xFFFFFFFF; // RA01: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
    const int ListTypeDirect = 0;         // RA01: D3D12_COMMAND_LIST_TYPE_DIRECT
    const int FenceFlagNone = 0;          // RA01: D3D12_FENCE_FLAG_NONE

    // RA01: the picture formats we can carry over unchanged (8 bits per channel). 10-bit and HDR need a conversion, that comes later.
    const int FormatR8G8B8A8 = 28;        // RA01: DXGI_FORMAT_R8G8B8A8_UNORM
    const int FormatR8G8B8A8Srgb = 29;    // RA01: DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
    const int FormatB8G8R8A8 = 87;        // RA01: DXGI_FORMAT_B8G8R8A8_UNORM
    const int FormatB8G8R8A8Srgb = 91;    // RA01: DXGI_FORMAT_B8G8R8A8_UNORM_SRGB

    public const int Mirrors = 6;         // RA01: about 60 ms of pictures at 100 grabs a second, more than the game's pipeline is deep
    const int MaxGrabsPerSecond = 240;    // RA01: M5a-3 - every game frame is grabbed (the game runs 120 to 170 frames a second in stereo); only the menus' 900+ Presents a second are thinned out

    static int state;                     // RA01: 0 = not ready yet, 1 = running, 2 = switched off for good
    static int hasFrame;                  // RA01: 1 once a mirror holds a game picture
    static int otherSwapChainLogged;
    static nint device, queue;
    static readonly nint[] mirrors = new nint[Mirrors];  // RA01: the newest pictures, used in turn
    static readonly long[] mirrorTime = new long[Mirrors]; // RA01: the Stopwatch time each mirror was grabbed (0 = nothing in it yet)
    static readonly long[] mirrorStamp = new long[Mirrors]; // RA01: M5a-2 - the pose stamp of the camera build each mirror's picture came from (0 = it was not steered)
    static int nextMirror;                // RA01: the mirror the next grab goes into
    static nint target;                   // RA01: the game's swapchain we copy from (the first one that presented)
    static nint targetV3;                 // RA01: the same swapchain seen as IDXGISwapChain3, which has GetCurrentBackBufferIndex
    static int width, height, format;     // RA01: size and DXGI format of its back buffers
    static Copier? grabber, shower;
    static int monitorMirror = -1;        // RA01: M13-16 - the newest mirror holding the monitor eye's picture (-1 = none yet)
    static int sizeVersion;               // RA01: M5a-5 - goes up each time the game's back buffers get a new size and the mirrors are made again (XrSession follows it)
    static uint pendingWidth, pendingHeight;   // RA01: M5a-5 - a new size seen at Present, and on how many grabs in a row (a window being dragged gives many sizes; only one that holds still is followed)
    static int pendingCount;
    const int ResizeAfter = 3; // RA01 - Resizes the frame
    static readonly List<(nint[] Textures, ulong Grabbed, ulong Shown)> retired = new(); // RA01: M5a-5 - old mirrors, with the last copy that could use them; released when the GPU has finished those
    static readonly object grabLock = new();
    static long nextGrab, grabbed, skippedBusy, shown, skippedShow;

    // RA01: M5a-3 - the F10 window's upload: it is painted by the CPU into one of a few upload buffers, and a GPU copy carries the buffer into a headset swapchain image.
    const int MenuBuffers = 3;            // RA01: the GPU may still be reading the last copy or two when the next window picture is painted
    static readonly nint[] menuBuffers = new nint[MenuBuffers], menuPixels = new nint[MenuBuffers];
    static readonly ulong[] menuUsed = new ulong[MenuBuffers];  // RA01: the copy (fence number) that last read each buffer
    static int menuLast, menuCurrent;     // RA01: the buffer last copied from, and the one handed out for painting now
    static int menuWidth, menuHeight, menuFormat;
    static bool menuReady;

    public static nint GameWindow;        // RA01: the game's window (from its swapchain), where the mouse is taken over for the F10 window
    // RA01: true when the game's picture has blue first byte order (BGRA) instead of red first (RGBA), so the headset images match.
    // RA01: Cost - CPU: extremely low (two integer compares, read now and then); GPU: none (bookkeeping only).
    public static bool Bgra => format == FormatB8G8R8A8 || format == FormatB8G8R8A8Srgb; // RA01: the byte order of the game's picture, and so of the headset images
    // RA01: true when the frame copy runs and the F10 window's upload buffers exist.
    // RA01: Cost - CPU: extremely low (one volatile read and a flag, per headset frame); GPU: none (bookkeeping only).
    public static bool MenuReady => Volatile.Read(ref state) == 1 && menuReady;
    // RA01: true when the F10 window can never be painted again (see the comment on the same line).
    // RA01: Cost - CPU: extremely low (one volatile read and a flag, per headset frame); GPU: none (bookkeeping only).
    public static bool MenuLost => Volatile.Read(ref state) == 2 || !menuReady; // RA01: the window can never be painted again: the frame copy is off for good, or a copy into the headset failed

    // RA01: true when the frame copy runs and at least one mirror already holds a game picture.
    // RA01: Cost - CPU: extremely low (two volatile reads, a few times per headset frame); GPU: none (bookkeeping only).
    public static bool HasFrame => Volatile.Read(ref state) == 1 && Volatile.Read(ref hasFrame) == 1;

    // RA01: M10-1 - the game's back buffer size (0 until the first frame), read without the grab lock by HudLayer to recognise the screen-sized HUD layer.
    // RA01: Cost - CPU: extremely low (one field read); GPU: none.
    public static int Width => width;
    public static int Height => height;

    // RA01: first frame only - remember which swapchain is the game's and what its back buffers look like.
    // Cost - CPU: extremely low (two COM calls and a log line, runs once on the first frame); GPU: none (only reads the swapchain description).
    public static void SetTarget(nint swapChain)
    {
        SwapChainDesc desc;
        int result = ((delegate* unmanaged<nint, SwapChainDesc*, int>)Slot(swapChain, SlotSwapChainGetDesc))(swapChain, &desc);
        if (result < 0)
        {
            Log.Write($"RA01 back buffer: GetDesc failed, HRESULT 0x{result:X8}");
            return;
        }

        // RA01: ask for the IDXGISwapChain3 view of the swapchain instead of assuming the pointer we were handed already is one.
        // RA01: the reference this gives us is kept for the whole game run, like the device reference.
        Guid iidSwapChain3 = new(IidSwapChain3);
        nint swapChain3;
        result = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(swapChain, SlotQueryInterface))(swapChain, &iidSwapChain3, &swapChain3);
        if (result < 0)
        {
            Log.Write($"RA01 back buffer: the swapchain has no IDXGISwapChain3 interface, HRESULT 0x{result:X8}");
            return;
        }
        targetV3 = swapChain3;
        target = swapChain;
        width = (int)desc.Width;
        height = (int)desc.Height;
        format = (int)desc.Format;
        GameWindow = desc.OutputWindow;
        Log.Write($"RA01 back buffer: {width}x{height} {FormatName(format)} ({format}), {desc.BufferCount} buffers, swap effect {desc.SwapEffect}, windowed {desc.Windowed}");
    }

    // RA01: called on the OpenXR thread once the session exists. Builds the mirror textures and the two copy helpers.
    // false means the game's picture cannot be shown yet, the reason is in the log and the mod stays in its M2 behaviour.
    // Cost - CPU: low (creates 6 textures and 2 copiers of 16 command lists each, a few milliseconds, runs once at session start);
    // GPU: extremely low (allocates video memory for the mirrors, no drawing, runs once).
    public static bool Prepare(nint gameDevice, nint gameQueue, out int pictureWidth, out int pictureHeight)
    {
        pictureWidth = pictureHeight = 0;
        if (target == 0) return Fail("no back buffer information from the first frame");
        if (format != FormatR8G8B8A8 && format != FormatR8G8B8A8Srgb && format != FormatB8G8R8A8 && format != FormatB8G8R8A8Srgb)
            return Fail($"back buffer format {FormatName(format)} is not supported yet (only 8 bit per channel), turn HDR off in the game");
        try
        {
            device = gameDevice;
            queue = gameQueue;
            for (int i = 0; i < Mirrors; i++) mirrors[i] = CreateMirror();
            grabber = new Copier(device);
            shower = new Copier(device);
        }
        catch (Exception e)
        {
            return Fail($"setup failed: {e.Message}");
        }
        pictureWidth = width;
        pictureHeight = height;
        return true;
    }

    // RA01: the headset image format to request: the sRGB twin of the game's format if the runtime lists it (the compositor then
    // decodes the game's gamma-encoded picture correctly), otherwise the game's own format. 0 means nothing suitable.
    // Cost - CPU: extremely low (loops over the few formats the runtime offers, runs once at session start); GPU: none.
    public static long PickHeadsetFormat(long* offered, uint count)
    {
        long preferred = format == FormatR8G8B8A8 ? FormatR8G8B8A8Srgb : format == FormatB8G8R8A8 ? FormatB8G8R8A8Srgb : format;
        for (uint i = 0; i < count; i++)
            if (offered[i] == preferred) return preferred;
        for (uint i = 0; i < count; i++)
            if (offered[i] == format) return format;
        return 0;
    }

    // RA01: from now on the Present detour copies frames into the mirrors.
    // Cost - CPU: extremely low (one atomic compare and swap, runs once); GPU: none.
    public static void Enable() => Interlocked.CompareExchange(ref state, 1, 0);

    // RA01: switches the frame copy off for good and logs the reason once.
    // Cost - CPU: extremely low (one atomic exchange, and a log line only the first time; runs at most once per game run); GPU: none.
    public static void Disable(string reason)
    {
        if (Interlocked.Exchange(ref state, 2) != 2)
            Log.Write($"RA01 frame copy switched off: {reason}");
    }

    // RA01: switches the frame copy off with a reason and returns false, so a failed step can be written as one line.
    // Cost - CPU: extremely low (calls Disable, runs at most once per game run); GPU: none.
    static bool Fail(string reason)
    {
        Disable(reason);
        return false;
    }

    // RA01: runs on the game's thread inside every Present, before the real Present. Must be quick and must never throw into the game.
    // Cost - CPU: low (every Present, 100-400 a second, but all but up to 240 a second return at once; a real grab is a lock, two COM calls and one command list submit, a few tens of microseconds);
    // GPU: medium (one whole back buffer texture copy per grab, up to 240 a second, megabytes each, so it scales with resolution). 
    // //RA01 - Note: May be able to optimize later, after M6 is done, we'll see. (potential optimization cadidate function)
    public static void OnPresent(nint swapChain)
    {
        if (Volatile.Read(ref state) != 1) return;
        if (swapChain != target)
        {
            if (Interlocked.Exchange(ref otherSwapChainLogged, 1) == 0)
                Log.Write($"RA01 frame copy: Present also seen on another swapchain 0x{swapChain:X} (ignored, copying from 0x{target:X})");
            return;
        }

        long now = Stopwatch.GetTimestamp();
        long exact = RenderCameraProbe.PresentStamp;                    // RA01: M26-1 - the exact pose of this picture, read off the render side (0 = not known)
        HudLayer.NoteFrame(exact != 0 ? exact : CameraHook.StampOfPresent(Tuning.FrameLag, now)); // RA01: M23-0 - every Present (not only grabbed ones): the HUD sheet of this frame learns its eye
        if (now < Volatile.Read(ref nextGrab)) return;
        if (!Monitor.TryEnter(grabLock)) return; // RA01: another thread is already copying, that is enough
        try
        {
            Volatile.Write(ref nextGrab, now + Stopwatch.Frequency / MaxGrabsPerSecond);
            long stamp = exact != 0 ? exact : CameraHook.StampOfPresent(Tuning.FrameLag, now); // RA01: M26-1 - exact when known, else counted; taken first: a camera the game builds while the copy below runs belongs to the next picture

            // RA01: a copy into a mirror of another size would be an error, so the size is checked first. M5a-5 - a new size (the game's resolution was changed) is followed: the mirrors are made again
            // RA01: at the new size, once the size has held still for a few grabs. A new format (HDR, 10 bit) switches the copy off as before.
            SwapChainDesc desc;
            Check(((delegate* unmanaged<nint, SwapChainDesc*, int>)Slot(swapChain, SlotSwapChainGetDesc))(swapChain, &desc), "GetDesc");
            if (desc.Format != format)
            {
                Disable($"the game's back buffer changed to {desc.Width}x{desc.Height} {FormatName(desc.Format)}");
                return;
            }
            if (retired.Count > 0) ReleaseRetired();
            if (desc.Width != width || desc.Height != height)
            {
                if (desc.Width == 0 || desc.Height == 0) return; // RA01: a minimised window has no size
                if (desc.Width != pendingWidth || desc.Height != pendingHeight) { pendingWidth = desc.Width; pendingHeight = desc.Height; pendingCount = 0; }
                if (++pendingCount >= ResizeAfter) Resize((int)desc.Width, (int)desc.Height);
                return;
            }
            pendingCount = 0;

            uint index = ((delegate* unmanaged<nint, uint>)Slot(targetV3, SlotGetCurrentBackBufferIndex))(targetV3);
            Guid iidResource = new(IidResource);
            nint buffer;
            Check(((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Slot(swapChain, SlotGetBuffer))(swapChain, index, &iidResource, &buffer), "GetBuffer");
            bool copied;
            try
            {
                // RA01: a back buffer waits in the PRESENT state when the game calls Present; the mirror rests in COPY_SOURCE.
                copied = grabber!.Copy(buffer, StatePresent, StatePresent, mirrors[nextMirror], StateCopySource, StateCopySource);
                if (copied) CoverOtherEye(buffer, stamp); // RA01: M13-16 - after the grab, so the headset's mirror holds the game's own picture
            }
            finally
            {
                Release(buffer); // RA01: GetBuffer gave us a reference
            }

            if (copied)
            {
                Interlocked.Increment(ref grabbed);
                mirrorStamp[nextMirror] = stamp; // RA01: the pose (and so the eye) the game had when it built this picture
                mirrorTime[nextMirror] = now;
                nextMirror = (nextMirror + 1) % Mirrors;
                Volatile.Write(ref hasFrame, 1);
            }
            else
            {
                Interlocked.Increment(ref skippedBusy);
            }
        }
        catch (Exception e)
        {
            Disable($"capture failed: {e.Message}");
        }
        finally
        {
            Monitor.Exit(grabLock);
        }
    }

    // RA01: M13-16 - the monitor eye: this picture was just grabbed into mirrors[nextMirror]. If it is the picked eye's, that mirror becomes the one the monitor
    // shows; if it is the other eye's, the picked eye's newest mirror is copied over the back buffer before the game shows it. Off in 2D (one picture for
    // both eyes), with "Both eyes", or when the picture's eye isn't known (not head-steered). The picked eye follows "Swap eyes" exactly as the headset does.
    // Cost - CPU: low (a pose lookup; for the other eye's pictures one more copy submit, tens of microseconds); GPU: low (one whole-frame copy in video memory
    // for half the frames - about 1 GB/s at 2496x2688 and 90 pictures a second, a fraction of a percent of a modern card's memory speed).
    static void CoverOtherEye(nint buffer, long stamp)
    {
        int pick = Tuning.DesktopEye;
        if (pick == 0 || !Tuning.Stereo || !HeadPose.PoseOf(stamp, out _, out int eye) || eye == 0) return;
        int wanted = Tuning.SwapEyes ? 3 - pick : pick;
        if (eye == wanted) { monitorMirror = nextMirror; return; }
        if (monitorMirror < 0 || monitorMirror == nextMirror) return;
        grabber!.Copy(mirrors[monitorMirror], StateCopySource, StateCopySource, buffer, StatePresent, StatePresent);
    }

    // RA01: M5a-5 - the game's back buffers have a new size: new mirrors of that size replace the old ones (which are released once the GPU is done with them), nothing is grabbed until the next Present, and
    // SizeVersion goes up so the headset thread makes its images that size too. Runs on the game's thread, inside the grab lock.
    // Cost - CPU: medium (only when the game's resolution changes and holds still; makes 6 new textures on the game thread, a few milliseconds, rare);
    // GPU: extremely low (allocates 6 textures in video memory, no drawing, rare).
    static void Resize(int newWidth, int newHeight)
    {
        retired.Add(((nint[])mirrors.Clone(), grabber!.Submitted, shower!.Submitted));
        width = newWidth;
        height = newHeight;
        for (int i = 0; i < Mirrors; i++) mirrors[i] = CreateMirror();
        Array.Clear(mirrorTime);
        monitorMirror = -1; // RA01: M13-16 - the old mirrors are another size
        Array.Clear(mirrorStamp);
        nextMirror = 0;
        Volatile.Write(ref hasFrame, 0);
        pendingCount = 0;
        Interlocked.Increment(ref sizeVersion);
        Log.Write($"RA01 frame copy: the game's back buffer is now {width}x{height}, new mirrors made");
    }

    // RA01: M5a-5 - releases the old mirrors that no copy on the GPU can still be using (the copiers' fences have passed the last copy made before they were replaced).
    // Cost - CPU: extremely low (called per grab only while old mirrors wait, a few fence reads); GPU: none (frees video memory only after the GPU is done with it).
    static void ReleaseRetired()
    {
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            var old = retired[i];
            if (!grabber!.IsDone(old.Grabbed) || !shower!.IsDone(old.Shown)) continue;
            foreach (nint texture in old.Textures) Release(texture);
            retired.RemoveAt(i);
        }
    }

    // RA01: M5a-5 - OpenXR thread: the size of the mirrors and its version number, read together. The headset images must have the same size before a picture can be copied into them.
    // Cost - CPU: extremely low (a lock and three reads, per headset frame about 90 a second); GPU: none.
    public static int Size(out int pictureWidth, out int pictureHeight)
    {
        lock (grabLock)
        {
            pictureWidth = width;
            pictureHeight = height;
            return sizeVersion;
        }
    }

    // RA01: the number that goes up each time the mirrors are made again at a new size.
    // RA01: Cost - CPU: extremely low (one volatile read, per headset frame); GPU: none.
    public static int SizeVersion => Volatile.Read(ref sizeVersion);

    // RA01: runs on the OpenXR thread: writes into 'times' when each mirror was grabbed (0 = empty) and into 'stamps' which pose its picture was built from (0 = none),
    // so the caller can choose which picture it wants. Taken under the same lock as the grabs, so a pair is never half-written.
    // Cost - CPU: extremely low (a lock and a copy of 6 pairs of numbers, per headset frame about 90 a second); GPU: none.
    public static void Snapshot(Span<long> times, Span<long> stamps)
    {
        lock (grabLock)
            for (int i = 0; i < Mirrors && i < times.Length && i < stamps.Length; i++)
            {
                times[i] = mirrorTime[i];
                stamps[i] = mirrorStamp[i];
            }
    }

    // RA01: runs on the OpenXR thread: copy the picture that was grabbed at 'pictureTime' (one of the times from Snapshot) into a headset swapchain image
    // (which rests in RENDER_TARGET, and is imageWidth x imageHeight). False = that picture is gone (a newer grab took its mirror) or the GPU is busy, nothing was copied.
    // The GPU runs the copies in the order they were submitted, and the lock keeps a grab from being submitted between choosing the mirror and submitting this copy.
    // Cost - CPU: low (a lock, an array search and one command list submit, a few tens of microseconds, once per eye per headset frame, about 180 a second);
    // GPU: medium (one whole game picture texture copy per call, megabytes each, so it scales with resolution).
    public static bool Show(nint image, long pictureTime, int imageWidth, int imageHeight)
    {
        if (pictureTime == 0) return false;
        lock (grabLock)
        {
            try
            {
                if (!HasFrame) return false; // RA01: checked inside the lock, so a switch-off by the game thread while this waited is seen
                if (imageWidth != width || imageHeight != height) return false; // RA01: M5a-5 - the image is of an older size than the mirrors (the game was resized): the headset thread makes new images first
                int index = Array.IndexOf(mirrorTime, pictureTime);
                if (index < 0) return false;
                if (!shower!.Copy(mirrors[index], StateCopySource, StateCopySource, image, StateRenderTarget, StateRenderTarget))
                {
                    Interlocked.Increment(ref skippedShow);
                    return false;
                }
                Interlocked.Increment(ref shown);
                return true;
            }
            catch (Exception e)
            {
                Disable($"copy into the headset image failed: {e.Message}");
                return false;
            }
        }
    }

    // RA01: M16-A - the scope lens: copies a side x side square at (x, y) of the picture grabbed at 'pictureTime' into the top left of a lens image (resting in
    // RENDER_TARGET); with 'round' the square's corners are then blanked to see-through, leaving a disc. False = that picture is gone or the GPU is busy
    // (the lens image keeps nothing new - XrSession then hides the lens until the next picture). A failure here never switches the frame copy off.
    // Cost - CPU: low (a lock, and one command list of up to a few hundred small copies, per new eye picture while scoped); GPU: extremely low (a
    // square of at most 256 x 256 pixels and the blank corners).
    public static bool ShowLens(nint image, long pictureTime, int x, int y, int side, bool round)
    {
        if (pictureTime == 0) return false;
        lock (grabLock)
        {
            try
            {
                if (!HasFrame || x < 0 || y < 0 || side <= 0 || side > ScopeLens.MaxSide || x + side > width || y + side > height) return false;
                int index = Array.IndexOf(mirrorTime, pictureTime);
                if (index < 0) return false;
                if (round && lensBlank == 0) lensBlank = CreateTexture(ScopeLens.MaxSide, ScopeLens.MaxSide, "scope lens blank");
                return shower!.CopyLens(mirrors[index], image, round ? lensBlank : 0, x, y, side);
            }
            catch (Exception e)
            {
                Log.Write($"RA01 scope lens: the copy failed ({e.Message})");
                return false;
            }
        }
    }
    static nint lensBlank; // RA01: M16-A - an all-zero (fully see-through) square in the picture's format, resting in COPY_SOURCE; never released (the GPU may still read it)

    // RA01: M5a-3 - makes the upload buffers for a window picture of this size, in the given headset image format. Called once, on the OpenXR thread. False = no window can be shown (the reason is in the log); the game picture is not affected.
    // Cost - CPU: extremely low (creates and maps 3 small buffers, well under a millisecond, runs once); GPU: extremely low (allocates 3 small upload buffers, no drawing, runs once).
    public static bool PrepareMenu(int panelWidth, int panelHeight, long headsetFormat)
    {
        try
        {
            if (Volatile.Read(ref state) != 1) return false;
            for (int i = 0; i < MenuBuffers; i++) menuBuffers[i] = CreateUploadBuffer(panelWidth * 4 * panelHeight, out menuPixels[i]);
            menuWidth = panelWidth;
            menuHeight = panelHeight;
            menuFormat = (int)headsetFormat;
            menuReady = true;
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"RA01 menu: the upload buffers could not be made ({e.Message}), the F10 window is not available");
            return false;
        }
    }

    // RA01: OpenXR thread: the memory to paint the next window picture into (panel width x height pixels of 4 bytes, rows packed), or null when the GPU is still reading every buffer (try again next frame).
    // each call must be followed by EndMenu if the picture was painted.
    // Cost - CPU: extremely low (up to 3 fence reads, per headset frame while the window is painted); GPU: none (only hands out CPU memory).
    public static byte* BeginMenu()
    {
        if (!MenuReady) return null;
        for (int k = 1; k <= MenuBuffers; k++)
        {
            int i = (menuLast + k) % MenuBuffers;
            if (shower!.IsDone(menuUsed[i])) { menuCurrent = i; return (byte*)menuPixels[i]; }
        }
        return null;
    }

    // RA01: OpenXR thread: copies the painted buffer into a headset swapchain image (which rests in RENDER_TARGET). False = the GPU was busy, nothing was copied, paint again next frame.
    // Cost - CPU: low (records and submits one command list, tens of microseconds, at most about 90 a second);
    // GPU: low (one buffer to texture copy of the small window picture, far less than a game frame, at most about 90 a second).
    public static bool EndMenu(nint image)
    {
        try
        {
            if (!MenuReady || !shower!.CopyBufferToTexture(menuBuffers[menuCurrent], image, StateRenderTarget, StateRenderTarget, menuFormat, menuWidth, menuHeight, menuWidth * 4)) return false;
            menuUsed[menuCurrent] = shower.Submitted;
            menuLast = menuCurrent;
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"RA01 menu: the copy into the headset image failed ({e.Message}), the F10 window is switched off");
            menuReady = false;
            return false;
        }
    }

    // RA01: M29-1 - THE SETTINGS WINDOW ON THE MONITOR (Rob 2026-10-09: so it shows in recordings, and F10 works without a headset). The painting thread (the
    // RA01: headset's, or FlatMenu's without one) puts the newest window picture into one of two upload buffers (BeginMonitorMenu / EndMonitorMenu, with the
    // RA01: pieces to show: the window, and the tooltip and cursor when they stick out of it); every Present, StampMenu copies those pieces onto the game's back
    // RA01: buffer, the window centred - AFTER the headset's grab, so the headset never sees it (there the window is its own floating panel). A plain copy, not
    // RA01: a blend: a stamp pressed onto the picture. 8-bit pictures only (like the headset copy). Its own copier, device and queue (SetGame), so it works with
    // RA01: or without the headset. Cost - CPU: low (only while the window is open: a GetDesc, a GetBuffer and one small submit per Present); GPU: extremely low
    // RA01: (a window-sized copy per Present, under a millisecond; nothing while closed).
    const int StampBuffers = 2;
    static readonly nint[] stampBuffers = new nint[StampBuffers], stampPixels = new nint[StampBuffers];
    static readonly ulong[] stampUsed = new ulong[StampBuffers];  // RA01: the stamp copy (fence number) that last read each buffer
    static readonly int[] stampBoxes = new int[MaxStampPieces * 4]; // RA01: the shown pieces, left, top, right, bottom on the panel; the first is the window
    const int MaxStampPieces = 3;
    static readonly object stampLock = new();
    static nint stampDevice, stampQueue;
    static Copier? stamper;
    static int stampShown = -1, stampWriting = -1, stampPieces;
    static bool stampFailed, stampFormatLogged;

    // RA01: M29-1 - the game's device and main queue (PresentHook, once the main queue holds steady): the monitor stamp can start, headset or not.
    // Cost - CPU: extremely low (two stores, once); GPU: none.
    public static void SetGame(nint gameDevice, nint gameQueue) { stampDevice = gameDevice; stampQueue = gameQueue; }

    // RA01: M29-1 - can the window be stamped on the monitor at all (the game's device is known, its picture is 8-bit, nothing failed)? Logs once when the format rules it out.
    // RA01: Cost - CPU: extremely low (a few compares); GPU: none.
    public static bool CanStamp
    {
        get
        {
            if (stampFailed || stampDevice == 0 || target == 0) return false;
            if (format == FormatR8G8B8A8 || format == FormatR8G8B8A8Srgb || format == FormatB8G8R8A8 || format == FormatB8G8R8A8Srgb) return true;
            if (!stampFormatLogged) { stampFormatLogged = true; Log.Write($"RA01 monitor menu: the game's picture is {FormatName(format)} (not 8-bit) - the settings window can't be shown on the monitor (turn HDR off)"); }
            return false;
        }
    }

    // RA01: M29-1 - painting thread: the memory to put the window picture in (PanelWidth x PanelHeight pixels of 4 bytes, rows packed), or null when not possible
    // or both buffers are still being read (try again next frame). Follow with EndMonitorMenu. The buffers and copier are made the first time.
    // Cost - CPU: extremely low (a lock and two fence reads; the first time, two 4.9 MB upload buffers and a copier, a few milliseconds); GPU: none.
    public static byte* BeginMonitorMenu()
    {
        if (!CanStamp) return null;
        lock (stampLock)
        {
            try
            {
                if (stamper == null)
                {
                    for (int i = 0; i < StampBuffers; i++) stampBuffers[i] = CreateUploadBuffer(SettingsMenu.PanelWidth * 4 * SettingsMenu.PanelHeight, out stampPixels[i], stampDevice);
                    stamper = new Copier(stampDevice, stampQueue);
                    Log.Write("RA01 monitor menu: ready - the settings window is shown on the monitor while it is open");
                }
                for (int i = 0; i < StampBuffers; i++)
                    if (i != stampShown && stamper.IsDone(stampUsed[i])) { stampWriting = i; return (byte*)stampPixels[i]; }
                return null;
            }
            catch (Exception e) { stampFailed = true; Log.Write($"RA01 monitor menu: could not be set up ({e.Message}) - the settings window stays headset-only"); return null; }
        }
    }

    // RA01: M29-1 - painting thread: the picture just put in is the one to show, with these pieces ('boxes': left, top, right, bottom on the panel, the window first).
    // Cost - CPU: extremely low (a lock and a few stores); GPU: none.
    public static void EndMonitorMenu(ReadOnlySpan<int> boxes, int pieces)
    {
        lock (stampLock)
        {
            if (stampWriting < 0) return;
            pieces = Math.Min(pieces, MaxStampPieces);
            for (int k = 0; k < pieces * 4; k++) stampBoxes[k] = boxes[k];
            stampPieces = pieces;
            stampShown = stampWriting;
            stampWriting = -1;
        }
    }

    // RA01: M29-1 - every Present (PresentHook, right after OnPresent's grab): while the window is open and 'show' says so, its pieces are copied onto the back buffer,
    // the window centred, everything clipped to the picture. Never waits: a busy lock or copier just skips this Present. Never throws into the game.
    // Cost - CPU: low (only while open: GetDesc, GetBuffer, one submit, tens of microseconds); GPU: extremely low (a window-sized copy).
    public static void StampMenu(nint swapChain, bool show)
    {
        if (!show || stamper == null || swapChain != target || stampShown < 0) return;
        if (!Monitor.TryEnter(stampLock)) return;
        try
        {
            if (stampShown < 0 || stampPieces == 0) return;
            SwapChainDesc desc;
            Check(((delegate* unmanaged<nint, SwapChainDesc*, int>)Slot(swapChain, SlotSwapChainGetDesc))(swapChain, &desc), "GetDesc");
            if (desc.Format != format || desc.Width == 0 || desc.Height == 0) return;
            int w = (int)desc.Width, h = (int)desc.Height;
            int ox = (w - (stampBoxes[2] - stampBoxes[0])) / 2 - stampBoxes[0], oy = (h - (stampBoxes[3] - stampBoxes[1])) / 2 - stampBoxes[1]; // RA01: the window centred
            int* boxes = stackalloc int[MaxStampPieces * 6];
            int count = 0;
            for (int k = 0; k < stampPieces; k++)
            {
                int l = stampBoxes[k * 4], t = stampBoxes[k * 4 + 1], r = stampBoxes[k * 4 + 2], b = stampBoxes[k * 4 + 3];
                l = Math.Max(l, Math.Max(0, -ox)); t = Math.Max(t, Math.Max(0, -oy));                               // RA01: clipped to the panel and to the picture
                r = Math.Min(r, Math.Min(SettingsMenu.PanelWidth, w - ox)); b = Math.Min(b, Math.Min(SettingsMenu.PanelHeight, h - oy));
                if (r <= l || b <= t) continue;
                int* p = boxes + count++ * 6;
                p[0] = l; p[1] = t; p[2] = r; p[3] = b; p[4] = l + ox; p[5] = t + oy;
            }
            if (count == 0) return;
            uint index = ((delegate* unmanaged<nint, uint>)Slot(targetV3, SlotGetCurrentBackBufferIndex))(targetV3);
            Guid iidResource = new(IidResource);
            nint buffer;
            Check(((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Slot(swapChain, SlotGetBuffer))(swapChain, index, &iidResource, &buffer), "GetBuffer");
            try
            {
                if (stamper.CopyRegions(stampBuffers[stampShown], format, SettingsMenu.PanelWidth, SettingsMenu.PanelHeight, SettingsMenu.PanelWidth * 4, buffer, boxes, count))
                    stampUsed[stampShown] = stamper.Submitted;
            }
            finally { Release(buffer); }
        }
        catch (Exception e) { stampFailed = true; stamper = null; Log.Write($"RA01 monitor menu: the stamp failed ({e.Message}) - switched off for this session"); }
        finally { Monitor.Exit(stampLock); }
    }

    // RA01: M24-4 - SMALL PAINTED IMAGES (HandStrip's laser beam and dot): one upload buffer of SmallBytes, made the first time it is asked for. OpenXR thread:
    // the memory to paint into, or null while the GPU still reads the last copy (or the frame copy is off) - try again next frame; then EndSmall.
    // Cost - CPU: extremely low (a fence read; once, one small buffer creation); GPU: none (only hands out CPU memory).
    public static byte* BeginSmall()
    {
        try
        {
            if (Volatile.Read(ref state) != 1 || shower == null || smallFailed) return null;
            if (smallBuffer == 0) smallBuffer = CreateUploadBuffer(SmallBytes, out smallPixels);
            return shower.IsDone(smallUsed) ? (byte*)smallPixels : null;
        }
        catch (Exception e) { smallFailed = true; Log.Write($"RA01 small image: no upload buffer ({e.Message}) - no laser this session"); return null; }
    }

    // RA01: M24-4 - copies the painted small image (width x height, rows packed, at most SmallBytes) into a headset swapchain image resting in RENDER_TARGET.
    // False = nothing copied (paint again next frame). Cost - CPU: low (records and submits one small command list); GPU: extremely low (a 32 KB copy).
    public static bool EndSmall(nint image, long headsetFormat, int width, int height)
    {
        try
        {
            if (smallBuffer == 0 || width * height * 4 > SmallBytes || !shower!.CopyBufferToTexture(smallBuffer, image, StateRenderTarget, StateRenderTarget, (int)headsetFormat, width, height, width * 4)) return false;
            smallUsed = shower.Submitted;
            return true;
        }
        catch (Exception e) { Log.Write($"RA01 small image: the copy failed ({e.Message})"); return false; }
    }
    const int SmallBytes = 1024 * 192 * 4;     // RA01: M27-1 - room for the banner (Banner.Width x Height); the laser's image is far smaller
    static nint smallBuffer, smallPixels;
    static ulong smallUsed;
    static bool smallFailed;

    // RA01: M25-1 - THE EYE CHECK'S STRIPS (Core\Video\EyeCheck.cs): a few thin bands of rows across two grabbed pictures (a left and a right eye's) copied on the GPU into
    // one read-back buffer the CPU can read. Picture A's bands come first, then picture B's, each band 'rows' rows of the picture's full width, every row
    // StripPitch bytes, every band starting at a multiple of StripBandBytes. Returns the copy's number (wait for StripsDone), or 0 when nothing was copied
    // (a picture is gone, the GPU is busy, the frame copy is off). The buffer is made (again) for the picture's width the first time it is needed.
    // Cost - CPU: low (a lock and one small command list submit, twice a second at most); GPU: extremely low (a few rows of two pictures, about 0.1 MB).
    public static ulong CopyStrips(long timeA, long timeB, ReadOnlySpan<int> bandTops, int rows)
    {
        lock (grabLock)
        {
            try
            {
                if (!HasFrame || stripFailed || bandTops.Length > MaxStripBands) return 0;
                int a = Array.IndexOf(mirrorTime, timeA), b = Array.IndexOf(mirrorTime, timeB);
                if (a < 0 || b < 0) return 0;
                foreach (int top in bandTops) if (top < 0 || top + rows > height) return 0;
                int pitch = (width * 4 + 255) & ~255, bandBytes = (pitch * rows + 511) & ~511; // RA01: D3D12's row (256) and placement (512) alignments
                if (stripBuffer == 0 || stripWidth != width || stripBandBytes != bandBytes)
                {
                    if (stripBuffer != 0 && !shower!.IsDone(stripUsed)) return 0;              // RA01: the old one may still be written: wait
                    if (stripBuffer != 0) Release(stripBuffer);
                    stripBuffer = CreateReadbackBuffer(bandBytes * MaxStripBands * 2, out stripPixels);
                    stripWidth = width; stripPitch = pitch; stripBandBytes = bandBytes;
                }
                if (!shower!.CopyBands(mirrors[a], mirrors[b], stripBuffer, bandTops, rows, width, pitch, bandBytes, format, MaxStripBands)) return 0;
                stripUsed = shower.Submitted;
                return stripUsed;
            }
            catch (Exception e) { stripFailed = true; Log.Write($"RA01 eye check: the strip copy failed ({e.Message}) - eye check off this session"); return 0; }
        }
    }

    // RA01: M25-1 - has the strip copy with this number finished (its bands can be read)? Cost - CPU: extremely low (a fence read); GPU: none.
    public static bool StripsDone(ulong copy) => shower != null && shower.IsDone(copy);

    // RA01: M25-1 - the read-back memory and its layout (see CopyStrips): picture A's band k at StripPixels + k * StripBandBytes, picture B's at
    // RA01: + (MaxStripBands + k) * StripBandBytes, each row StripPitch bytes, StripWidth pixels of 4 bytes. Cost - CPU: extremely low (reads); GPU: none.
    public static byte* StripPixels => (byte*)stripPixels;
    public static int StripPitch => stripPitch;
    public static int StripBandBytes => stripBandBytes;
    public static int StripWidth => stripWidth;
    public const int MaxStripBands = 4;
    static nint stripBuffer, stripPixels;
    static int stripWidth, stripPitch, stripBandBytes;
    static ulong stripUsed;
    static bool stripFailed;

    // RA01: M25-1 - a buffer the GPU writes and the CPU reads, mapped once and kept mapped (allowed for read-back memory). It rests in COPY_DEST for good.
    // Cost - CPU: extremely low (one resource creation and one Map, once, or again after a resolution change); GPU: extremely low (a small buffer).
    static nint CreateReadbackBuffer(int bytes, out nint pixels)
    {
        HeapProperties heap = new() { Type = HeapTypeReadback, CreationNodeMask = 1, VisibleNodeMask = 1 };
        ResourceDesc desc = new()
        {
            Dimension = DimensionBuffer, Width = (ulong)bytes, Height = 1, DepthOrArraySize = 1, MipLevels = 1,
            Format = 0, SampleCount = 1, Layout = LayoutRowMajor, Flags = ResourceFlagNone
        };
        Guid iidResource = new(IidResource);
        nint resource, mapped;
        Check(((delegate* unmanaged<nint, HeapProperties*, int, ResourceDesc*, int, nint, Guid*, nint*, int>)Slot(device, SlotCreateCommittedResource))
            (device, &heap, HeapFlagNone, &desc, StateCopyDest, 0, &iidResource, &resource), "CreateCommittedResource (read-back buffer)");
        Check(((delegate* unmanaged<nint, uint, nint, nint*, int>)Slot(resource, SlotMap))(resource, 0, 0, &mapped), "Map (read-back)");
        pixels = mapped;
        return resource;
    }

    // RA01: a buffer the CPU writes and the GPU reads, mapped once and kept mapped. Like the mirrors it is never released (the GPU may still be reading it).
    // Cost - CPU: extremely low (one resource creation and one Map, runs 3 times at start-up); GPU: extremely low (allocates a small upload buffer, runs 3 times at start-up).
    static nint CreateUploadBuffer(int bytes, out nint pixels, nint onDevice = 0) // RA01: M29-1 - 'onDevice': the monitor stamp's, made before (or without) the headset's
    {
        nint useDevice = onDevice != 0 ? onDevice : device;
        HeapProperties heap = new() { Type = HeapTypeUpload, CreationNodeMask = 1, VisibleNodeMask = 1 };
        ResourceDesc desc = new()
        {
            Dimension = DimensionBuffer, Width = (ulong)bytes, Height = 1, DepthOrArraySize = 1, MipLevels = 1,
            Format = 0, SampleCount = 1, Layout = LayoutRowMajor, Flags = ResourceFlagNone
        };
        Guid iidResource = new(IidResource);
        nint resource, mapped;
        Check(((delegate* unmanaged<nint, HeapProperties*, int, ResourceDesc*, int, nint, Guid*, nint*, int>)Slot(useDevice, SlotCreateCommittedResource))
            (useDevice, &heap, HeapFlagNone, &desc, StateGenericRead, 0, &iidResource, &resource), "CreateCommittedResource (upload buffer)");
        Check(((delegate* unmanaged<nint, uint, nint, nint*, int>)Slot(resource, SlotMap))(resource, 0, 0, &mapped), "Map"); // RA01: no read range given: it is only ever written
        pixels = mapped;
        return resource;
    }

    // RA01: once per second, called from the report in PresentHook.
    // Cost - CPU: extremely low (four atomic exchanges and one log line, once a second); GPU: none.
    public static void LogSecond()
    {
        if (Volatile.Read(ref state) != 1) return;
        Log.Write($"RA01 frame copy: grabbed {Interlocked.Exchange(ref grabbed, 0)} (skipped, GPU busy: {Interlocked.Exchange(ref skippedBusy, 0)}), "
            + $"shown in headset {Interlocked.Exchange(ref shown, 0)} (skipped, GPU busy: {Interlocked.Exchange(ref skippedShow, 0)})");
    }

    // RA01: the mirror is a plain default-heap texture with the same size and format as the back buffer.
    // it is never released: the GPU may still be reading it, and freeing it under the game's queue could crash the game.
    // Cost - CPU: low (one committed texture creation, up to a millisecond, 6 times at start-up and 6 more on a resolution change);
    // GPU: extremely low (allocates one game frame sized texture in video memory, no drawing).
    static nint CreateMirror() => CreateTexture(width, height, "CreateCommittedResource");

    // RA01: M16-A - a plain default-heap texture in the picture's format, resting in COPY_SOURCE; D3D12 hands it over zero-filled (CreateMirror's, and the scope lens blank).
    // Cost - CPU: low (one committed texture creation, up to a millisecond, rare); GPU: extremely low (allocates video memory, no drawing).
    static nint CreateTexture(int textureWidth, int textureHeight, string what)
    {
        HeapProperties heap = new() { Type = HeapTypeDefault, CreationNodeMask = 1, VisibleNodeMask = 1 };
        ResourceDesc desc = new()
        {
            Dimension = DimensionTexture2D, Width = (ulong)textureWidth, Height = (uint)textureHeight, DepthOrArraySize = 1, MipLevels = 1,
            Format = format, SampleCount = 1, Layout = LayoutUnknown, Flags = ResourceFlagNone
        };
        Guid iidResource = new(IidResource);
        nint resource;
        Check(((delegate* unmanaged<nint, HeapProperties*, int, ResourceDesc*, int, nint, Guid*, nint*, int>)Slot(device, SlotCreateCommittedResource))
            (device, &heap, HeapFlagNone, &desc, StateCopySource, 0, &iidResource, &resource), what);
        return resource;
    }

    // RA01: builds the description of one resource state switch (a barrier) for a whole texture.
    // Cost - CPU: extremely low (fills one small struct, up to 4 times per copy); GPU: none (the barrier is only recorded later).
    static ResourceBarrier Transition(nint resource, int before, int after) => new()
    {
        Type = BarrierTransition, Flags = BarrierFlagNone, Resource = resource, Subresource = AllSubresources, StateBefore = before, StateAfter = after
    };

    // RA01: records the given barriers into the command list, or nothing when there are none.
    // Cost - CPU: extremely low (one COM call that records into the list, up to twice per copy); GPU: extremely low (a state switch marker, nearly free).
    static void Barrier(nint list, int count, ResourceBarrier* barriers)
    {
        if (count > 0) ((delegate* unmanaged<nint, uint, ResourceBarrier*, void>)Slot(list, SlotResourceBarrier))(list, (uint)count, barriers);
    }

    // RA01: records and submits "copy this texture into that one" on the game's queue, using a small ring of reusable command lists.
    // a fence tells us when the GPU has finished with a list, so we never touch one that is still in use and never wait.
    // Cost - CPU: low (each copy is a list reset, a few commands and a submit, tens of microseconds, at most about 240 plus 180 a second);
    // GPU: medium (runs the whole texture copies it is given, megabytes each, so it scales with resolution).
    internal sealed class Copier // RA01: M10-1 - internal, so HudLayer can use one for the HUD panel's copy
    {
        const int Slots = 16; // RA01: the game queue can run a few frames behind and the headset copies twice a frame (one per eye), so plenty of lists are kept in flight
        readonly nint[] allocators = new nint[Slots];
        readonly nint[] lists = new nint[Slots];
        readonly ulong[] lastUse = new ulong[Slots];  // RA01: the fence value each list was last submitted with
        readonly nint fence;
        readonly nint submitQueue;                    // RA01: M29-1 - the queue it submits to (0 = FrameCopy's, set when the headset starts)
        ulong submitted;
        int next;

        // RA01: creates the 16 command allocators and command lists and the fence used by this copier.
        // RA01: Cost - CPU: low (about 32 D3D12 object creations, a few milliseconds, once per copier, twice at start-up); GPU: extremely low (creates bookkeeping objects only, no drawing, once).
        public Copier(nint device, nint onQueue = 0)
        {
            submitQueue = onQueue;
            Guid iidAllocator = new(IidCommandAllocator), iidList = new(IidCommandList), iidFence = new(IidFence);
            for (int i = 0; i < Slots; i++)
            {
                nint allocator, list;
                Check(((delegate* unmanaged<nint, int, Guid*, nint*, int>)Slot(device, SlotCreateCommandAllocator))
                    (device, ListTypeDirect, &iidAllocator, &allocator), "CreateCommandAllocator");
                Check(((delegate* unmanaged<nint, uint, int, nint, nint, Guid*, nint*, int>)Slot(device, SlotCreateCommandList))
                    (device, 0, ListTypeDirect, allocator, 0, &iidList, &list), "CreateCommandList");
                Check(((delegate* unmanaged<nint, int>)Slot(list, SlotListClose))(list), "list Close"); // RA01: a new list starts open, we Reset it before every use
                allocators[i] = allocator;
                lists[i] = list;
            }
            nint createdFence;
            Check(((delegate* unmanaged<nint, ulong, int, Guid*, nint*, int>)Slot(device, SlotCreateFence))
                (device, 0, FenceFlagNone, &iidFence, &createdFence), "CreateFence");
            fence = createdFence;
        }

        // RA01: the number of the last copy submitted to the game's queue (the fence value it will reach).
        // RA01: Cost - CPU: extremely low (one field read); GPU: none.
        public ulong Submitted => submitted; // RA01: the number of the last copy submitted

        // RA01: true when the GPU has finished the copy with this number (0 = none, always done).
        // Cost - CPU: extremely low (one COM call that reads the fence, a few times per copy); GPU: none (reads a counter the GPU has already written).
        public bool IsDone(ulong copy) => copy == 0 || ((delegate* unmanaged<nint, ulong>)Slot(fence, SlotGetCompletedValue))(fence) >= copy;

        // RA01: takes the next command list if the GPU has finished with it, and opens it. -1 = every list is still busy on the GPU, nothing was done.
        // RA01: Cost - CPU: low (resets one allocator and one list, a few microseconds to tens, once per copy); GPU: none (only prepares command recording).
        int OpenList(out nint list)
        {
            int i = next;
            list = lists[i];
            if (!IsDone(lastUse[i])) return -1;
            nint allocator = allocators[i];
            Check(((delegate* unmanaged<nint, int>)Slot(allocator, SlotAllocatorReset))(allocator), "allocator Reset");
            Check(((delegate* unmanaged<nint, nint, nint, int>)Slot(list, SlotListReset))(list, allocator, 0), "list Reset");
            return i;
        }

        // RA01: closes the list, runs it on the game's queue, and marks it with the next fence number.
        // RA01: Cost - CPU: low (Close, ExecuteCommandLists and Signal, tens of microseconds, once per copy); GPU: extremely low (one queue submit and one fence signal, the copy itself is counted under Copy).
        void SubmitList(int i, nint list)
        {
            Check(((delegate* unmanaged<nint, int>)Slot(list, SlotListClose))(list), "list Close");
            nint submit = list, q = submitQueue != 0 ? submitQueue : queue;
            ((delegate* unmanaged<nint, uint, nint*, void>)Slot(q, SlotExecuteCommandLists))(q, 1, &submit);
            submitted++;
            Check(((delegate* unmanaged<nint, nint, ulong, int>)Slot(q, SlotSignal))(q, fence, submitted), "queue Signal");

            lastUse[i] = submitted;
            next = (i + 1) % Slots;
        }

        // RA01: the 'before' states say what the textures are in right now, the 'after' states what they must be left in.
        // false = every list is still busy on the GPU, nothing was done.
        // Cost - CPU: low (about 6 small COM calls plus a submit, tens of microseconds, per grab or per eye); GPU: medium (one whole texture copy per call, megabytes each, so it scales with resolution).
        public bool Copy(nint source, int sourceBefore, int sourceAfter, nint dest, int destBefore, int destAfter)
        {
            int i = OpenList(out nint list);
            if (i < 0) return false;

            ResourceBarrier* barriers = stackalloc ResourceBarrier[2];
            int count = 0;
            if (sourceBefore != StateCopySource) barriers[count++] = Transition(source, sourceBefore, StateCopySource);
            if (destBefore != StateCopyDest) barriers[count++] = Transition(dest, destBefore, StateCopyDest);
            Barrier(list, count, barriers);

            ((delegate* unmanaged<nint, nint, nint, void>)Slot(list, SlotCopyResource))(list, dest, source);

            count = 0;
            if (sourceAfter != StateCopySource) barriers[count++] = Transition(source, StateCopySource, sourceAfter);
            if (destAfter != StateCopyDest) barriers[count++] = Transition(dest, StateCopyDest, destAfter);
            Barrier(list, count, barriers);

            SubmitList(i, list);
            return true;
        }

        // RA01: M13-61 - the HUD SPLIT, one list: the whole sheet into the panel image; then the panel's centre square (side x side) overwritten from 'blank' (an
        // all-transparent texture, resting in COPY_SOURCE); then the sheet's centre square into the crosshair image's top left. The panel image and the
        // crosshair image rest in RENDER_TARGET, the sheet too. Like cutting the gunsight out of a printed dashboard and mounting it on the barrel line.
        // Cost - CPU: low (a few COM calls and one submit, once per headset frame); GPU: low (one screen-sized copy and two small square copies).
        public bool CopySplit(nint sheet, nint panel, nint crosshair, nint blank, int sheetWidth, int sheetHeight, int side)
        {
            int i = OpenList(out nint list);
            if (i < 0) return false;
            ResourceBarrier* barriers = stackalloc ResourceBarrier[3];
            barriers[0] = Transition(sheet, StateRenderTarget, StateCopySource);
            barriers[1] = Transition(panel, StateRenderTarget, StateCopyDest);
            barriers[2] = Transition(crosshair, StateRenderTarget, StateCopyDest);
            Barrier(list, 3, barriers);

            ((delegate* unmanaged<nint, nint, nint, void>)Slot(list, SlotCopyResource))(list, panel, sheet);
            uint x = (uint)((sheetWidth - side) / 2), y = (uint)((sheetHeight - side) / 2);
            uint* box = stackalloc uint[6] { 0, 0, 0, (uint)side, (uint)side, 1 };          // RA01: D3D12_BOX: left, top, front, right, bottom, back
            TextureCopyLocation to = new() { Resource = panel, Type = 0, SubresourceIndex = 0 }, from = new() { Resource = blank, Type = 0, SubresourceIndex = 0 };
            ((delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, uint*, void>)Slot(list, SlotCopyTextureRegion))(list, &to, x, y, 0, &from, box);
            box[0] = x; box[1] = y; box[3] = x + (uint)side; box[4] = y + (uint)side;
            to.Resource = crosshair; from.Resource = sheet;
            ((delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, uint*, void>)Slot(list, SlotCopyTextureRegion))(list, &to, 0, 0, 0, &from, box);

            barriers[0] = Transition(sheet, StateCopySource, StateRenderTarget);
            barriers[1] = Transition(panel, StateCopyDest, StateRenderTarget);
            barriers[2] = Transition(crosshair, StateCopyDest, StateRenderTarget);
            Barrier(list, 3, barriers);
            SubmitList(i, list);
            return true;
        }

        // RA01: M16-A - the SCOPE LENS, one list: the picture's side x side square at (x, y) into the lens image's top left; then, with a blank, the corners
        // outside the circle overwritten see-through, row band by row band (rows whose corner is equally wide share one copy) - like masking a round
        // window with tape strips. The picture rests in COPY_SOURCE, the lens image in RENDER_TARGET.
        // Cost - CPU: low (up to about 2 copies per corner width, a few hundred tiny commands at 256 pixels, and one submit); GPU: extremely low (a small square).
        public bool CopyLens(nint picture, nint lens, nint blank, int x, int y, int side)
        {
            int i = OpenList(out nint list);
            if (i < 0) return false;
            ResourceBarrier* barrier = stackalloc ResourceBarrier[1];
            barrier[0] = Transition(lens, StateRenderTarget, StateCopyDest);
            Barrier(list, 1, barrier);

            var copy = (delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, uint*, void>)Slot(list, SlotCopyTextureRegion);
            uint* box = stackalloc uint[6] { (uint)x, (uint)y, 0, (uint)(x + side), (uint)(y + side), 1 }; // RA01: D3D12_BOX: left, top, front, right, bottom, back
            TextureCopyLocation to = new() { Resource = lens, Type = 0, SubresourceIndex = 0 }, from = new() { Resource = picture, Type = 0, SubresourceIndex = 0 };
            copy(list, &to, 0, 0, 0, &from, box);
            if (blank != 0)
            {
                from.Resource = blank;
                float r = side / 2f;
                int top = 0, run = Corner(0, r);
                for (int row = 1; row <= side; row++)
                {
                    int corner = row < side ? Corner(row, r) : -1;
                    if (corner == run) continue;
                    if (run > 0)
                    {
                        box[0] = 0; box[1] = 0; box[3] = (uint)run; box[4] = (uint)(row - top);
                        copy(list, &to, 0, (uint)top, 0, &from, box);
                        copy(list, &to, (uint)(side - run), (uint)top, 0, &from, box);
                    }
                    top = row; run = corner;
                }
            }

            barrier[0] = Transition(lens, StateCopyDest, StateRenderTarget);
            Barrier(list, 1, barrier);
            SubmitList(i, list);
            return true;
        }

        // RA01: M16-A - how wide each corner outside the circle (radius r, centred in the square) is on this pixel row.
        // Cost - CPU: extremely low (a square root); GPU: none.
        static int Corner(int row, float r)
        {
            float dy = row + 0.5f - r;
            return (int)MathF.Round(r - MathF.Sqrt(MathF.Max(0f, r * r - dy * dy)));
        }

        // RA01: M25-1 - the eye check's bands, one list: each band (rows x the full width, from row bandTops[k]) of picture A, then of picture B, into the read-back
        // buffer (resting in COPY_DEST) at its own offset. Both pictures rest in COPY_SOURCE (mirrors), so no barriers are needed.
        // Cost - CPU: low (up to eight small copy commands and one submit); GPU: extremely low (a few rows of two pictures).
        public bool CopyBands(nint pictureA, nint pictureB, nint buffer, ReadOnlySpan<int> bandTops, int rows, int width, int pitch, int bandBytes, int pixelFormat, int bandsPerPicture)
        {
            int i = OpenList(out nint list);
            if (i < 0) return false;
            var copy = (delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, uint*, void>)Slot(list, SlotCopyTextureRegion);
            uint* box = stackalloc uint[6];
            for (int p = 0; p < 2; p++)
                for (int k = 0; k < bandTops.Length; k++)
                {
                    box[0] = 0; box[1] = (uint)bandTops[k]; box[2] = 0; box[3] = (uint)width; box[4] = (uint)(bandTops[k] + rows); box[5] = 1; // RA01: D3D12_BOX
                    TextureCopyLocation from = new() { Resource = p == 0 ? pictureA : pictureB, Type = 0, SubresourceIndex = 0 };
                    TextureCopyLocation to = new() { Resource = buffer, Type = 1, Offset = (ulong)((p * bandsPerPicture + k) * bandBytes), Format = (uint)pixelFormat, Width = (uint)width, Height = (uint)rows, Depth = 1, RowPitch = (uint)pitch };
                    copy(list, &to, 0, 0, 0, &from, box);
                }
            SubmitList(i, list);
            return true;
        }

        // RA01: M5a-3 - the same for a buffer (an upload buffer holding 'width' x 'height' pixels of 'pixelFormat', 'pitch' bytes per row) copied into the top left of a texture.
        // Cost - CPU: low (a few COM calls plus a submit, tens of microseconds, at most about 90 a second); GPU: low (one buffer to texture copy of the small window picture, far less than a game frame).
        public bool CopyBufferToTexture(nint buffer, nint dest, int destBefore, int destAfter, int pixelFormat, int width, int height, int pitch)
        {
            int i = OpenList(out nint list);
            if (i < 0) return false;

            ResourceBarrier* barrier = stackalloc ResourceBarrier[1];
            if (destBefore != StateCopyDest) { barrier[0] = Transition(dest, destBefore, StateCopyDest); Barrier(list, 1, barrier); }

            TextureCopyLocation to = new() { Resource = dest, Type = 0, SubresourceIndex = 0 };  // RA01: 0 = a texture's subresource
            TextureCopyLocation from = new() { Resource = buffer, Type = 1, Offset = 0, Format = (uint)pixelFormat, Width = (uint)width, Height = (uint)height, Depth = 1, RowPitch = (uint)pitch }; // RA01: 1 = a footprint in a buffer
            ((delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, nint, void>)Slot(list, SlotCopyTextureRegion))(list, &to, 0, 0, 0, &from, 0);

            if (destAfter != StateCopyDest) { barrier[0] = Transition(dest, StateCopyDest, destAfter); Barrier(list, 1, barrier); }
            SubmitList(i, list);
            return true;
        }

        // RA01: M29-1 - pieces of a painted image in a buffer ('width' x 'height' pixels of 'pixelFormat', 'pitch' bytes a row) copied into a texture resting in
        // PRESENT (the game's back buffer), one list: 'boxes' holds 'count' pieces of six numbers - the piece's left, top, right, bottom in the image and the
        // spot (x, y) it goes to in the texture. Like pasting cut-outs onto a poster. Cost - CPU: low (a few COM calls and one submit); GPU: extremely low (a
        // window-sized copy, under a millisecond).
        public bool CopyRegions(nint buffer, int pixelFormat, int width, int height, int pitch, nint dest, int* boxes, int count)
        {
            int i = OpenList(out nint list);
            if (i < 0) return false;
            ResourceBarrier* barrier = stackalloc ResourceBarrier[1];
            barrier[0] = Transition(dest, StatePresent, StateCopyDest);
            Barrier(list, 1, barrier);
            var copy = (delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, uint*, void>)Slot(list, SlotCopyTextureRegion);
            TextureCopyLocation to = new() { Resource = dest, Type = 0, SubresourceIndex = 0 };
            TextureCopyLocation from = new() { Resource = buffer, Type = 1, Offset = 0, Format = (uint)pixelFormat, Width = (uint)width, Height = (uint)height, Depth = 1, RowPitch = (uint)pitch };
            uint* box = stackalloc uint[6];
            for (int k = 0; k < count; k++)
            {
                int* b = boxes + k * 6;
                box[0] = (uint)b[0]; box[1] = (uint)b[1]; box[2] = 0; box[3] = (uint)b[2]; box[4] = (uint)b[3]; box[5] = 1; // RA01: D3D12_BOX
                copy(list, &to, (uint)b[4], (uint)b[5], 0, &from, box);
            }
            barrier[0] = Transition(dest, StateCopyDest, StatePresent);
            Barrier(list, 1, barrier);
            SubmitList(i, list);
            return true;
        }
    }

    // RA01: readable names for the log. The numbers are DXGI_FORMAT values.
    // Cost - CPU: extremely low (a switch on an integer, only used for log lines, rare; a string is built only for unknown formats); GPU: none.
    public static string FormatName(long value) => value switch
    {
        10 => "R16G16B16A16_FLOAT",
        20 => "D32_FLOAT_S8X24_UINT",
        24 => "R10G10B10A2_UNORM",
        28 => "R8G8B8A8_UNORM",
        29 => "R8G8B8A8_UNORM_SRGB",
        40 => "D32_FLOAT",
        45 => "D24_UNORM_S8_UINT",
        55 => "D16_UNORM",
        87 => "B8G8R8A8_UNORM",
        91 => "B8G8R8A8_UNORM_SRGB",
        _ => $"format {value}"
    };
}
