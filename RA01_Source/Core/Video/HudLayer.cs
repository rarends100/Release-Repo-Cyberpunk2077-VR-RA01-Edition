using System.Runtime.InteropServices;
using System.Text;
using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M10-1 - the HUD panel. The game paints its HUD onto its own see-through layer - a render target exactly the screen's size, sRGB 8-bit RGBA, wiped to fully
// transparent (0,0,0,0) each frame, then about 40 small draws (found by M10-0's frame capture, see claude/RA01_M10_HUD_Research_2026-09-30.md) - and a last draw
// lays that layer over the world.
// M10-3b - how the HUD is taken (M10-1's redirect is gone: the layer has smaller copies of itself - mip levels - that the game builds after the HUD for a glow,
// and redirecting their draws drew shrunken HUDs into the corner of our sheet and a purple haze). Now the game paints its whole HUD as normal; the moment it hands
// the finished full-size picture (mip 0) from painting to reading - its own state-change call, RENDER_TARGET -> SHADER_RESOURCE on mip 0 - we first copy mip 0
// onto our sheet and wipe the game's mip 0, then let its call through. Like photocopying the finished sign the moment the painter steps back, then wiping his
// board before it goes on display: our sheet gets the exact HUD, the game's glow and final composite get an empty layer, so the eyes see the world without it.
// The game's own call proves mip 0 is in RENDER_TARGET right then, so nothing about its state is guessed. XrSession copies our sheet onto the floating panel.
// The layer is recognised by its shape on every launch - no addresses, nothing tied to one game version. While the panel isn't live (HUD tab switch off, a game
// menu showing, or no layer found yet) nothing is touched and the HUD stays in the picture as before.
// Also kept from M10-0: the "paint here" handle notes, and Dev Only Tools' one-frame draw-pass capture for future hunts.
// M23-0 - A HUD PER EYE (Rob 2026-10-07: world markers - mission pointers, NPC chatter, ammo-box and computer icons - doubled up close and jiggled further
// away; gone with 3D off, NOT gone with the panel off). In 3D the game draws the eyes in turn, and it places world markers for each frame's eye - from the
// look of it one frame late, so each picture carries the OTHER eye's markers. One sheet shown to both eyes flipped them every frame. Now each frame's HUD
// goes onto the next of Sheets sheets (a small rotation), and at that frame's Present the sheet is labelled with the frame's eye (FrameCopy calls
// NoteFrame with the frame's pose stamp). XrSession then shows each eye the newest sheet of the eye it asks for (HUD tab / Dev row "HUD eye pairing": per
// eye, per eye swapped, or one eye for both) on its own one-eye panel. Builder picture: two sign-painters take turns; each eye now only gets the signs
// painted for it, instead of whichever painter finished last.
// Core only: it moves pictures; nothing here knows about the player.
// Cost - CPU: extremely low (permanent listeners on the game's wipe, paint-target and state-change calls: a pointer compare each, a few thousand a frame; a short
// locked lookup per wipe to transparent); GPU: low (one screen-sized copy and one wipe per frame in the game's list, plus one copy per headset frame to the panel).
internal static unsafe class HudLayer
{
    // RA01: D3D12 function-table positions (d3d12.h order): ID3D12Device, ID3D12GraphicsCommandList(4), ID3D12Resource, ID3D12DescriptorHeap, IDXGISwapChain
    const int SlotCreateCommandAllocator = 9, SlotCreateCommandList = 12, SlotGetIncrement = 15, SlotCreateRtv = 20, SlotCopyDescriptorsSimple = 24, SlotCreateCommitted = 27;
    const int SlotReset = 10, SlotDraw = 12, SlotDrawIndexed = 13, SlotDispatch = 14, SlotCopyResource = 17, SlotSetTargets = 46, SlotClearRtv = 48;
    const int SlotSetMarker = 56, SlotBeginEvent = 57, SlotBeginRenderPass = 68;
    const int SlotResourceBarrier = 26, SlotEnhancedBarrier = 80; // RA01: M10-3a - the classic state-change call, and the newer "enhanced barriers" one (ID3D12GraphicsCommandList7)
    const int SlotResourceGetDesc = 10, SlotSwapGetBuffer = 9;
    const int RtvHeapType = 2;          // RA01: D3D12_DESCRIPTOR_HEAP_TYPE_RTV
    const int BeginClear = 2;           // RA01: D3D12_RENDER_PASS_BEGINNING_ACCESS_TYPE_CLEAR
    const int StateCopyDest = 0x400, StateCopySource = 0x800; // RA01: M10-3b - D3D12_RESOURCE_STATE_COPY_DEST / COPY_SOURCE
    const uint AllSubresources = 0xFFFFFFFF; // RA01: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
    const int SlotCopyTextureRegion = 16;
    const int StateRenderTarget = 0x4;  // RA01: D3D12_RESOURCE_STATE_RENDER_TARGET - our sheet always rests in it, so the game's own lists can paint on it without knowing
    const int HudFormat = 29;           // RA01: M10-2 - DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: the HUD layer's view format in every capture; the screen-sized RGBA8 (28) targets the loading screens wipe are other layers
    const int FormatTypeless = 27;      // RA01: DXGI_FORMAT_R8G8B8A8_TYPELESS - our sheet's format, so it can be painted as the game's layer is and copied into the headset's image
    const string IidDevice = "189819f1-1db6-4b57-be54-1821339b85f7", IidAllocator = "6102dee4-af59-4b09-b999-b44d73f09b24";
    const string IidList = "5b160d0f-ac1b-4185-8ba8-b3ae42a5a455", IidResource = "696442be-a72e-4059-bc79-5b5c98040fad";

    // ---------------------------------------------------------------- which picture each "paint here" handle points at (always on)

    const int RtvSlots = 16384;         // RA01: a power of two; the game keeps a few thousand render-target views at most
    static readonly nint[] rtvKey = new nint[RtvSlots], rtvResource = new nint[RtvSlots];
    static readonly int[] rtvWidth = new int[RtvSlots], rtvHeight = new int[RtvSlots], rtvFormat = new int[RtvSlots], rtvMip = new int[RtvSlots]; // RA01: M10-3b - mip: which size level of the picture the view paints
    static readonly object rtvGate = new();
    static nint originalCreateRtv, originalCopySimple, originalSetTargets, originalClearRtv, originalBarrier;
    static uint rtvStep;                // RA01: bytes between neighbouring render-target handles on this graphics card
    static nint gameDevice;             // RA01: the device the game makes its views on (our sheet is made on the same one)

    // RA01: the game made a render-target view: let it, then note which picture it points at.
    // Cost - CPU: extremely low (one GetDesc and a short locked table write; the game makes these rarely, mostly at loads); GPU: none of its own.
    [UnmanagedCallersOnly]
    static void DetourCreateRtv(nint device, nint resource, nint desc, nint handle)
    {
        ((delegate* unmanaged<nint, nint, nint, nint, void>)originalCreateRtv)(device, resource, desc, handle);
        try
        {
            if (rtvStep == 0) { rtvStep = ((delegate* unmanaged<nint, int, uint>)Slot(device, SlotGetIncrement))(device, RtvHeapType); gameDevice = device; }
            ResourceDesc d = default;
            if (resource != 0) ((delegate* unmanaged<nint, ResourceDesc*, ResourceDesc*>)Slot(resource, SlotResourceGetDesc))(resource, &d);
            int format = desc != 0 && *(int*)desc != 0 ? *(int*)desc : d.Format; // RA01: the view's own format, or the picture's when the view doesn't name one
            int mip = desc != 0 && *(int*)(desc + 4) == 4 ? *(int*)(desc + 8) : 0;    // RA01: M10-3b - D3D12_RENDER_TARGET_VIEW_DESC: TEXTURE2D (4) keeps its mip level at +8
            lock (rtvGate) Store(handle, resource, (int)d.Width, (int)d.Height, format, mip);
        }
        catch { }
    }

    // RA01: the game copied handles from one place to another; render-target ones take their notes along.
    // Cost - CPU: extremely low (one compare for every other kind of handle; for render-target handles a short locked copy per handle); GPU: none of its own.
    [UnmanagedCallersOnly]
    static void DetourCopySimple(nint device, uint count, nint destination, nint source, int heapType)
    {
        ((delegate* unmanaged<nint, uint, nint, nint, int, void>)originalCopySimple)(device, count, destination, source, heapType);
        if (heapType != RtvHeapType || rtvStep == 0) return;
        try
        {
            lock (rtvGate)
                for (uint i = 0; i < count; i++)
                {
                    int from = Find(source + (nint)(i * rtvStep));
                    if (from >= 0) Store(destination + (nint)(i * rtvStep), rtvResource[from], rtvWidth[from], rtvHeight[from], rtvFormat[from], rtvMip[from]);
                }
        }
        catch { }
    }

    // RA01: the handle's slot in the notes table, or -1 (open addressing: start at a slot picked from the handle's bits, walk forward up to 32 places). Caller holds rtvGate.
    // Cost - CPU: extremely low (a multiply and at most 32 compares); GPU: none.
    static int Find(nint handle)
    {
        int home = (int)(((ulong)handle * 0x9E3779B97F4A7C15UL) >> 50); // RA01: 64 - 14 bits = 16384 slots
        for (int k = 0; k < 32; k++) { int i = (home + k) & (RtvSlots - 1); if (rtvKey[i] == handle) return i; if (rtvKey[i] == 0) return -1; }
        return -1;
    }

    // RA01: writes one note (same handle: updated in place; table crowded: the home slot is overwritten). A handle re-made for another picture stops counting as the HUD's.
    // Caller holds rtvGate.
    // Cost - CPU: extremely low (at most 32 compares and a few writes); GPU: none.
    static void Store(nint handle, nint resource, int width, int height, int format, int mip)
    {
        int home = (int)(((ulong)handle * 0x9E3779B97F4A7C15UL) >> 50), at = home;
        for (int k = 0; k < 32; k++) { int i = (home + k) & (RtvSlots - 1); if (rtvKey[i] == handle || rtvKey[i] == 0) { at = i; break; } }
        rtvKey[at] = handle; rtvResource[at] = resource; rtvWidth[at] = width; rtvHeight[at] = height; rtvFormat[at] = format; rtvMip[at] = mip;
        if (resource != hudLayer || mip != 0) for (int i = 0; i < hudHandles.Length; i++) if (hudHandles[i] == handle) hudHandles[i] = 0;
    }

    // ---------------------------------------------------------------- the HUD layer and our sheet

    static nint hudLayer;               // RA01: the game's HUD layer picture (0 = not found yet)
    static int hudWidth, hudHeight, hudVersion;
    static readonly nint[] hudHandles = new nint[8]; // RA01: handles known to point at the HUD layer, checked first without the lock
    static nint sheet;                  // RA01: our own sheet (0 = not made yet) - M23-0: the first of the rotation, for the checks that only need "made"
    const int Sheets = 4;               // RA01: M23-0 - the rotation: enough that a sheet being copied to the headset is never the one the game is painting
    static readonly nint[] sheets = new nint[Sheets];
    static readonly int[] sheetEye = new int[Sheets];   // RA01: M23-0 - the eye each sheet's frame was built for: 0 = not presented yet, 1 left, 2 right, 3 no eye (2D)
    static readonly long[] sheetSeq = new long[Sheets]; // RA01: M23-0 - when it was filled (a running count; 0 = never)
    static volatile int lastTaken = -1;                 // RA01: M23-0 - the sheet the newest frame's HUD went onto
    static long takeSeq;
    static int sheetsWidth, sheetsHeight;
    static readonly List<(nint Texture, long ReleaseAt)> retired = new();   // RA01: M23-3 - old sheets and blank squares, freed RetireSeconds later (the GPU may still be reading them)
    static readonly object retiredGate = new();
    const double RetireSeconds = 3.0;               // RA01: M23-1 - the size the sheets were made at (they are reused for any new layer of that size)
    static int sheetVersion;            // RA01: the hudVersion the sheet was made for
    static volatile bool sheetFilled;   // RA01: M10-3b - our sheet has had a finished HUD copied in (a new texture holds leftover video memory - "TV fuzz" - until then)
    static nint copiedFrom;             // RA01: M10-3b - the HUD layer handle whose wipe opened the current frame's HUD; cleared once that frame's HUD is copied, so it's copied once
    static readonly object sheetGate = new(); // RA01: M10-2 - only one thread at a time takes a new HUD layer on
    static FrameCopy.Copier? copier;    // RA01: the headset thread's own copy helper
    static volatile bool panelLive;     // RA01: set by XrSession: the panel is being shown, so taking the HUD off the picture is safe (it won't just vanish)
    public static volatile bool HideCrosshair; // RA01: M13-2 - set by Actions (WeaponRules): the crosshair should be off. M13-5 - the centre wipe was removed (it cut holes in
                                                // RA01: the phone and other HUD parts, and missed the wide shotgun and melee crosshairs); nothing acts on this until the game's own
                                                // RA01: crosshair switch is found

    // RA01: XrSession says whether the panel is on screen right now. The HUD is only taken while it is.
    // RA01: Cost - CPU: extremely low (one write per headset frame); GPU: none.
    public static bool PanelLive { set => panelLive = value; }

    // RA01: true while the finished HUD is being taken off the game's layer onto our sheet.
    // RA01: Cost - CPU: extremely low (a few reads); GPU: none.
    static bool Active => panelLive && sheet != 0 && sheetVersion == hudVersion && Tuning.HudPanel && !MenuSense.ShowsMenu && PlayerSkeleton.InGame; // RA01: M10-4 - never on the start screen or loading screens

    // RA01: true (with the sheet's size and version) when the HUD layer has been found and our sheet exists, so XrSession can make a panel that size.
    // Cost - CPU: extremely low (a few reads, once per headset frame); GPU: none.
    public static bool Ready(out int width, out int height, out int version)
    {
        width = hudWidth; height = hudHeight; version = sheetVersion;
        return sheet != 0 && sheetVersion == hudVersion;
    }

    // RA01: headset thread: copies our sheet into a headset panel image (which rests in RENDER_TARGET). False = the GPU was busy, the image keeps its last picture.
    // RA01: The game's HUD pass (wipe and draws) is one command list on the same queue, so this copy always lands between whole HUD pictures, never in the middle of one.
    // RA01: Cost - CPU: low (a few COM calls and one submit, tens of microseconds, once per headset frame); GPU: low (one screen-sized copy).
    public static bool SheetFilled => sheetFilled; // RA01: M10-3b - XrSession only shows the panel once this is true

    public static bool CopyTo(nint image, nint device)
    {
        return CopyTo(image, device, 0);
    }

    // RA01: M23-0 - the same, from the newest sheet of 'eye' (1 left, 2 right; 0 = the newest of any eye). Cost - as CopyTo.
    public static bool CopyTo(nint image, nint device, int eye)
    {
        nint from = Pick(eye);
        if (from == 0) return false;
        copier ??= new FrameCopy.Copier(device);
        return copier.Copy(from, StateRenderTarget, StateRenderTarget, image, StateRenderTarget, StateRenderTarget);
    }

    // RA01: M23-0 - the newest presented sheet built for 'eye' (0 = any eye); if there is none of that eye yet, the newest presented one. Sheets not presented yet are
    // skipped (their copy may still be on its way). 0 = nothing to show yet.
    // Cost - CPU: extremely low (a loop over four sheets); GPU: none.
    static nint Pick(int eye)
    {
        int best = -1, any = -1;
        for (int k = 0; k < Sheets; k++)
        {
            int label = Volatile.Read(ref sheetEye[k]);
            long seq = Volatile.Read(ref sheetSeq[k]);
            if (label == 0 || seq == 0 || sheets[k] == 0) continue;
            if (any < 0 || seq > sheetSeq[any]) any = k;
            if ((eye == 0 || label == eye) && (best < 0 || seq > sheetSeq[best])) best = k;
        }
        int pick = best >= 0 ? best : any;
        return pick >= 0 ? sheets[pick] : 0;
    }

    // RA01: M23-0 - FrameCopy, at every Present: labels the sheet the newest HUD went onto with this frame's eye (from its pose stamp; 3 = no eye, 2D or unknown).
    // Cost - CPU: extremely low (a pose lookup and a store, 100-400 times a second); GPU: none.
    public static void NoteFrame(long stamp)
    {
        int k = lastTaken;
        if (k < 0 || Volatile.Read(ref sheetEye[k]) != 0) return;
        int eye = stamp != 0 && HeadPose.PoseOf(stamp, out _, out int e) && e is 1 or 2 ? e : 3;
        Volatile.Write(ref sheetEye[k], eye);
    }

    // RA01: M23-3 - puts textures aside to be freed RetireSeconds from now (Tidy frees them). Cost - CPU: extremely low (a short locked list add, on a resolution change); GPU: none.
    static void Retire(nint[] textures)
    {
        long at = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(RetireSeconds * System.Diagnostics.Stopwatch.Frequency);
        lock (retiredGate) foreach (nint t in textures) if (t != 0) retired.Add((t, at));
    }

    // RA01: M23-3 - XrSession, once per headset frame: frees the put-aside textures whose time has come (only after a resolution change; otherwise the list is empty).
    // Cost - CPU: extremely low (one count check per headset frame; a Release per old texture, rarely); GPU: none (frees video memory).
    public static void Tidy()
    {
        if (retired.Count == 0) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (retiredGate)
            for (int i = retired.Count - 1; i >= 0; i--)
                if (now >= retired[i].ReleaseAt) { Release(retired[i].Texture); retired.RemoveAt(i); }
    }

    // RA01: M23-0 - is this picture one of our sheets? Cost - CPU: extremely low (four compares); GPU: none.
    static bool IsSheet(nint picture) { foreach (nint s in sheets) if (s != 0 && s == picture) return true; return false; }

    // RA01: M13-61 - THE CROSSHAIR ON THE TRUE AIM. The game draws its crosshair in the middle of its HUD picture, and the middle of the game's picture is where
    // it really aims. With the HUD panel moved (up, down, nearer), that middle no longer sits on the aim - so it is cut out: the panel image gets the whole
    // sheet with its centre square (side x side pixels) blanked, and the crosshair image gets that square, which XrSession floats on the middle of the
    // game's own picture. One crosshair, always where the shot goes; the rest of the HUD stays where you put it. False = the GPU was busy (both images keep
    // their last picture) or the blank square couldn't be made.
    // Cost - CPU: low (a few COM calls and one submit per headset frame); GPU: low (one screen-sized copy and two small square copies).
    public static bool CopySplitTo(nint panelImage, nint crosshairImage, int side, nint device, int eye = 0)
    {
        nint from = Pick(eye);                                                       // RA01: M23-0 - the sheet of the eye asked for
        if (from == 0 || side <= 0 || side > hudHeight || side > hudWidth) return false;
        if (blank == 0 || blankSide != side) MakeBlank(device, side);
        if (blank == 0) return false;
        copier ??= new FrameCopy.Copier(device);
        return copier.CopySplit(from, panelImage, crosshairImage, blank, hudWidth, hudHeight, side);
    }
    static nint blank;              // RA01: M13-61 - an all-transparent square texture (resting in COPY_SOURCE) for blanking the panel's centre
    static int blankSide;

    // RA01: M13-61 - makes the blank square: a committed texture is handed over zero-filled by D3D12 (no CREATE_NOT_ZEROED flag), and zero is fully transparent
    // in RGBA8. Never released (the GPU may still use an old one) - a new side size (resolution change) leaves one small texture behind.
    // Cost - CPU: low (one texture, under a millisecond, rarely); GPU: extremely low (one small allocation).
    static void MakeBlank(nint device, int side)
    {
        try
        {
            HeapProperties heap = new() { Type = 1, CreationNodeMask = 1, VisibleNodeMask = 1 }; // RA01: D3D12_HEAP_TYPE_DEFAULT
            ResourceDesc desc = new() { Dimension = 3, Width = (ulong)side, Height = (uint)side, DepthOrArraySize = 1, MipLevels = 1, Format = FormatTypeless, SampleCount = 1, Flags = 0 };
            Guid iidResource = new(IidResource);
            nint texture;
            Check(((delegate* unmanaged<nint, HeapProperties*, int, ResourceDesc*, int, nint, Guid*, nint*, int>)Slot(device, SlotCreateCommitted))
                (device, &heap, 0, &desc, StateCopySource, 0, &iidResource, &texture), "CreateCommittedResource (HUD blank square)");
            if (blank != 0) Retire(new[] { blank });                                  // RA01: M23-3 - the old square is freed a moment later
            blank = texture; blankSide = side;
        }
        catch (Exception e) { blank = 0; Log.Write($"RA01 HUD layer: the blank square could not be made - {e.Message}; the crosshair stays on the panel"); }
    }

    // RA01: true if this handle paints the HUD layer's full-size picture, mip 0 (the handles already known are checked first without taking the lock).
    // Cost - CPU: extremely low (up to 8 compares; otherwise one short locked lookup); GPU: none.
    static bool IsHud(nint handle)
    {
        if (handle == 0 || hudLayer == 0) return false;
        foreach (nint h in hudHandles) if (h == handle) return true;
        lock (rtvGate)
        {
            int i = Find(handle);
            if (i < 0 || rtvResource[i] != hudLayer || rtvMip[i] != 0) return false;
            for (int k = 0; k < hudHandles.Length; k++) if (hudHandles[k] == 0) { hudHandles[k] = handle; break; }
            return true;
        }
    }

    // RA01: a wipe to fully transparent: is this the HUD layer (the screen's size, 8-bit RGBA family)? A new one (first time, or after a resolution change) is taken on.
    // Cost - CPU: extremely low (one short locked lookup per wipe to transparent, about a hundred a frame); on a new layer, making our sheet once (under a millisecond); GPU: none.
    static void Recognise(nint handle)
    {
        int width, height, format;
        nint picture;
        lock (rtvGate)
        {
            int i = Find(handle);
            if (i < 0) return;
            picture = rtvResource[i]; width = rtvWidth[i]; height = rtvHeight[i]; format = rtvFormat[i];
            if (rtvMip[i] != 0) return;
        }
        if (picture == hudLayer || IsSheet(picture) || format != HudFormat || width != FrameCopy.Width || height != FrameCopy.Height) return;
        lock (sheetGate) // RA01: M10-2 - several render threads can wipe at once; only one takes a new layer on and makes its sheet
        {
            if (picture == hudLayer) return;
            lock (rtvGate) { Array.Clear(hudHandles); hudHandles[0] = handle; hudLayer = picture; hudWidth = width; hudHeight = height; hudVersion++; }
            Log.Write($"RA01 HUD layer: found - picture 0x{picture:X}, {width}x{height}, format {format}");
            MakeSheet();
        }
    }

    // RA01: M23-1 - made only when there are none yet or the HUD's size changed (a resolution change); a new layer of the same size reuses them.
    // our sheets (M23-0: Sheets of them, a rotation): textures the HUD layer's size (one mip level), resting in RENDER_TARGET, that each frame's finished HUD is copied onto. Never released (the GPU
    // may still be using an old one), so a resolution change leaves one screen-sized texture behind - rare.
    // Cost - CPU: low (four textures, under a millisecond, once per HUD layer found); GPU: low (four screen-sized allocations, a few MB each).
    static void MakeSheet()
    {
        try
        {
            nint device = gameDevice;
            if (device == 0) return;
            if (sheets[0] != 0 && sheetsWidth == hudWidth && sheetsHeight == hudHeight)   // RA01: M23-1 - THE LEAK FIX (Rob approved 2026-10-07): the game makes a fresh
            {                                                                            // RA01: HUD layer at nearly every menu or PC switch (9 times in 6 minutes);
                sheetVersion = hudVersion;                                               // RA01: making new sheets each time leaked 27 MB (one sheet) or 107 MB (four)
                return;                                                                  // RA01: of video memory per switch. Same size: the same sheets serve the new layer.
            }
            HeapProperties heap = new() { Type = 1, CreationNodeMask = 1, VisibleNodeMask = 1 }; // RA01: D3D12_HEAP_TYPE_DEFAULT
            ResourceDesc desc = new() { Dimension = 3, Width = (ulong)hudWidth, Height = (uint)hudHeight, DepthOrArraySize = 1, MipLevels = 1, Format = FormatTypeless, SampleCount = 1, Flags = 0x1 }; // RA01: TEXTURE2D, ALLOW_RENDER_TARGET
            Guid iidResource = new(IidResource);
            nint* made = stackalloc nint[Sheets];
            for (int k = 0; k < Sheets; k++)                                          // RA01: M23-0 - the rotation, all made before any is used
                Check(((delegate* unmanaged<nint, HeapProperties*, int, ResourceDesc*, int, nint, Guid*, nint*, int>)Slot(device, SlotCreateCommitted))
                    (device, &heap, 0, &desc, StateRenderTarget, 0, &iidResource, &made[k]), "CreateCommittedResource (HUD sheet)");
            sheetFilled = false;
            lastTaken = -1;
            Retire(sheets);                                                           // RA01: M23-3 - the old size's sheets are freed a moment later (the leak on a resolution change)
            for (int k = 0; k < Sheets; k++) { sheets[k] = made[k]; sheetEye[k] = 0; sheetSeq[k] = 0; }
            sheet = made[0]; sheetVersion = hudVersion;
            sheetsWidth = hudWidth; sheetsHeight = hudHeight;
            Log.Write($"RA01 HUD layer: our {Sheets} sheets are ready ({hudWidth}x{hudHeight}) - one HUD per frame, labelled with its eye");
        }
        catch (Exception e) { Log.Write($"RA01 HUD layer: our sheet could not be made - {e.Message}; the HUD stays in the picture"); }
    }

    // RA01: the game picks what to paint on: let it, and note it during a capture.
    // Cost - CPU: extremely low (the forward and one compare); GPU: none of its own.
    [UnmanagedCallersOnly]
    static void DetourSetTargets(nint list, uint count, nint targets, int oneRange, nint depth)
    {
        ((delegate* unmanaged<nint, uint, nint, int, nint, void>)originalSetTargets)(list, count, targets, oneRange, depth);
        NoteTargets(list, count, count > 0 && targets != 0 ? *(nint*)targets : 0, depth, null);
    }

    // RA01: the game wipes a picture: let it. A wipe to transparent may be the HUD layer - recognise it, and mark that this frame's HUD is now being painted.
    // Also noted during a capture.
    // Cost - CPU: extremely low (four float compares; for wipes to transparent, one short locked lookup); GPU: none of its own.
    [UnmanagedCallersOnly]
    static void DetourClearRtv(nint list, nint handle, float* color, uint rects, nint rectList)
    {
        var original = (delegate* unmanaged<nint, nint, float*, uint, nint, void>)originalClearRtv;
        original(list, handle, color, rects, rectList);
        if (color != null && color[0] == 0 && color[1] == 0 && color[2] == 0 && color[3] == 0)
        {
            try { if (!IsHud(handle)) Recognise(handle); } catch { }
            if (IsHud(handle)) copiedFrom = handle; // RA01: M10-3b - this frame's HUD starts here; it is copied at the state change that ends it
        }
        int at = NewRecord(list, Kind.Clear);
        if (at < 0) return;
        Describe(at, handle);
        if (color != null) { records[at].R = color[0]; records[at].G = color[1]; records[at].B = color[2]; records[at].A = color[3]; }
    }

    // ---------------------------------------------------------------- Dev Only Tools: the one-frame draw-pass capture (kept from M10-0)

    enum Kind { Targets = 1, Clear, Label, Copy, Reset }
    struct Record
    {
        public nint List, Resource, Other;
        public int Kind, Width, Height, Format, Count, Draws, Dispatches;
        public float R, G, B, A;
        public bool Cleared;
        public string? Text;
    }

    const int MaxRecords = 100_000, MaxSubmits = 8192, ListSlots = 2048;
    static Record[] records = Array.Empty<Record>();
    static int recordCount, submitCount, unknownHandles;
    static readonly nint[] submitList = new nint[MaxSubmits], submitQueue = new nint[MaxSubmits];
    static readonly nint[] listKey = new nint[ListSlots];
    static readonly int[] listCurrent = new int[ListSlots];   // RA01: each command list's newest Targets record, so its draws are counted against the right target
    static readonly object listGate = new();
    static readonly nint[] screenBuffers = new nint[4];
    static nint listTable;                                     // RA01: the command lists' shared function table (read once at start-up)
    static readonly int[] captureSlots = { SlotReset, SlotDraw, SlotDrawIndexed, SlotDispatch, SlotCopyResource, SlotSetMarker, SlotBeginEvent, SlotBeginRenderPass, SlotEnhancedBarrier };
    static readonly nint[] listOriginal = new nint[96];        // RA01: the game's own functions for those slots; never cleared, since a call already inside a listener may still need them
    static readonly bool[] listPatched = new bool[96];
    static int state;                   // RA01: 0 idle, 1 armed (listening, waiting for the next Present), 2 recording, 3 writing the log
    static string status = "idle - press Capture draw passes in game";

    // RA01: Dev Only Tools' "Capture draw passes": puts the capture's listeners in; recording starts at the next Present and runs until the one after.
    // Cost - CPU: extremely low (eight table writes; the 8 MB record array is made once, the first time); GPU: none.
    public static void Capture()
    {
        if (listTable == 0) { status = "not available - see log"; return; }
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
        if (records.Length == 0) records = new Record[MaxRecords];
        recordCount = submitCount = unknownHandles = 0;
        Array.Clear(listKey);
        foreach (int s in captureSlots)
        {
            nint now = ((nint*)listTable)[s];
            if (!PresentHook.InsideD3D12(now)) continue; // RA01: slot missing on an older Windows, or someone else's hook there - leave it alone
            listOriginal[s] = now;
            PresentHook.Patch(listTable, s, Detour(s));
            listPatched[s] = true;
        }
        status = "armed - recording the next frame...";
    }

    // RA01: status line for Dev Only Tools: the capture, and whether the HUD layer has been found.
    // Cost - CPU: extremely low (a short string); GPU: none.
    public static string Status() => $"{status}; HUD layer {(hudLayer == 0 ? "not found yet" : $"found {hudWidth}x{hudHeight}")}{(Active ? ", on the panel" : "")}";

    // RA01: the capture's replacement for each listened-to slot.
    // Cost - CPU: extremely low (a switch); GPU: none.
    static nint Detour(int slot) => slot switch
    {
        SlotReset => (nint)(delegate* unmanaged<nint, nint, nint, int>)&DetourReset,
        SlotDraw => (nint)(delegate* unmanaged<nint, uint, uint, uint, uint, void>)&DetourDraw,
        SlotDrawIndexed => (nint)(delegate* unmanaged<nint, uint, uint, uint, int, uint, void>)&DetourDrawIndexed,
        SlotDispatch => (nint)(delegate* unmanaged<nint, uint, uint, uint, void>)&DetourDispatch,
        SlotCopyResource => (nint)(delegate* unmanaged<nint, nint, nint, void>)&DetourCopyResource,
        SlotSetMarker => (nint)(delegate* unmanaged<nint, uint, byte*, uint, void>)&DetourSetMarker,
        SlotBeginEvent => (nint)(delegate* unmanaged<nint, uint, byte*, uint, void>)&DetourBeginEvent,
        SlotEnhancedBarrier => (nint)(delegate* unmanaged<nint, uint, byte*, void>)&DetourEnhancedBarrier,
        _ => (nint)(delegate* unmanaged<nint, uint, nint, nint, int, void>)&DetourBeginRenderPass,
    };

    // RA01: called by PresentHook on every Present: moves a capture along (armed -> recording -> done).
    // Cost - CPU: extremely low (a compare; at a capture's end, eight table writes and a background task start); GPU: none.
    public static void OnPresent(nint swapChain)
    {
        if (state == 1)
        {
            NoteScreenBuffers(swapChain);
            state = 2;
            status = "recording...";
        }
        else if (state == 2) // RA01: the next Present: one whole frame recorded
        {
            state = 3;
            foreach (int s in captureSlots) if (listPatched[s]) { PresentHook.Patch(listTable, s, listOriginal[s]); listPatched[s] = false; }
            status = "writing the log...";
            Task.Run(WriteCapture);
        }
    }

    // RA01: called by PresentHook for every ExecuteCommandLists: while recording, the order the command lists went to the GPU.
    // Cost - CPU: extremely low (one compare; while recording, a few writes per list); GPU: none.
    public static void OnExecute(nint queue, uint count, nint lists)
    {
        if (state != 2) return;
        for (uint i = 0; i < count; i++)
        {
            int at = Interlocked.Increment(ref submitCount) - 1;
            if (at >= MaxSubmits) return;
            submitList[at] = ((nint*)lists)[i]; submitQueue[at] = queue;
        }
    }

    // RA01: the swapchain's own pictures (what finally reaches the screen), so the log can mark them.
    // Cost - CPU: extremely low (up to four GetBuffer/Release pairs, once per capture); GPU: none.
    static void NoteScreenBuffers(nint swapChain)
    {
        Guid iid = new(IidResource);
        for (uint i = 0; i < 4; i++)
        {
            nint buffer = 0;
            screenBuffers[i] = ((delegate* unmanaged<nint, uint, Guid*, nint*, int>)Slot(swapChain, SlotSwapGetBuffer))(swapChain, i, &iid, &buffer) >= 0 ? buffer : 0;
            Release(buffer);
        }
    }

    // RA01: a new record for this command list (or -1 when recording is off or the store is full).
    // Cost - CPU: extremely low (one compare, while recording one atomic add); GPU: none.
    static int NewRecord(nint list, Kind kind)
    {
        if (state != 2) return -1;
        int at = Interlocked.Increment(ref recordCount) - 1;
        if (at >= MaxRecords) return -1;
        records[at] = new Record { List = list, Kind = (int)kind };
        return at;
    }

    // RA01: a paint-target switch, recorded while capturing.
    // Cost - CPU: extremely low (one compare when not capturing); GPU: none.
    static void NoteTargets(nint list, uint count, nint first, nint depth, string? text)
    {
        int at = NewRecord(list, Kind.Targets);
        if (at < 0) return;
        records[at].Count = (int)count; records[at].Other = depth != 0 ? 1 : 0; records[at].Text = text;
        if (count > 0) Describe(at, first);
        Current(list, at);
    }

    // RA01: fills a record with the picture a handle points at.
    // Cost - CPU: extremely low (a short locked lookup); GPU: none.
    static void Describe(int at, nint handle)
    {
        lock (rtvGate)
        {
            int i = Find(handle);
            if (i < 0) { Interlocked.Increment(ref unknownHandles); return; }
            records[at].Resource = rtvResource[i]; records[at].Width = rtvWidth[i]; records[at].Height = rtvHeight[i]; records[at].Format = rtvFormat[i];
        }
    }

    // RA01: which Targets record this list's draws count against; a list that picked its target before recording started gets one made for it.
    // Cost - CPU: extremely low (a short walk of a small table); GPU: none.
    static int Current(nint list, int setTo = -2)
    {
        int home = (int)(((ulong)list * 0x9E3779B97F4A7C15UL) >> 53); // RA01: 2048 slots
        lock (listGate)
        {
            for (int k = 0; k < ListSlots; k++)
            {
                int i = (home + k) & (ListSlots - 1);
                if (listKey[i] != list && listKey[i] != 0) continue;
                if (setTo != -2) { listKey[i] = list; listCurrent[i] = setTo; return setTo; }
                if (listKey[i] == list) return listCurrent[i];
                int made = NewRecord(list, Kind.Targets);
                if (made >= 0) records[made].Text = "(target picked before recording started)";
                listKey[i] = list; listCurrent[i] = made;
                return made;
            }
        }
        return -1;
    }

    // RA01: the capture's listeners (only in the table during a capture). Each lets the game's call through first, then notes it while recording.
    // Cost - CPU: extremely low each (the forward, one compare, and while recording a lookup and an atomic add); GPU: none of their own.
    [UnmanagedCallersOnly]
    static int DetourReset(nint list, nint allocator, nint pipeline)
    {
        int hr = ((delegate* unmanaged<nint, nint, nint, int>)listOriginal[SlotReset])(list, allocator, pipeline);
        if (state == 2) { NewRecord(list, Kind.Reset); Current(list, -1); }
        return hr;
    }

    [UnmanagedCallersOnly]
    static void DetourDraw(nint list, uint vertices, uint instances, uint firstVertex, uint firstInstance)
    {
        ((delegate* unmanaged<nint, uint, uint, uint, uint, void>)listOriginal[SlotDraw])(list, vertices, instances, firstVertex, firstInstance);
        if (state == 2) { int at = Current(list); if (at >= 0) Interlocked.Increment(ref records[at].Draws); }
    }

    [UnmanagedCallersOnly]
    static void DetourDrawIndexed(nint list, uint indices, uint instances, uint firstIndex, int baseVertex, uint firstInstance)
    {
        ((delegate* unmanaged<nint, uint, uint, uint, int, uint, void>)listOriginal[SlotDrawIndexed])(list, indices, instances, firstIndex, baseVertex, firstInstance);
        if (state == 2) { int at = Current(list); if (at >= 0) Interlocked.Increment(ref records[at].Draws); }
    }

    [UnmanagedCallersOnly]
    static void DetourDispatch(nint list, uint x, uint y, uint z)
    {
        ((delegate* unmanaged<nint, uint, uint, uint, void>)listOriginal[SlotDispatch])(list, x, y, z);
        if (state == 2) { int at = Current(list); if (at >= 0) Interlocked.Increment(ref records[at].Dispatches); }
    }

    [UnmanagedCallersOnly]
    static void DetourCopyResource(nint list, nint destination, nint source)
    {
        ((delegate* unmanaged<nint, nint, nint, void>)listOriginal[SlotCopyResource])(list, destination, source);
        int at = NewRecord(list, Kind.Copy);
        if (at >= 0) { records[at].Resource = destination; records[at].Other = source; }
    }

    [UnmanagedCallersOnly]
    static void DetourSetMarker(nint list, uint metadata, byte* data, uint size)
    {
        ((delegate* unmanaged<nint, uint, byte*, uint, void>)listOriginal[SlotSetMarker])(list, metadata, data, size);
        int at = NewRecord(list, Kind.Label);
        if (at >= 0) records[at].Text = "marker " + LabelText(metadata, data, size);
    }

    [UnmanagedCallersOnly]
    static void DetourBeginEvent(nint list, uint metadata, byte* data, uint size)
    {
        ((delegate* unmanaged<nint, uint, byte*, uint, void>)listOriginal[SlotBeginEvent])(list, metadata, data, size);
        int at = NewRecord(list, Kind.Label);
        if (at >= 0) records[at].Text = "event " + LabelText(metadata, data, size);
    }

    [UnmanagedCallersOnly]
    static void DetourBeginRenderPass(nint list, uint count, nint targets, nint depth, int flags)
    {
        ((delegate* unmanaged<nint, uint, nint, nint, int, void>)listOriginal[SlotBeginRenderPass])(list, count, targets, depth, flags);
        int at = NewRecord(list, Kind.Targets);
        if (at < 0) return;
        records[at].Count = (int)count; records[at].Other = depth != 0 ? 1 : 0; records[at].Text = "render pass";
        if (count > 0 && targets != 0)
        {
            Describe(at, *(nint*)targets); // RA01: D3D12_RENDER_PASS_RENDER_TARGET_DESC: the handle first, then how the pass begins (+8), a clear's colour at +16
            if (*(int*)(targets + 8) == BeginClear) { float* c = (float*)(targets + 16); records[at].Cleared = true; records[at].R = c[0]; records[at].G = c[1]; records[at].B = c[2]; records[at].A = c[3]; }
        }
        Current(list, at);
    }

    // RA01: M10-3b - the game's classic state-change call (D3D12_RESOURCE_BARRIER, 32 bytes each: type, flags, then for a transition the picture at +8, the
    // subresource at +16, before at +20, after at +24). Permanent. When it hands the HUD layer's mip 0 (or the whole layer) from RENDER_TARGET to anything else for
    // the first time this frame, and the panel is live: copy mip 0 onto our sheet and wipe it (TakeHud), THEN let the game's call through. During a capture, state
    // changes on the HUD layer or our sheet are also noted (M10-3a).
    // Cost - CPU: extremely low (a pointer compare per state change, a few thousand a frame); GPU: once per frame while live, one screen-sized copy and one wipe.
    [UnmanagedCallersOnly]
    static void DetourResourceBarrier(nint list, uint count, byte* barriers)
    {
        if (barriers != null && hudLayer != 0)
            for (uint i = 0; i < count; i++)
            {
                byte* b = barriers + i * 32;
                if (*(nint*)(b + 8) != hudLayer) continue;
                uint subresource = *(uint*)(b + 16);
                if (*(int*)b == 0 && *(int*)(b + 20) == StateRenderTarget && *(int*)(b + 24) != StateRenderTarget && (subresource == 0 || subresource == AllSubresources)
                    && copiedFrom != 0 && Active)
                {
                    try { TakeHud(list, copiedFrom); } catch { }
                    copiedFrom = 0;
                }
                if (state == 2)
                {
                    int at = NewRecord(list, Kind.Label);
                    if (at >= 0) records[at].Text = *(int*)b == 0 ? $"state change HUD LAYER (subresource {(subresource == AllSubresources ? "all" : subresource.ToString())}): {StateName(*(int*)(b + 20))} -> {StateName(*(int*)(b + 24))}" : $"state barrier type {*(int*)b} on HUD LAYER";
                }
            }
        ((delegate* unmanaged<nint, uint, byte*, void>)originalBarrier)(list, count, barriers);
    }

    // RA01: M10-3b - recorded into the game's own command list, right before its "done painting" state change: mip 0 of the HUD layer -> copy source and our sheet ->
    // copy destination; copy mip 0 across; both back to RENDER_TARGET; wipe the game's mip 0 to transparent. The game's mips and composite then work from an empty
    // layer. All on the game's list, so it runs at exactly that point in the frame.
    // Cost - CPU: extremely low (four or five small recording calls); GPU: low (one screen-sized copy and one wipe).
    static void TakeHud(nint list, nint hudHandle)
    {
        int k = (lastTaken + 1) % Sheets;                                            // RA01: M23-0 - the next sheet of the rotation
        nint sheet = sheets[k];
        if (sheet == 0) return;
        ResourceBarrier* swap = stackalloc ResourceBarrier[2];
        swap[0] = new ResourceBarrier { Type = 0, Resource = hudLayer, Subresource = 0, StateBefore = StateRenderTarget, StateAfter = StateCopySource };
        swap[1] = new ResourceBarrier { Type = 0, Resource = sheet, Subresource = AllSubresources, StateBefore = StateRenderTarget, StateAfter = StateCopyDest };
        var barrier = (delegate* unmanaged<nint, uint, ResourceBarrier*, void>)originalBarrier;
        barrier(list, 2, swap);
        TextureCopyLocation to = new() { Resource = sheet, Type = 0, SubresourceIndex = 0 }, from = new() { Resource = hudLayer, Type = 0, SubresourceIndex = 0 };
        ((delegate* unmanaged<nint, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, nint, void>)Slot(list, SlotCopyTextureRegion))(list, &to, 0, 0, 0, &from, 0);
        swap[0] = new ResourceBarrier { Type = 0, Resource = hudLayer, Subresource = 0, StateBefore = StateCopySource, StateAfter = StateRenderTarget };
        swap[1] = new ResourceBarrier { Type = 0, Resource = sheet, Subresource = AllSubresources, StateBefore = StateCopyDest, StateAfter = StateRenderTarget };
        barrier(list, 2, swap);
        float* nothing = stackalloc float[4] { 0, 0, 0, 0 };
        ((delegate* unmanaged<nint, nint, float*, uint, nint, void>)originalClearRtv)(list, hudHandle, nothing, 0, 0);
        Volatile.Write(ref sheetEye[k], 0);                                           // RA01: M23-0 - its eye is learnt at this frame's Present (NoteFrame)
        Volatile.Write(ref sheetSeq[k], ++takeSeq);
        lastTaken = k;
        if (!sheetFilled) Log.Write("RA01 HUD layer: taking the finished HUD onto our sheets each frame");
        sheetFilled = true;
    }

    // RA01: M10-3a - enhanced barriers: groups (type, count, pointer at +8); texture barriers are 64 bytes (60 rounded up for the pointer) with the layouts at +16/+20 and the picture at +24.
    // Cost - CPU: extremely low (only during a capture: a pointer compare per barrier); GPU: none of its own.
    [UnmanagedCallersOnly]
    static void DetourEnhancedBarrier(nint list, uint groups, byte* group)
    {
        ((delegate* unmanaged<nint, uint, byte*, void>)listOriginal[SlotEnhancedBarrier])(list, groups, group);
        if (state != 2 || group == null) return;
        for (uint g = 0; g < groups; g++)
        {
            byte* entry = group + g * 16;
            if (*(int*)entry != 1) continue; // RA01: D3D12_BARRIER_TYPE_TEXTURE
            byte* texture = *(byte**)(entry + 8);
            for (uint i = 0; texture != null && i < *(uint*)(entry + 4); i++)
            {
                byte* t = texture + i * 64;
                nint picture = *(nint*)(t + 24);
                if (picture == 0 || (picture != hudLayer && !IsSheet(picture))) continue;
                int at = NewRecord(list, Kind.Label);
                if (at < 0) return;
                records[at].Text = $"enhanced barrier {(picture == hudLayer ? "HUD LAYER" : "our sheet")}: layout {*(int*)(t + 16)} -> {*(int*)(t + 20)}, access 0x{*(int*)(t + 8):X} -> 0x{*(int*)(t + 12):X}";
            }
        }
    }

    // RA01: M10-3a - a D3D12 resource state as a readable name for the common ones.
    // Cost - CPU: extremely low (a switch); GPU: none.
    static string StateName(int s) => s switch
    {
        0x0 => "COMMON/PRESENT", 0x4 => "RENDER_TARGET", 0x8 => "UNORDERED_ACCESS", 0x40 => "NON_PIXEL_SHADER_RESOURCE", 0x80 => "PIXEL_SHADER_RESOURCE",
        0xC0 => "SHADER_RESOURCE", 0x400 => "COPY_DEST", 0x800 => "COPY_SOURCE", _ => $"0x{s:X}",
    };

    // RA01: a pass label as text: wide (metadata 0) or plain (1) text, or the readable runs inside PIX's packed form (2).
    // Cost - CPU: extremely low (a short string, only for labelled passes during a capture); GPU: none.
    static string LabelText(uint metadata, byte* data, uint size)
    {
        if (data == null || size == 0) return "''";
        int n = (int)Math.Min(size, 256u);
        if (metadata == 0) return $"'{new string((char*)data, 0, n / 2).TrimEnd('\0')}'";
        if (metadata == 1) return $"'{Encoding.ASCII.GetString(data, n).TrimEnd('\0')}'";
        var text = new StringBuilder("(packed) '");
        for (int i = 0; i < n; i++) text.Append(data[i] >= 32 && data[i] < 127 ? (char)data[i] : data[i] == 0 ? ' ' : '.');
        return text.Append('\'').ToString();
    }

    // RA01: builds the capture's log block (background thread): every command list in the order the GPU ran it, each with its passes in the order they were recorded.
    // Cost - CPU: low (a few thousand short lines built once, one file append); GPU: none.
    static void WriteCapture()
    {
        try
        {
            int total = Math.Min(recordCount, MaxRecords), submits = Math.Min(submitCount, MaxSubmits);
            var text = new StringBuilder($"RA01 draw-pass capture: one frame - {total} records, {submits} command lists sent, {unknownHandles} targets not in the notes table; screen buffers");
            foreach (nint b in screenBuffers) if (b != 0) text.Append($" 0x{b:X}");
            var byList = new Dictionary<nint, List<int>>();
            for (int i = 0; i < total; i++) { if (!byList.TryGetValue(records[i].List, out var l)) byList[records[i].List] = l = new(); l.Add(i); }
            var done = new HashSet<nint>();
            int lines = 0;
            for (int s = 0; s < submits && lines < 6000; s++)
            {
                nint list = submitList[s];
                text.Append($"{Environment.NewLine}RA01 [{s}] list 0x{list:X} on queue 0x{submitQueue[s]:X}");
                if (!done.Add(list)) { text.Append(" (listed above)"); continue; }
                if (!byList.TryGetValue(list, out var mine)) { text.Append(" (nothing recorded)"); continue; }
                foreach (int i in mine) { text.Append(Environment.NewLine).Append("RA01     ").Append(Describe(records[i])); lines++; }
            }
            foreach (var (list, mine) in byList)
            {
                if (done.Contains(list) || lines >= 6000) continue;
                text.Append($"{Environment.NewLine}RA01 [not sent this frame] list 0x{list:X}");
                foreach (int i in mine) { text.Append(Environment.NewLine).Append("RA01     ").Append(Describe(records[i])); lines++; }
            }
            Log.Write(text.ToString());
            status = $"done - {total} records written to the log";
        }
        catch (Exception e) { status = "failed - see log"; Log.Write($"RA01 draw-pass capture: writing it failed - {e.Message}"); }
        finally { state = 0; }
    }

    // RA01: one record as a log line.
    // Cost - CPU: extremely low (one short string); GPU: none.
    static string Describe(in Record r)
    {
        string picture = r.Resource == 0 ? "unknown picture" : $"0x{r.Resource:X} {r.Width}x{r.Height} {FormatName(r.Format)}{(Array.IndexOf(screenBuffers, r.Resource) >= 0 ? " [SCREEN]" : "")}{(r.Resource == hudLayer ? " [HUD LAYER]" : "")}";
        return (Kind)r.Kind switch
        {
            Kind.Targets => $"{r.Text ?? "targets"}: RT0 {picture}{(r.Count > 1 ? $" (+{r.Count - 1} more)" : "")}{(r.Other != 0 ? ", depth" : "")}{(r.Cleared ? $", cleared to ({r.R:0.##}, {r.G:0.##}, {r.B:0.##}, {r.A:0.##})" : "")} - {r.Draws} draws, {r.Dispatches} dispatches",
            Kind.Clear => $"clear {picture} to ({r.R:0.##}, {r.G:0.##}, {r.B:0.##}, {r.A:0.##})",
            Kind.Copy => $"copy 0x{r.Other:X} -> 0x{r.Resource:X}{(r.Resource != 0 && Array.IndexOf(screenBuffers, r.Resource) >= 0 ? " [SCREEN]" : "")}",
            Kind.Reset => "(list reset)",
            _ => r.Text ?? "",
        };
    }

    // RA01: a DXGI format number as a readable name for the common ones.
    // Cost - CPU: extremely low (a switch); GPU: none.
    static string FormatName(int f) => f switch
    {
        2 => "RGBA32F", 10 => "RGBA16F", 11 => "RGBA16", 24 => "RGB10A2", 26 => "R11G11B10F", 27 => "RGBA8-typeless", 28 => "RGBA8", 29 => "RGBA8-sRGB",
        41 => "R32F", 54 => "R16F", 61 => "R8", 87 => "BGRA8", 91 => "BGRA8-sRGB", _ => $"format {f}",
    };

    // ---------------------------------------------------------------- start-up

    static int installed;

    // RA01: start-up: reads the device's and command lists' function tables from a throwaway device (the same trick PresentHook uses) and puts the permanent listeners in:
    // the handle notes (device) and the wipe / paint-target / state-change listeners (command lists). The command-list table is kept for captures. Call after PresentHook.Install.
    // Cost - CPU: medium, once (a throwaway device, tens of milliseconds); GPU: extremely low (nothing is submitted).
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        nint device = 0, allocator = 0, list = 0;
        try
        {
            Guid iidDevice = new(IidDevice), iidAllocator = new(IidAllocator), iidList = new(IidList);
            Check(D3D12CreateDevice(0, 0xB000, &iidDevice, &device), "D3D12CreateDevice");
            Check(((delegate* unmanaged<nint, int, Guid*, nint*, int>)Slot(device, SlotCreateCommandAllocator))(device, 0, &iidAllocator, &allocator), "CreateCommandAllocator");
            Check(((delegate* unmanaged<nint, uint, int, nint, nint, Guid*, nint*, int>)Slot(device, SlotCreateCommandList))(device, 0, 0, allocator, 0, &iidList, &list), "CreateCommandList");

            nint deviceTable = *(nint*)device, table = *(nint*)list;
            nint createRtv = ((nint*)deviceTable)[SlotCreateRtv], copySimple = ((nint*)deviceTable)[SlotCopyDescriptorsSimple];
            nint setTargets = ((nint*)table)[SlotSetTargets], clearRtv = ((nint*)table)[SlotClearRtv], barrier = ((nint*)table)[SlotResourceBarrier];
            if (!PresentHook.InsideD3D12(createRtv) || !PresentHook.InsideD3D12(copySimple) || !PresentHook.InsideD3D12(setTargets) || !PresentHook.InsideD3D12(clearRtv) || !PresentHook.InsideD3D12(barrier))
            {
                Log.Write("RA01 HUD layer: NOT installed - the D3D12 functions aren't inside D3D12; the HUD stays in the picture");
                return;
            }
            originalCreateRtv = createRtv; originalCopySimple = copySimple; originalSetTargets = setTargets; originalClearRtv = clearRtv; originalBarrier = barrier;
            PresentHook.Patch(deviceTable, SlotCreateRtv, (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DetourCreateRtv);
            PresentHook.Patch(deviceTable, SlotCopyDescriptorsSimple, (nint)(delegate* unmanaged<nint, uint, nint, nint, int, void>)&DetourCopySimple);
            PresentHook.Patch(table, SlotSetTargets, (nint)(delegate* unmanaged<nint, uint, nint, int, nint, void>)&DetourSetTargets);
            PresentHook.Patch(table, SlotClearRtv, (nint)(delegate* unmanaged<nint, nint, float*, uint, nint, void>)&DetourClearRtv);
            PresentHook.Patch(table, SlotResourceBarrier, (nint)(delegate* unmanaged<nint, uint, byte*, void>)&DetourResourceBarrier);
            listTable = table;
            Log.Write("RA01 HUD layer: listening (it will be recognised in game by its shape)");
        }
        catch (Exception e) { Log.Write($"RA01 HUD layer: start-up failed - {e.Message}; the HUD stays in the picture"); }
        finally { Release(list); Release(allocator); Release(device); }
    }
}
