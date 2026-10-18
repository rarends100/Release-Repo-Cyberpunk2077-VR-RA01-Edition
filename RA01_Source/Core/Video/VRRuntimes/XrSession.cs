using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RA01.Core;

// RA01: M2 - connects the game's DX12 device and queue to the headset through OpenXR and keeps the headset's frame loop alive.
// M3 - shows the game's picture on a flat virtual screen floating in front of the head (frames come from FrameCopy).
// M4b-2a - the headset's tracking gives the real floor and head height: the screen stands in the room with its bottom edge on the floor, and Home recentres it.
// M4b-2 - every frame the head's movement is handed to the game's camera (HeadPose). End switches that on or off.
// M4b-3 - the head pose handed over is predicted ahead, and the picture stands in the room at the pose the head had when the game built it (its pose tag).
// M5a - stereo by alternate eyes: the head pose handed to the game is one eye's, the left and the right in turn, and each eye shows the picture that was built for it.
// M5a-3 - the F10 settings window (UI_settingsMenus) is one more picture layer on top, both eyes see it, and it stands in the room where the head faced when it was opened.
// M5a-4 - the picture can be moved sideways and up or down (Tuning.ShiftRightDeg, ShiftUpDeg), and while the game shows a menu it has a size of its own (Tuning.GameMenuFov).
// M5a-5 - the picture-position sliders turn the picture around you (so menus are not tilted), the game menu view can be forced (F9), and a change of the game's resolution is followed live (FollowResize).
// M5a-12 - AutoFov now reads each eye's own field-of-view cone from OpenXR separately (it used to read the left eye's cone once and reuse it for both), so a squared-off picture is shaped correctly for each eye instead of the right eye borrowing the left eye's numbers.
// M5a-13 - real headset testing showed that squared-off frame (M5a-11/12) was a mistake: vertically compressed and ugly, it left the Game field of view slider with nothing to do, and it broke the menu's own sizing. AutoFov no longer changes the picture frame's shape at all (see DescribeScreen); it only auto-picks a wider Game field of view once per session, as before M5a-11's frame-shaping was added.
// a small per-eye trim (Tuning.LeftEyeShiftRightDeg etc.) can nudge one eye's picture on top of the shared ShiftRightDeg/ShiftUpDeg. All of the keys that used to adjust these values directly are gone; the F10 menu is the only way to change them now.
// all of it runs on its own thread, so the game's render threads are never blocked by the headset.
// Cost - CPU: low (one dedicated thread, about 90 headset frames a second, a few tens of microseconds of own work per frame, and it mostly sleeps inside xrWaitFrame);
// GPU: low (one copy of the game picture per eye per new picture, the runtime does the compositing).
internal static unsafe class XrSession
{
    // RA01: where the virtual screen stands. The distance is fixed for now; its size comes from the game's field of view (see DescribeScreen) and Tuning.SizeScale.
    const float ScreenDistance = 2.0f;  // RA01: metres in front of the head (or of the recentre point when the picture stands in the room with steering off)
    const float DefaultPictureFov = 68f, MinPictureFov = 55f, MaxPictureFov = 140f; // RA01: degrees; the field of view the game draws with (M5a-3: widened by Tuning.FovScale, at most 140) is limited to this for the picture's size, so aiming zooms a little and the map does not shrink the picture to nothing
    const float MaxEyeOffset = 0.15f;   // RA01: metres; an eye further than this from the head's centre is a bad answer from the runtime, not a face
    const float FovSmoothing = 0.1f;    // RA01: how much of the way to a new field of view the picture's size goes each headset frame, so it does not jump
    const float HudSmoothing = 0.08f;   // RA01: M10-1 - how much of the way to where your head points the HUD panel moves each headset frame in "Head (smooth)" follow
    const float MenuDistance = 1.0f, MenuPanelWidth = 1.6f; // RA01: M5a-3 - the F10 window's panel stands this far ahead of the head, and is this many metres wide (its height follows the panel's shape)

    static ulong instance, system, session, floorSpace, viewSpace;
    static readonly ulong[] swapchains = new ulong[2];   // RA01: one headset swapchain per eye (the left eye's is also the one used when both eyes see the same picture)
    static readonly nint[][] imageTextures = { Array.Empty<nint>(), Array.Empty<nint>() }; // RA01: the ID3D12Resource of each swapchain image, by eye and image index
    static int screenWidth, screenHeight;
    public static int RecommendedWidth, RecommendedHeight; // RA01: M5a-10 - the headset's recommended size per eye as OpenXR said it (0 until then)
    static long screenFormat;              // RA01: M5a-5 - the headset images' format, kept so the images can be made again at a new size
    static int seenSize;                   // RA01: M5a-5 - the FrameCopy.SizeVersion the headset images were last made for
    static readonly List<(ulong Chain, long DestroyAt)> oldChains = new(); // RA01: M5a-5 - swapchains of an older size, kept a moment (the runtime may still show their last image) and then destroyed
    const double OldChainSeconds = 1.0;
    static nint gameDevice, gameQueue;
    static bool running;   // RA01: true between xrBeginSession and xrEndSession, the only time frames may be submitted
    // RA01: M29-1 - for FlatMenu: is the headset's frame loop running (it then owns F10, the mouse and the settings window)? Cost - CPU: extremely low (a read); GPU: none.
    public static bool Running => Volatile.Read(ref running);
    static bool finished;  // RA01: set when the runtime asks us to quit
    static long nextPoseLog;
    static float pictureFov;        // RA01: the field of view the picture is sized for now, in degrees (0 = not set yet)
    static readonly Xr.Posef[] exactPose = new Xr.Posef[3];   // RA01: M5b-1 - per eye (1 left, 2 right): the eye pose the picture now in its image was drawn from
    static readonly float[] usedFov = new float[3];           // RA01: M5b-1 - per eye: the field of view the picture was laid out with this frame (degrees)
    static readonly bool[] exactOk = new bool[3];             // RA01: M5b-1 - per eye: this frame's picture can be given as that eye's own view (projection layer)
    static bool wasProjection;                                // RA01: M5b-1 - for the one log line when the projection layer starts or stops
    static float menuBlend;         // RA01: M5a-4 - 0 = the picture is sized for the game's camera, 1 = for a game menu (Tuning.GameMenuFov); it moves a tenth of the way each headset frame, like pictureFov
    static float pickupAgeMs;       // RA01: how old the game picture was when the headset frame last copied a new one (for the log)
    static readonly Xr.Vector3f[] eyeOffset = { new() { X = -0.032f }, new() { X = 0.032f } }; // RA01: where the left and right eye are in the head's own axes, metres (a typical IPD until the runtime says)
    static int nextEye;             // RA01: which eye the next handed-over pose is for: 0 = left, 1 = right
    // RA01: M68 - SteamVR lines both eyes of a projection layer up by one eye's pose, so the left eye (drawn ~12 ms apart from the right) doubled and blurred while
    // RA01: the head moved (Rob 2026-10-12/13, also through Steam Link - not Virtual Desktop's doing). On SteamVR the two exact per-eye panels DescribeScreen already
    // RA01: builds are shown instead: each stands where its eye was when the picture was drawn, at the picture's true size, seen by that eye only - the same view as
    // RA01: the projection layer's slot (one frustum), but as its own object in the room, so SteamVR places each eye from its own pose. Like two separate framed
    // RA01: photos hung each at its own camera spot instead of one double frame hung from one nail. Other runtimes keep the projection layer.
    static bool panelEyes;

    static readonly int[] freshPictures = new int[3], missedFrames = new int[3]; // RA01: for the log, by eye (0 = both eyes see one picture, 1 = left, 2 = right): new pictures taken, and headset frames when no picture built for that eye was to be found
    static readonly long[] lastPicture = new long[3]; // RA01: the grab time of the picture that is in each eye's newest swapchain image (0 = none that can be shown)
    static readonly long[] lastStamp = new long[3];   // RA01: M5a-2 - the pose stamp of that picture (which pose and eye it was built from), to keep it standing in the room while it is shown again
    static bool wasStereo;          // RA01: whether the last frame showed one picture per eye
    static long statsSince = Stopwatch.GetTimestamp(); // RA01: when those counts were last written to the log (or when the mod started)
    static bool predicting;         // RA01: true when the head handed to the game on the last frame was the predicted one (false = the runtime would not predict, or prediction is set to 0)
    static string pictureMode = "no picture yet";
    static bool recentreWanted;     // RA01: set by Home, by the runtime's own recentre and once by the first Focused state; cleared when the recentre has happened
    static long recentreAt;         // RA01: the runtime announced a move of the floor space for this time (0 = none announced), the recentre waits for it
    static long lastDisplayTime;    // RA01: the newest predicted display time, to say how far ahead an announced move is
    static int floorSpaceType;      // RA01: STAGE or LOCAL, whichever floorSpace is
    static bool waitingLogged;      // RA01: keeps the "look straight ahead" note to one line per wish
    static bool locateFailLogged;   // RA01: keeps a failing head lookup to one log line until it works again
    static bool autoRecentreDone;   // RA01: the automatic recentre happens only the first time the headset is worn and focused
    static ulong hudChain;              // RA01: M10-1 - the HUD panel's headset swapchain (0 = none yet)
    static nint[] hudTextures = Array.Empty<nint>();
    static int hudChainVersion, hudChainWidth, hudChainHeight;
    static bool[] hudFilled = Array.Empty<bool>(); // RA01: M10-2 - which panel images have had a HUD copied in (a new image holds leftover video memory until then)
    static bool flatWas;                // RA01: M23-2 - the last flat-at-device answer, so each change is logged once
    static ulong hudChain2;             // RA01: M23-0 - the RIGHT eye's panel (per-eye HUD); hudChain is then the left eye's (0 = none yet)
    static nint[] hudTextures2 = Array.Empty<nint>();
    static bool[] hudFilled2 = Array.Empty<bool>();
    static bool hudFailed, hudAimed;    // RA01: M10-1 - failed: the panel can't be made this session; aimed: the smoothed direction has been set once
    static float hudYaw, hudPitch;      // RA01: M10-1 - the panel's smoothed direction in "Head (smooth)" follow, radians
    static bool johnnyNoCentreWas;      // RA01: M54-4 - the centre square was last left out for Johnny (for the log)
    static ulong aimChain;              // RA01: M13-61 - the crosshair's own small swapchain (the HUD's centre square), 0 = none yet
    static nint[] aimTextures = Array.Empty<nint>();
    static bool[] aimFilled = Array.Empty<bool>();
    static int aimSide;                 // RA01: its side in pixels (CrosshairShare of the HUD's height, even)
    const int CutsceneTier = 3;           // RA01: M21-10 - the game's scene tier from which the centre is not cut out (M21-12: 3, was 4 - a shard video handed to V still had the hole
                                          // RA01: (Rob 2026-10-07), most likely a tier-3 'limited' dialogue scene; V can't aim in those anyway; 4-5 are cinematics. The log line below shows the tier)
    static bool splitWas = true;          // RA01: M21-12 - the cut-out's last on / off, so each change is logged once with its reason
    const float CrosshairShare = 1f / 6f; // RA01: M13-61 - the centre square cut out for the crosshair: a sixth of the HUD's height (room for Smart and shotgun reticles)
    static float reticleX, reticleY = 1f, reticleZ; // RA01: M13-47 - which way the HUD's centre (where the game draws its crosshair) lies from your head, in the view's own axes
    static ulong menuChain, menuSpace;  // RA01: M5a-3 - the F10 window's headset swapchain (0 = none, no window), and the space its pose is in
    static nint[] menuTextures = Array.Empty<nint>(); // RA01: the ID3D12Resource of each of its images
    static Xr.Posef menuPose;       // RA01: where the F10 window stands (set when it opens)
    static bool menuShown;          // RA01: the swapchain holds a painted window picture that can be shown
    static bool menuPlaceWanted;    // RA01: the window has just been opened and must be put where the head faces
    static bool menuFailed;         // RA01: the window could not be painted, so it is not offered again
    static bool swapSeen;           // RA01: the eyes' swap as of the last look, to notice a change made by a key or by the F10 window
    static readonly ulong[] lensChains = new ulong[2];   // RA01: M16-A - the scope lens's own small swapchain per eye side (0 = not made yet), ScopeLens.MaxSide square
    static readonly nint[][] lensTextures = { Array.Empty<nint>(), Array.Empty<nint>() };
    static readonly ScopeLens.Cutout[] lensCut = new ScopeLens.Cutout[3]; // RA01: M16-A - by eye (0 both, 1 left, 2 right): the square in that eye's lens image now (Side 0 = no lens)
    static bool lensFailed;                                // RA01: M16-A - the lens swapchain couldn't be made: no magnifier this session
    static ulong stripChain;                               // RA01: M24-4 - the hand strip's (laser's) small swapchain (0 = not made yet), HandStrip's 128 x 64 image
    static nint[] stripTextures = Array.Empty<nint>();
    static int stripLook = -1;                             // RA01: M24-4 - HandStrip.Look the image was last painted for (-1 = never painted)
    static ulong pouchChain;                               // RA01: M27-5c - the mag pouch glow's small swapchain (0 = not made yet), PouchGlow's 64 x 64 image
    static nint[] pouchTextures = Array.Empty<nint>();
    static int pouchLook = -1;                             // RA01: M27-5c - PouchGlow.Look the image was last painted for (-1 = never painted)
    static bool pouchFailed;                               // RA01: M27-5c - it couldn't be made or painted: no pouch glow this session
    static ulong dotChain;                                 // RA01: M76 - the unarmed dot's small swapchain (0 = not made yet), UnarmedDot's 32 x 32 image
    static nint[] dotTextures = Array.Empty<nint>();
    static bool dotPainted, dotFailed, dotWas;             // RA01: M76 - painted once; failed = no dot this session; was = shown last frame (logged on a change)
    static bool stripFailed;                               // RA01: M24-4 - the strip couldn't be made or painted: no laser this session
    static ulong bannerChain;                              // RA01: M27-1 - the banner's swapchain (0 = not made yet)
    static nint[] bannerTextures = Array.Empty<nint>();
    static int bannerVersion = -1;                         // RA01: M27-1 - Banner's version the image holds (-1 = none)
    static bool bannerFailed;

    // RA01: M5a-11 - the headset's own field of view cone, in radians, as OpenXR reports it (left and down are negative, right and up are positive; a rectangle of view, not a single angle). Read once and kept, since it does not change while the headset is worn.
    // RA01: M5a-12 - kept per eye now (index 0 = left, 1 = right, the same as eyeOffset), because a real headset's two eyes are mirror images of each other, not the same cone twice.
    static readonly float[] headsetFovL = new float[2], headsetFovR = new float[2], headsetFovU = new float[2], headsetFovD = new float[2]; // RA01: M5a-13 - kept for the "RA01 headset field of view:" log line only now; nothing shapes the picture frame from these any more
    // RA01: M5a-13 - only the left/right edges of the cone that covers both eyes are kept: TryAutoFovScale is the only thing left that reads them, to work out how wide to make the game's own field of view.
    static float headsetFovBothL, headsetFovBothR;
    static float fitTanX, fitTanY;  // RA01: M5b-2 - the widest edges of both eyes' cones, turned into the head's own directions: how far out (right/left) and up/down the game must reach, as slopes (tangents)
    static float fitFov;            // RA01: M5b-2 - see FitFov
    static bool headsetFovKnown;    // RA01: M5a-11 - true once the angles above have been read successfully
    static bool autoFovScaleDone;   // RA01: M5a-11 - true once Tuning.FovScale has been auto-picked for this session (it is then left alone, so the field of view slider still works normally on top of it)

    // RA01: called once by PresentHook. The device and queue stay referenced by the caller for the whole game run.
    // Cost - CPU: extremely low (runs once, only stores two handles and starts one thread); GPU: none.
    public static void Start(nint device, nint queue)
    {
        gameDevice = device;
        gameQueue = queue;
        new Thread(Run) { IsBackground = true, Name = "RA01 OpenXR" }.Start();
    }

    // RA01: the body of the OpenXR thread: load the saved settings, connect, make the session and the headset screen, then poll events and run one headset frame per turn until the runtime says stop, then clean up.
    // Cost - CPU: low (the loop itself is trivial and about 90 turns a second, the per-frame work is in RunFrame; while waiting for the runtime it sleeps 50 ms per turn); GPU: none of its own.
    static void Run()
    {
        try
        {
            SettingsFile.Load(); // RA01: M5a-2 - the values saved by an earlier run, before anything uses them
            Connect();
            CreateSession();
            CreateHeadsetScreen();
            while (!finished)
            {
                PollEvents();
                if (GameQuitting()) { Log.Write("RA01 OpenXR: the game is quitting (its window is gone and no frames came for a moment) - closing the VR session first (M49-1)"); break; }
                if (running) RunFrame();
                else Thread.Sleep(50); // RA01: waiting for the runtime to say Ready
            }
            Log.Write("RA01 OpenXR finished");
        }
        catch (Exception e)
        {
            Log.Write($"RA01 OpenXR stopped: {e.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    // RA01: M49-1 - the quit check's notes (see GameQuitting).
    const long QuitConfirmMs = 300;
    static long windowGoneAt, framesWhenGone;
    // RA01: M49-1 - THE GAME IS QUITTING (Rob approved 2026-10-11): the crash on quit was the VR runtime's own thread (Virtual Desktop's LibOVRRT, in Direct3D 11
    // and the driver) still working on our open session while the game tore itself down - Cleanup only ran when the RUNTIME said stop. The quit watch
    // (M49-0, 09:05) showed the game window goes first, frames stop, and the crash follows 1-3 s later. So: once the game's window is gone AND no new frame
    // has been presented for QuitConfirmMs (a window rebuilt for a display change keeps the frames coming, so it never counts), the loop ends and the
    // usual Cleanup runs - copies stopped, headset images handed back, the session and the runtime connection closed - before the game pulls the floor
    // out. Builder picture: the forklift parks and switches off as the lights start going out. Before that moment, nothing changes.
    // Cost - CPU: extremely low (one Windows call and a compare per loop turn, about 90 a second); GPU: none.
    static bool GameQuitting()
    {
        nint window = FrameCopy.GameWindow;
        if (window == 0 || Win32.IsWindow(window)) { windowGoneAt = 0; return false; }
        long now = Environment.TickCount64, frames = PresentHook.Frames;
        if (windowGoneAt == 0) { windowGoneAt = now; framesWhenGone = frames; return false; }
        if (frames != framesWhenGone) { windowGoneAt = now; framesWhenGone = frames; return false; }   // RA01: still presenting - not a quit
        return now - windowGoneAt >= QuitConfirmMs;
    }

    // RA01: step 1 - load the loader, open an OpenXR instance, find the headset and ask what the runtime needs from DX12.
    // Cost - CPU: medium (runs once at startup: loading the OpenXR loader and the runtime and creating the instance can take tens to hundreds of milliseconds, then a few queries and log lines); GPU: none.
    static void Connect()
    {
        string loaderPath = Path.Combine(OwnFolder(), "openxr_loader.dll");
        nint loader = Win32.LoadLibraryW(loaderPath);
        if (loader == 0) throw new InvalidOperationException($"could not load {loaderPath} (Windows error {Marshal.GetLastPInvokeError()}), is openxr_loader.dll next to RA01.dll?");
        Xr.Load(loader);

        Xr.InstanceCreateInfo info = default;
        info.Type = Xr.TypeInstanceCreateInfo;
        Xr.SetText(info.ApplicationInfo.ApplicationName, 128, "RA01 Cyberpunk VR");
        Xr.SetText(info.ApplicationInfo.EngineName, 128, "RA01");
        info.ApplicationInfo.ApiVersion = Xr.MakeVersion(1, 0, 0);
        bool hp = Offered(Xr.ExtensionHpController);       // RA01: M50 - HP Reverb G2 controller bindings need this extension; only switched on when the runtime offers it
        bool frame = Offered(Xr.ExtensionFrameController); // RA01: M50-2 - the same for the Steam Frame controllers
        fixed (byte* extension = "XR_KHR_D3D12_enable\0"u8, hpExtension = "XR_EXT_hp_mixed_reality_controller\0"u8, frameExtension = "XR_VALVE_frame_controller_interaction\0"u8)
        {
            byte** extensionNames = stackalloc byte*[3];
            uint extensionCount = 0;
            extensionNames[extensionCount++] = extension;
            if (hp) extensionNames[extensionCount++] = hpExtension;
            if (frame) extensionNames[extensionCount++] = frameExtension;
            info.EnabledExtensionCount = extensionCount; // RA01: M50 - neither offered: D3D12 alone, exactly as before
            info.EnabledExtensionNames = extensionNames;
            ulong handle;
            Check(Xr.CreateInstance(&info, &handle), "xrCreateInstance (-51 means no active OpenXR runtime, set SteamVR as the OpenXR runtime)");
            instance = handle;
        }
        Controllers.HpExtension = hp;
        Controllers.FrameExtension = frame;
        Log.Write($"RA01 OpenXR: controller extensions - HP Reverb G2 {(hp ? "offered, switched on" : "not offered")}, Steam Frame {(frame ? "offered, switched on" : "not offered")} (M50)");
        Xr.LoadExtensions(instance);

        Xr.InstanceProperties properties = default;
        properties.Type = Xr.TypeInstanceProperties;
        Check(Xr.GetInstanceProperties(instance, &properties), "xrGetInstanceProperties");
        ulong v = properties.RuntimeVersion;
        Log.Write($"RA01 OpenXR runtime: {Xr.GetText(properties.RuntimeName)} {v >> 48}.{(v >> 32) & 0xFFFF}.{v & 0xFFFFFFFF}");
        // RA01: RUNTIME FINDINGS (Rob's tests, 2026-10-12/13, Quest 3) - what each OpenXR runtime does with RA01's pictures, and why the code below differs per runtime:
        // RA01:  - RA01 draws the eyes in turn (left, right, left...), so the two pictures in a frame come from moments about 12 ms apart. Each is handed over with
        // RA01:    the exact pose it was drawn from (M63: 100% exact pairing) and the picture's own symmetric field of view - what OpenXR asks for.
        // RA01:  - VDXR (Virtual Desktop's own runtime): honours each eye's pose in the projection layer - clean. SteamVR isn't started at all.
        // RA01:  - Meta (Quest Link / Air Link, oculus_openxr_64.json): honours each eye's pose too - clean ("works perfectly", 02:29).
        // RA01:  - SteamVR (through Virtual Desktop AND through Steam Link - so SteamVR itself, not the streamer): lines BOTH eyes of a projection layer up by ONE
        // RA01:    eye's pose, so the left eye doubled and blurred whenever the head moved (still head and stick turns were fine). It does place separate panels
        // RA01:    (quads) each by its own pose, so on SteamVR each eye is shown as its own exact panel (M68, panelEyes below) - same view, one frame per eye.
        // RA01:    Tried and dropped first: drawing both eyes from one head moment (M64 - one eye always a frame behind: a steady "ghost hand"), two half-blank
        // RA01:    projection layers (Option C - depends on the game picture's alpha). Ruled out: SteamVR Motion Smoothing, Desktop Game Theatre, the OBS Mirror
        // RA01:    and Virtual Desktop compatibility API layers, depth submission (RA01 sends none), XR_MSFT_composition_layer_reprojection (no runtime offers it).
        // RA01:  - SteamVR takes the keyboard from the game's window as it starts, and again shortly after -> Refocus (M67/M67-2) hands it back, up to 3 times.
        // RA01:  - Meta Link + the game started THROUGH STEAM: stack overflow at RA01's start-up (Steam's overlay, Meta's runtime and the HUD layer hooking the
        // RA01:    same D3D12 functions) -> the launcher always starts the game directly while Meta's runtime is active (M70, Launcher\Program.cs).
        // RA01:  - Not tested yet: Pimax, Varjo. Windows Mixed Reality is gone from Windows 11 24H2. If another runtime doubles one eye, add its name to panelEyes.
        // RA01:  - Every start logs the whole VR setup (extensions, active runtime, API layers, VR programs, SteamVR settings) - RuntimeReport, M66.
        if (OperatingSystem.IsWindows()) RuntimeReport.Write();          // RA01: M66 - the VR setup around RA01, log only
        panelEyes = Xr.GetText(properties.RuntimeName).Contains("SteamVR", StringComparison.OrdinalIgnoreCase); // RA01: M68
        if (panelEyes) Log.Write("RA01 SteamVR: each eye shown as its own exact panel instead of one projection layer (M68)");

        Xr.SystemGetInfo getInfo = new() { Type = Xr.TypeSystemGetInfo, FormFactor = Xr.FormFactorHeadMountedDisplay };
        ulong systemId;
        Check(Xr.GetSystem(instance, &getInfo, &systemId), "xrGetSystem (is the headset connected and SteamVR running?)");
        system = systemId;

        Xr.SystemProperties systemProperties = default;
        systemProperties.Type = Xr.TypeSystemProperties;
        Check(Xr.GetSystemProperties(instance, system, &systemProperties), "xrGetSystemProperties");
        Log.Write($"RA01 OpenXR headset: '{Xr.GetText(systemProperties.SystemName)}' vendor 0x{systemProperties.VendorId:X}, "
            + $"max image {systemProperties.MaxSwapchainImageWidth}x{systemProperties.MaxSwapchainImageHeight}, "
            + $"orientation tracking {systemProperties.OrientationTracking}, position tracking {systemProperties.PositionTracking}");
        AutoTune.Detect(Xr.GetText(systemProperties.SystemName)); // RA01: M24-2 - the Video tab's Auto tune starts on this headset

        // RA01: OpenXR requires this call before a DX12 session. The answer is logged for the record and used by the runtime itself.
        Xr.GraphicsRequirementsD3D12 requirements = default;
        requirements.Type = Xr.TypeGraphicsRequirementsD3D12;
        Check(Xr.GetD3D12GraphicsRequirements(instance, system, &requirements), "xrGetD3D12GraphicsRequirementsKHR");
        Log.Write($"RA01 OpenXR wants adapter LUID {requirements.AdapterLuidHighPart:X8}:{requirements.AdapterLuidLowPart:X8}, minimum feature level 0x{requirements.MinFeatureLevel:X}");

        Xr.ViewConfigurationView* views = stackalloc Xr.ViewConfigurationView[2];
        views[0] = new() { Type = Xr.TypeViewConfigurationView };
        views[1] = new() { Type = Xr.TypeViewConfigurationView };
        uint count;
        Check(Xr.EnumerateViewConfigurationViews(instance, system, Xr.ViewConfigurationPrimaryStereo, 2, &count, views), "xrEnumerateViewConfigurationViews");
        if (count > 0) { RecommendedWidth = (int)views[0].RecommendedImageRectWidth; RecommendedHeight = (int)views[0].RecommendedImageRectHeight; Tuning.HeadsetWidth = RecommendedWidth; Tuning.HeadsetHeight = RecommendedHeight; } // RA01: M12-2 - also remembered in RA01_Settings.ini for the next start
        for (int i = 0; i < count && i < 2; i++)
            Log.Write($"RA01 OpenXR eye {i}: recommended {views[i].RecommendedImageRectWidth}x{views[i].RecommendedImageRectHeight}, "
                + $"max {views[i].MaxImageRectWidth}x{views[i].MaxImageRectHeight}, samples {views[i].RecommendedSwapchainSampleCount}");
    }

    // RA01: M50 - true if the runtime offers this OpenXR extension (xrEnumerateInstanceExtensionProperties, asked before the instance exists). Any failure
    // (the call missing, an error, an odd count) counts as "not offered", so start-up goes on exactly as before.
    // Cost - CPU: extremely low (two runtime calls and a walk over a few dozen names, once at start-up); GPU: none.
    static bool Offered(string name)
    {
        try
        {
            if (Xr.EnumerateInstanceExtensionProperties == null) return false;
            uint count;
            if (Xr.EnumerateInstanceExtensionProperties(null, 0, &count, null) < 0 || count == 0 || count > 1024) return false;
            Xr.ExtensionProperties[] list = new Xr.ExtensionProperties[count];
            for (int i = 0; i < list.Length; i++) list[i].Type = Xr.TypeExtensionProperties;
            fixed (Xr.ExtensionProperties* first = list)
            {
                if (Xr.EnumerateInstanceExtensionProperties(null, count, &count, first) < 0) return false;
                for (int i = 0; i < count && i < list.Length; i++)
                    if (Xr.GetText(first[i].ExtensionName) == name) return true;
            }
        }
        catch { }
        return false;
    }

    // RA01: step 2 - open the session on the game's own device and queue, and create the reference spaces:
    // the floor space (STAGE, whose origin is on the floor; LOCAL when the runtime has no STAGE) and VIEW (moves with the head).
    // Cost - CPU: medium (runs once: xrCreateSession makes the runtime set up its compositor on the game's device, which can take some milliseconds);
    // GPU: low (the runtime makes its own resources on the game's device once).
    static void CreateSession()
    {
        Xr.GraphicsBindingD3D12 binding = new() { Type = Xr.TypeGraphicsBindingD3D12, Device = gameDevice, Queue = gameQueue };
        Xr.SessionCreateInfo sessionInfo = new() { Type = Xr.TypeSessionCreateInfo, Next = &binding, SystemId = system };
        ulong sessionHandle;
        Check(Xr.CreateSession(instance, &sessionInfo, &sessionHandle), "xrCreateSession");
        session = sessionHandle;
        Log.Write($"RA01 OpenXR session created on device 0x{gameDevice:X}, queue 0x{gameQueue:X}");

        bool hasStage = OfferedSpaces();
        floorSpaceType = hasStage ? Xr.ReferenceSpaceStage : Xr.ReferenceSpaceLocal;
        floorSpace = CreateSpace(floorSpaceType);
        HeadPose.FloorAssumed = !hasStage; // RA01: LOCAL has no floor, so the first recentre estimates it
        if (!hasStage) Log.Write($"RA01 OpenXR: no STAGE space, so the floor will be estimated as {HeadPose.AssumedEyeHeight} m below the head at the first recentre");
        viewSpace = CreateSpace(Xr.ReferenceSpaceView); // RA01: the head itself, located inside the floor space each frame
        Controllers.Setup(instance, session, floorSpace); // RA01: M6-0 - the motion controller action set; a failure here only turns off controller tracking, it never stops the headset picture (see Controllers.Setup)
    }

    // RA01: asks the runtime which reference spaces it supports, logs them, and says whether STAGE is one of them.
    // if the runtime cannot answer, that counts as "no STAGE" and the LOCAL fallback is used.
    // Cost - CPU: extremely low (runs once at startup: one runtime call and a short loop over at most 16 entries plus one log line); GPU: none.
    static bool OfferedSpaces()
    {
        int* types = stackalloc int[16];
        uint count;
        int result = Xr.EnumerateReferenceSpaces(session, 16, &count, types);
        if (result < 0)
        {
            Log.Write($"RA01 OpenXR: xrEnumerateReferenceSpaces failed ({result})");
            return false;
        }
        string names = "";
        bool stage = false;
        for (uint i = 0; i < count && i < 16; i++) // RA01: never read past the 16 entries there is room for
        {
            names += (i > 0 ? ", " : "") + SpaceName(types[i]);
            if (types[i] == Xr.ReferenceSpaceStage) stage = true;
        }
        Log.Write($"RA01 OpenXR reference spaces offered: {names}");
        return stage;
    }

    // RA01: the readable name of a reference space type, or just its number when it is not in the list. Used only for log lines.
    // Cost - CPU: extremely low (one array lookup, only when a log line is written, rarely); GPU: none.
    static string SpaceName(int type) => type >= 0 && type < Xr.ReferenceSpaceNames.Length ? Xr.ReferenceSpaceNames[type] : type.ToString();

    // RA01: makes one reference space of the given type (no offset, no rotation) and returns its handle.
    // Cost - CPU: extremely low (runs twice at startup, one small runtime call each); GPU: none.
    static ulong CreateSpace(int type)
    {
        Xr.ReferenceSpaceCreateInfo spaceInfo = new() { Type = Xr.TypeReferenceSpaceCreateInfo, ReferenceSpaceType = type };
        spaceInfo.PoseInReferenceSpace.Orientation.W = 1; // RA01: no rotation, no offset
        ulong handle;
        Check(Xr.CreateReferenceSpace(session, &spaceInfo, &handle), "xrCreateReferenceSpace");
        return handle;
    }

    // RA01: step 3 (M3, M5a) - a headset swapchain per eye, the size of the game's back buffer, plus the mirror textures that feed them (see FrameCopy).
    // if anything is not possible the reason is logged and the headset simply keeps getting empty frames, like in M2.
    // Cost - CPU: medium (runs once at startup: creates two swapchains and the mirror textures, a few milliseconds); GPU: medium (allocates the video memory for several game-picture-sized images per eye, once).
    static void CreateHeadsetScreen()
    {
        try
        {
            if (!FrameCopy.Prepare(gameDevice, gameQueue, out int width, out int height)) return;

            long* offered = stackalloc long[128];
            uint offeredCount;
            Check(Xr.EnumerateSwapchainFormats(session, 128, &offeredCount, offered), "xrEnumerateSwapchainFormats");
            string names = "";
            for (uint i = 0; i < offeredCount && i < 128; i++)
                names += (i > 0 ? ", " : "") + FrameCopy.FormatName(offered[i]);
            Log.Write($"RA01 OpenXR swapchain formats offered: {names}");

            long format = FrameCopy.PickHeadsetFormat(offered, Math.Min(offeredCount, 128u));
            if (format == 0)
            {
                FrameCopy.Disable("the headset offers no image format that matches the game's back buffer");
                return;
            }

            for (int side = 0; side < 2; side++) swapchains[side] = CreateSwapchain(format, width, height, out imageTextures[side]);
            screenWidth = width;
            screenHeight = height;
            screenFormat = format;
            Log.Write($"RA01 OpenXR screen: {width}x{height} {FrameCopy.FormatName(format)}, {imageTextures[0].Length} images per eye (one swapchain for each eye), shown {ScreenDistance} m away and sized from the game's field of view");
            FrameCopy.Enable();
            CreateMenu(format);
        }
        catch (Exception e)
        {
            FrameCopy.Disable($"headset screen setup failed: {e.Message}");
        }
    }

    // RA01: M5a-3 - the F10 window's own swapchain, the size of its panel and in the same format as the game's picture. A failure here only means there is no window, the game's picture is not affected.
    // Cost - CPU: low (runs once at startup: one small swapchain and its upload buffer); GPU: low (video memory for one small panel-sized image set, once).
    static void CreateMenu(long format)
    {
        try
        {
            if (!FrameCopy.PrepareMenu(SettingsMenu.PanelWidth, SettingsMenu.PanelHeight, format)) return;
            menuChain = CreateSwapchain(format, SettingsMenu.PanelWidth, SettingsMenu.PanelHeight, out menuTextures);
            Log.Write($"RA01 menu: the F10 window is ready ({SettingsMenu.PanelWidth}x{SettingsMenu.PanelHeight} panel, {menuTextures.Length} images, {MenuDistance} m ahead of where you face when you open it)");
        }
        catch (Exception e)
        {
            menuChain = 0;
            Log.Write($"RA01 menu: the F10 window is not available ({e.Message})");
        }
    }

    // RA01: one headset swapchain of the given size and format, and the ID3D12Resource of each of its images.
    // Cost - CPU: medium for the single call (the runtime creates the images, a few milliseconds), but rare: at startup for the two eyes and the window, and again when the game's resolution changes;
    // GPU: medium (allocates several images in video memory, once per call).
    static ulong CreateSwapchain(long format, int width, int height, out nint[] textures)
    {
        Xr.SwapchainCreateInfo createInfo = new()
        {
            Type = Xr.TypeSwapchainCreateInfo, UsageFlags = Xr.SwapchainUsageColorAttachment | Xr.SwapchainUsageSampled, Format = format,
            SampleCount = 1, Width = (uint)width, Height = (uint)height, FaceCount = 1, ArraySize = 1, MipCount = 1
        };
        ulong handle;
        Check(Xr.CreateSwapchain(session, &createInfo, &handle), "xrCreateSwapchain");
        try
        {
            uint imageCount;
            Check(Xr.EnumerateSwapchainImages(handle, 0, &imageCount, null), "xrEnumerateSwapchainImages (count)");
            Xr.SwapchainImageD3D12* images = stackalloc Xr.SwapchainImageD3D12[(int)imageCount];
            for (int i = 0; i < imageCount; i++)
                images[i] = new() { Type = Xr.TypeSwapchainImageD3D12 };
            Check(Xr.EnumerateSwapchainImages(handle, imageCount, &imageCount, images), "xrEnumerateSwapchainImages");

            textures = new nint[imageCount];
            for (int i = 0; i < imageCount; i++)
                textures[i] = images[i].Texture;
            return handle;
        }
        catch
        {
            Xr.DestroySwapchain(handle); // RA01: M5a-5 - a swapchain whose images cannot be listed is of no use, and nothing else knows its handle: give it back (a resize can ask for new ones many times in a session)
            throw;
        }
    }

    // RA01: the runtime reports session changes as events. Ready means begin, Stopping means end, Exiting means we are done.
    // Cost - CPU: extremely low (runs every turn of the thread, about 90 times a second, and nearly always finds no event: one runtime call and a return); GPU: none.
    static void PollEvents()
    {
        Xr.EventDataBuffer buffer;
        while (true)
        {
            buffer = default;
            buffer.Type = Xr.TypeEventDataBuffer;
            int result = Xr.PollEvent(instance, &buffer);
            if (result == Xr.EventUnavailable) return;
            Check(result, "xrPollEvent");

            if (buffer.Type == Xr.TypeEventDataSessionStateChanged)
                OnSessionState(((Xr.EventDataSessionStateChanged*)&buffer)->State);
            else if (buffer.Type == Xr.TypeEventDataReferenceSpaceChangePending)
                OnSpaceChange((Xr.EventDataReferenceSpaceChangePending*)&buffer);
            else if (buffer.Type == Xr.TypeEventDataInstanceLossPending)
            {
                Log.Write("RA01 OpenXR runtime is going away (instance loss pending)");
                finished = true;
            }
        }
    }

    // RA01: the runtime is about to move one of its reference spaces: the user held the Meta button (its own recentre) or changed the room setup.
    // when it is our floor space, that counts as a recentre (as if Home had been pressed) once the announced time has come, because before then
    // the head is still measured in the old space. The runtime announces LOCAL and STAGE together, so only the one we use counts.
    // Cost - CPU: extremely low (runs only when the runtime announces a floor move, a few times a session at most: some arithmetic and one log line); GPU: none.
    static void OnSpaceChange(Xr.EventDataReferenceSpaceChangePending* change)
    {
        if (change->ReferenceSpaceType == floorSpaceType) recentreAt = change->ChangeTime == 0 ? 1 : change->ChangeTime;
        string move = "no movement given";
        if (change->PoseValid != 0)
        {
            Xr.Vector3f p = change->PoseInPreviousSpace.Position;
            move = $"new origin at ({p.X:F2}, {p.Y:F2}, {p.Z:F2}) m in the old space";
            if (HeadPose.YawOf(change->PoseInPreviousSpace.Orientation, out float yaw)) move += $", turned {yaw * (180f / MathF.PI):F0} degrees to the left";
        }
        string when = lastDisplayTime == 0 || change->ChangeTime <= lastDisplayTime ? "" : $", in {(change->ChangeTime - lastDisplayTime) / 1_000_000} ms"; // RA01: the times are in nanoseconds
        Log.Write($"RA01 OpenXR: the runtime will move its {SpaceName(change->ReferenceSpaceType)} space ({move}{when}){(change->ReferenceSpaceType == floorSpaceType ? ", the head will be recentred then" : "")}");
    }

    // RA01: M67 - SteamVR takes the keyboard and mouse from the game's window when it starts (Rob 00:34: the controls were dead until he clicked the window).
    // Starting RefocusDelayMs after the session first gets focus and for RefocusWindowMs, once a frame: if some other window is in front, the game's window is
    // brought back to the front - like handing the steering wheel back to the driver after a passenger grabbed it. M67-2 - Rob 01:12: SteamVR took it again
    // after the first refocus (0.5 s in, logged as done), so it waits 2 s first and may hand it back up to MaxRefocus times, at least RefocusGapMs apart; then
    // never again that session, so it can't fight a deliberate Alt+Tab for long.
    // Windows only lets the front window change from the thread that has the input, so that thread's input is borrowed for the moment of the switch.
    // Cost - CPU: extremely low (one Windows call a frame for those few seconds; the switch itself a few calls, once); GPU: none.
    static void Refocus()
    {
        if (refocusUntil == 0) return;
        long now = Environment.TickCount64;
        if (now > refocusUntil || refocusCount >= MaxRefocus) { refocusUntil = 0; return; }
        if (now < refocusFrom) return;
        nint game = FrameCopy.GameWindow, front = Win32.GetForegroundWindow();
        if (game == 0 || front == 0 || front == game || !Win32.IsWindow(game)) return;
        refocusFrom = now + RefocusGapMs;
        refocusCount++;
        uint frontThread = Win32.GetWindowThreadProcessId(front, 0), me = Win32.GetCurrentThreadId();
        bool attached = frontThread != 0 && frontThread != me && Win32.AttachThreadInput(me, frontThread, true);
        Win32.BringWindowToTop(game);
        bool ok = Win32.SetForegroundWindow(game);
        if (attached) Win32.AttachThreadInput(me, frontThread, false);
        Log.Write(ok ? $"RA01 game window refocused ({refocusCount} of at most {MaxRefocus}) - another window had taken the keyboard when the VR session started (M67)"
                     : "RA01 game window: another window took the keyboard when the VR session started and Windows would not hand it back - click the game window (M67)");
    }
    static long refocusUntil, refocusFrom;
    static int refocusCount;
    const long RefocusDelayMs = 2000, RefocusWindowMs = 20000, RefocusGapMs = 1000; // RA01: M67-2
    const int MaxRefocus = 3;

    // RA01: reacts to the runtime's session state: Ready begins the session, Focused asks for the first recentre, Stopping ends it (and closes the F10 window), Exiting or loss pending ends the thread's loop.
    // Cost - CPU: extremely low (runs a few times per session, when the state changes: one log line and at most one begin or end call); GPU: none.
    static void OnSessionState(int state)
    {
        Log.Write($"RA01 OpenXR session state: {(state >= 0 && state < Xr.StateNames.Length ? Xr.StateNames[state] : state.ToString())}");
        switch (state)
        {
            case Xr.StateReady:
                Xr.SessionBeginInfo begin = new() { Type = Xr.TypeSessionBeginInfo, PrimaryViewConfigurationType = Xr.ViewConfigurationPrimaryStereo };
                Check(Xr.BeginSession(session, &begin), "xrBeginSession");
                running = true;
                break;
            case Xr.StateFocused:
                if (!autoRecentreDone) { autoRecentreDone = true; recentreWanted = true; } // RA01: the game has input focus, a good moment for a fresh centre (Home can redo it any time)
                if (refocusUntil == 0 && refocusCount == 0) { refocusFrom = Environment.TickCount64 + RefocusDelayMs; refocusUntil = refocusFrom + RefocusWindowMs; } // RA01: M67 - watch for the runtime taking the keyboard from the game
                break;
            case Xr.StateStopping:
                running = false;
                Keys.Forget(); // RA01: the keys are not polled while stopped, so forget their old state
                if (SettingsMenu.Open) { SettingsMenu.Toggle(); MenuInput.Stop(); } // RA01: M5a-3 - the mouse goes back to the game while the headset is not rendering
                recentreAt = 0;
                Check(Xr.EndSession(session), "xrEndSession");
                break;
            case Xr.StateExiting:
            case Xr.StateLossPending:
                finished = true;
                break;
        }
    }

    // RA01: one headset frame: wait for the runtime's timing, begin, read the head pose (and recentre if asked), and end with the virtual screen as the only layer.
    // when there is no picture to show yet, the frame ends with no layers (empty), like in M2.
    // Cost - CPU: low (about 90 times a second: a dozen OpenXR calls and some arithmetic, tens of microseconds, not counting the sleep inside xrWaitFrame);
    // GPU: low (submits at most one game-picture copy per eye, the runtime composites the quads).
    static void RunFrame()
    {
        PollKeys();
        Refocus();                                  // RA01: M67
        MenuSense.Update(Stopwatch.GetTimestamp()); // RA01: M5a-6 - keeps MenuSense.InMenu up to date; logs one line when it changes

        Xr.FrameWaitInfo waitInfo = new() { Type = Xr.TypeFrameWaitInfo };
        Xr.FrameState frame = new() { Type = Xr.TypeFrameState };
        Check(Xr.WaitFrame(session, &waitInfo, &frame), "xrWaitFrame");

        Controllers.Sync(frame.PredictedDisplayTime); // RA01: M6-0 - reads both motion controllers for this headset frame; needs frame.PredictedDisplayTime, so it runs here rather than up with PollKeys

        Xr.FrameBeginInfo beginInfo = new() { Type = Xr.TypeFrameBeginInfo };
        Check(Xr.BeginFrame(session, &beginInfo), "xrBeginFrame");

        Xr.CompositionLayerProjectionView* views = stackalloc Xr.CompositionLayerProjectionView[2]; // RA01: M5b-1 - the two eye views of the projection layer
        Xr.CompositionLayerProjection projection = default;
        bool projected = false;
        Xr.CompositionLayerQuad* screens = stackalloc Xr.CompositionLayerQuad[15]; // RA01: M76 - 15 (the unarmed dot); M27-6g - 14 (the magazine well's small ball); M27-5c - 13 (the mag pouch glow); M27-1 - 12 (the banner); M24-4 - 10 (the laser's beam and dot); M23-0 - 8 (a second HUD panel). The layers of this frame: the game's picture (one for both eyes, or one per eye), the scope lens per picture (M16-A), then the HUD panel (M10-1), the crosshair (M13-61), then the F10 window
        int screenCount = 0;
        if (frame.ShouldRender != 0)
        {
            lastDisplayTime = frame.PredictedDisplayTime;
            if (recentreAt != 0 && frame.PredictedDisplayTime >= recentreAt) { recentreAt = 0; recentreWanted = true; HeadPose.Ready = false; HeadPose.ForgetHistory(); } // RA01: the announced move has happened, so the old centre and floor mean nothing in the moved space until the recentre below
            bool haveHead = LocateHead(frame.PredictedDisplayTime, out Xr.Posef head, out ulong headFlags);
            Controllers.PublishFrame(haveHead, head); // RA01: M8-2 - the head and both controllers from this same headset frame, handed over together (see Controllers.PublishFrame)
            if (haveHead && recentreWanted) RecentreNow(head);
            if (menuPlaceWanted && SettingsMenu.Open) PlaceMenu(haveHead, head);
            // RA01: the pictures the game builds from this pose reach the headset some tens of milliseconds later, so the head is predicted that far ahead of this frame's display time.
            // RA01: when the runtime will not predict that far, the head as it is now is used.
            Xr.Posef steerHead = head;
            predicting = haveHead && Tuning.PredictionMs > 0 && LocateHead(frame.PredictedDisplayTime + (long)(Tuning.PredictionMs * 1_000_000.0), out steerHead, out _, false);
            if (!predicting) steerHead = head;
            // RA01: M5a - with stereo on, the pose handed to the game is one eye's, the left and the right in turn. The game then builds its picture from that eye, and the pose tag
            // RA01: (kept with the pose) says which eye a picture belongs to. With stereo off it is the head's centre, as in M4b-3 (eye 0).
            // RA01: M5a-2 - the game's camera goes to the eye as the IPD setting wants it, but the picture is placed where the headset really has that eye ('placed'), so the picture's far
            // RA01: parts stay at the right distance (a larger IPD then makes the world look smaller, and does not push infinity apart).
            int eye = 0;
            Xr.Posef placed = steerHead;
            bool flat = Tuning.Stereo && Tuning.FlatAtDevices && BlackboardProbe.AtDeviceScreen;   // RA01: M23-2 - at a PC screen: steer from the head's centre (one pointer)
                                                                                         // RA01: M23-2b - the first build (eye 0 there) froze the picture at PCs: in 3D each eye
                                                                                         // RA01: shows the newest picture built FOR it, and eye-0 pictures belong to neither, so both
                                                                                         // RA01: eyes kept their last 3D picture (Rob 16:45: the room swam with the head, the screen
                                                                                         // RA01: blank, no mouse). Now the eyes keep taking turns; only the camera's eye offset is dropped.
            if (flat != flatWas) { flatWas = flat; Log.Write(flat ? "RA01 3D: flat view at a device screen (one picture for both eyes, so its pointer has one place)" : "RA01 3D: back to two eyes"); }
            if (Tuning.Stereo)
            {
                LocateEyes(frame.PredictedDisplayTime);
                eye = 1 + nextEye;
                nextEye ^= 1;
                placed = HeadPose.EyePose(steerHead, eyeOffset[eye - 1]);
                if (!flat) steerHead = HeadPose.EyePose(steerHead, EyeLocal(eye)); // RA01: M23-2b - flat: the eyes still take turns (each eye keeps getting fresh
            }                                                                    // RA01: pictures), but the game's camera stays at your head's centre for both
            if (flat && Tuning.DeviceScreenBackCm > 0)                          // RA01: M23-2c - at a PC screen, V's view sits back a little (Rob 16:53: her face was too close)
            {
                System.Numerics.Vector3 ahead = System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ, new System.Numerics.Quaternion(steerHead.Orientation.X, steerHead.Orientation.Y, steerHead.Orientation.Z, steerHead.Orientation.W));
                ahead.Y = 0;                                                     // RA01: straight back along your facing, level (OpenXR: -Z ahead, Y up)
                if (ahead.LengthSquared() > 1e-4f)
                {
                    ahead = System.Numerics.Vector3.Normalize(ahead) * (Tuning.DeviceScreenBackCm / 100f);
                    steerHead.Position.X -= ahead.X; steerHead.Position.Z -= ahead.Z;
                }
            }
            HeadPose.Publish(haveHead, steerHead, eye, placed, eye == 0 || flat ? default : EyeLocal(eye)); // RA01: M23-2b - flat: no eye offset to take back off // RA01: M9-1 - the eye offset rides along so the camera hook can publish the head's centre; RA01: the game's camera threads pick this up (nothing is published while the headset is not rendering, so they stop steering)
            LogPose(frame.PredictedDisplayTime, haveHead, head, headFlags);
            screenCount = ShowScreens(screens);
            EyeCheck.Tick();                              // RA01: M25-1 - the frame lag auto-fix's look, when one is due
            projected = !panelEyes && BuildProjection(&projection, views, screens, screenCount); // RA01: M5b-1 - the two eye pictures as one projection layer, when they can be; M68 - not on SteamVR
            if ((projected || (panelEyes && exactOk[1] && exactOk[2])) && haveHead) NoteEyeTiming(head); // RA01: M63 - log only
            if (screenCount > 0) screenCount += ShowStrip(&screens[screenCount], haveHead, head); // RA01: M24-4 - the laser, over the picture and under the HUD
            if (screenCount > 0) screenCount += ShowPouch(&screens[screenCount], haveHead, head); // RA01: M27-5c - the mag pouch glow, beside the laser; M27-6g - and the magazine well's ball
            if (screenCount > 0 && ShowHud(&screens[screenCount], haveHead, head, &screens[0], out int hudLayers, out bool crosshairShown))
            {
                NoteReticle(crosshairShown ? &screens[screenCount + hudLayers - 1] : &screens[screenCount], haveHead, head); // RA01: M21-5 - with the crosshair cut out, it is the crosshair layer (on the true aim), not the panel, wherever the panel was moved
                screenCount += hudLayers;                                                     // RA01: M13-61, M23-0 - the panel (or the two per-eye panels), then the crosshair layer
            }
            else { if (screenCount == 0) HudLayer.PanelLive = false; NoteReticle(null, false, head); } // RA01: M13-47 - no panel: the crosshair is in the picture's centre
            if (screenCount > 0 && ShowDot(&screens[screenCount], &screens[0])) screenCount++; // RA01: M76 - the unarmed dot, on the true aim, over the HUD
            if (ShowBanner(&screens[screenCount])) screenCount++;                // RA01: M27-1 - a message (Banner.cs), over everything but the F10 window
            if (ShowMenu(&screens[screenCount])) screenCount++;
        }

        void** layers = stackalloc void*[15];                 // RA01: M76 - 15 (the unarmed dot); M27-6g - 14 (was 13; M27-5c - 13, was 12)
        for (int i = 0; i < 15; i++) layers[i] = &screens[i];
        if (projected)                                    // RA01: M5b-1 - the projection layer takes the place of the two eye panels (screens 0 and 1); everything after them moves up one
        {
            layers[0] = &projection;
            for (int i = 1; i < 13; i++) layers[i] = &screens[i + 1];
            screenCount--;
        }
        Xr.FrameEndInfo endInfo = new()
        {
            Type = Xr.TypeFrameEndInfo, DisplayTime = frame.PredictedDisplayTime, EnvironmentBlendMode = Xr.BlendModeOpaque,
            LayerCount = (uint)screenCount, Layers = screenCount > 0 ? layers : null
        };
        Check(Xr.EndFrame(session, &endInfo), "xrEndFrame");
    }

    // RA01: fills 'screens' with the picture layers of this frame and returns how many (0 to 2). With stereo on (and the head steering, after a recentre) there is one layer per eye,
    // each showing the newest picture that was built for that eye; otherwise one layer that both eyes see, showing the newest picture (as in M4b-3).
    // Cost - CPU: extremely low (once per headset frame: a small snapshot of the mirrors' times and a short loop, a few microseconds apart from ShowEye);
    // GPU: low (through ShowEye, at most one game-picture copy per eye).
    static int ShowScreens(Xr.CompositionLayerQuad* screens)
    {
        if (swapchains[0] == 0) return 0;
        FollowResize();
        if (!FrameCopy.HasFrame) return 0;
        SmoothFov();
        Span<long> times = stackalloc long[FrameCopy.Mirrors], stamps = stackalloc long[FrameCopy.Mirrors];
        FrameCopy.Snapshot(times, stamps);
        Array.Clear(exactOk);                             // RA01: M5b-1 - set again by DescribeScreen for each eye that qualifies this frame
        bool stereo = Tuning.Stereo && HeadPose.Ready && HeadPose.Steering && NewestIsSteered(times, stamps);
        if (stereo != wasStereo) { Array.Clear(lastPicture); wasStereo = stereo; } // RA01: the images in the swapchains were made for the other way of showing
        int count = 0;
        if (!stereo) count = ShowEye(0, times, stamps, &screens[0]) ? 1 : 0;
        else
            for (int eye = 1; eye <= 2; eye++)
                if (ShowEye(eye, times, stamps, &screens[count])) count++;
        return count + ShowLenses(screens, count); // RA01: M16-A - the scope lens on top of each picture
    }

    // RA01: M16-A - adds a scope lens layer after each picture layer whose eye has a cut-out (ScopeLens), seen by that eye only, standing on the scope's
    // eyepiece (M16-A2). Returns how many were added. While the magnifier is off, every eye's cut-out is dropped.
    // Cost - CPU: extremely low (per headset frame: a flag check, and per lens one placement); GPU: none here (the runtime draws the small quad).
    static int ShowLenses(Xr.CompositionLayerQuad* screens, int pictures)
    {
        if (!ScopeLens.Wanted(out _)) { Array.Clear(lensCut); return 0; }
        int added = 0;
        for (int i = 0; i < pictures; i++)
        {
            int eye = (int)screens[i].EyeVisibility, side = eye == 2 ? 1 : 0;
            ScopeLens.Cutout cut = lensCut[eye];
            if (cut.Side == 0 || lensChains[side] == 0) continue;
            screens[pictures + added++] = new Xr.CompositionLayerQuad
            {
                Type = Xr.TypeCompositionLayerQuad, LayerFlags = Tuning.ScopeLensRound ? Xr.LayerBlendTextureSourceAlpha : 0, EyeVisibility = screens[i].EyeVisibility,
                SubImage = { Swapchain = lensChains[side], ImageRect = { Extent = { Width = cut.Side, Height = cut.Side } } },
                Space = screens[i].Space, Pose = ScopeLens.Placed(screens[i].Pose, ScreenDistance, cut),
                Size = { Width = cut.LensMetres, Height = cut.LensMetres }
            };
        }
        return added;
    }

    // RA01: M16-A - right after a new picture went into this eye's image: while the magnifier is on, cut its square (ScopeLens.Cut) into the next image of the
    // eye side's lens swapchain (made the first time). A failed copy hides this eye's lens until the next picture (the image released holds nothing new).
    // Cost - CPU: low (per new eye picture while scoped: acquire, one copy list, release); GPU: extremely low (FrameCopy.ShowLens).
    static void CutLens(int eye, int side, long picture, Xr.CompositionLayerQuad* screen)
    {
        lensCut[eye] = default;
        if (lensFailed || !ScopeLens.Wanted(out float zoom)
            || !ScopeLens.Cut(zoom, screen->Size.Width, screen->Size.Height, usedFov[eye], screenWidth, screenHeight, ScreenDistance, eye == 0 ? default : EyeLocal(eye), out ScopeLens.Cutout cut)) return;
        try
        {
            if (lensChains[side] == 0)
            {
                lensChains[side] = CreateSwapchain(screenFormat, ScopeLens.MaxSide, ScopeLens.MaxSide, out lensTextures[side]);
                Log.Write($"RA01 scope lens: ready for the {(side == 0 ? "left (or both)" : "right")} eye ({ScopeLens.MaxSide}x{ScopeLens.MaxSide}, {lensTextures[side].Length} images)");
            }
            uint index = AcquireImage(lensChains[side]);
            bool copied = FrameCopy.ShowLens(lensTextures[side][index], picture, cut.X, cut.Y, cut.Side, Tuning.ScopeLensRound);
            ReleaseImage(lensChains[side]);
            if (copied) lensCut[eye] = cut;
        }
        catch (Exception e) { lensFailed = true; Log.Write($"RA01 scope lens: switched off for this session ({e.Message})"); }
    }

    // RA01: M5a-2 - true when the newest picture was built from a pose of ours that is still remembered. A picture that was not (a menu, a loading screen: no camera was built for it) cannot be
    // given an eye, so those are shown as one picture for both eyes, as before M5a, instead of leaving both eyes with the last picture the head's camera built.
    // Cost - CPU: extremely low (once per headset frame: a loop over the few mirrors and one pose lookup); GPU: none.
    static bool NewestIsSteered(Span<long> times, Span<long> stamps)
    {
        int newest = 0;
        for (int i = 1; i < times.Length; i++)
            if (times[i] > times[newest]) newest = i;
        return times[newest] != 0 && HeadPose.PoseOf(stamps[newest], out _, out _);
    }

    // RA01: the eye number a picture's pose tag must carry for this eye's layer. Eye 0 is the picture for both eyes and wants a tag from the head's centre (0).
    // Tuning.SwapEyes turns the two eyes round, for a pipeline that is one frame off.
    // Cost - CPU: extremely low (one comparison, called a few times per eye per headset frame); GPU: none.
    static int WantedTagEye(int eye) => Tuning.SwapEyes && eye != 0 ? 3 - eye : eye;

    // RA01: the mirror index of the newest picture for 'eye' (-1 = none). Eye 0: simply the newest. Eye 1 or 2: the newest one whose pose stamp says it was built from that eye's pose.
    // Cost - CPU: extremely low (once per eye per headset frame: a loop over the few mirrors, each with one pose lookup); GPU: none.
    static int ChoosePicture(int eye, Span<long> times, Span<long> stamps)
    {
        int best = -1;
        long bestTime = 0;
        for (int i = 0; i < times.Length; i++)
        {
            if (times[i] <= bestTime) continue; // RA01: also skips an empty mirror (time 0)
            if (eye != 0 && !(HeadPose.PoseOf(stamps[i], out _, out int tagEye) && tagEye == WantedTagEye(eye))) continue;
            best = i;
            bestTime = times[i];
        }
        return best;
    }

    // RA01: describes this eye's floating screen in 'screen'. A picture for this eye that is newer than the one already in the eye's swapchain is copied into a new headset image first.
    // with no newer picture the layer shows the image that is already there (the runtime keeps the last released image), and the screen stays in the room where it was.
    // false = nothing to show for this eye this frame. An acquired image is always released again before the frame ends.
    // Cost - CPU: low (per eye per headset frame: acquire, wait, one copy command and release of a swapchain image, microseconds to tens of microseconds, but AcquireImage can wait for the runtime);
    // GPU: low when a new picture is copied (one game-picture copy, a few MB), none when the old image is reused.
    static bool ShowEye(int eye, Span<long> times, Span<long> stamps, Xr.CompositionLayerQuad* screen)
    {
        int chosen = ChoosePicture(eye, times, stamps);
        long picture = chosen < 0 ? 0 : times[chosen];
        long stamp = chosen < 0 ? 0 : stamps[chosen];
        int side = eye == 2 ? 1 : 0;
        ulong chain = swapchains[side];
        if (picture <= lastPicture[eye]) // RA01: none, the one already there, or an older one (the frame lag was changed): nothing newer to copy
        {
            if (picture == 0) missedFrames[eye]++;
            if (lastPicture[eye] == 0) return false;
            DescribeScreen(screen, chain, eye, lastStamp[eye]);
            return true;
        }

        uint index = AcquireImage(chain);
        bool copied = FrameCopy.Show(imageTextures[side][index], picture, screenWidth, screenHeight);
        ReleaseImage(chain);
        lastPicture[eye] = copied ? picture : 0; // RA01: a failed copy leaves an image with nothing valid in it, which must not be shown again
        if (!copied) return false;

        lastStamp[eye] = stamp;
        freshPictures[eye]++;
        pickupAgeMs = (float)((Stopwatch.GetTimestamp() - picture) * 1000.0 / Stopwatch.Frequency); // RA01: for the log: how old the picture is now that the headset has taken it
        DescribeScreen(screen, chain, eye, stamp);
        CutLens(eye, side, picture, screen); // RA01: M16-A - the scope lens's square, from this same picture
        return true;
    }

    // RA01: M5a-5 - the game's resolution was changed: FrameCopy has made new mirrors of the new size, so the headset images are made again at that size too, and the picture's shape follows.
    // the old swapchains are destroyed a second later (the runtime may still be showing their last image). If the new size cannot be made (bigger than the headset allows) the frame copy is switched off, with the reason in the log.
    // Cost - CPU: extremely low on almost every headset frame (a clock read, a short list check and a size compare); medium on the rare frame when the game's resolution changed (two new swapchains are made);
    // GPU: none per frame, medium on a resize (new images in video memory).
    static void FollowResize()
    {
        long now = Stopwatch.GetTimestamp();
        for (int i = oldChains.Count - 1; i >= 0; i--)
            if (now >= oldChains[i].DestroyAt) { Xr.DestroySwapchain(oldChains[i].Chain); oldChains.RemoveAt(i); }
        if (FrameCopy.SizeVersion == seenSize) return;

        seenSize = FrameCopy.Size(out int width, out int height);
        if (width == screenWidth && height == screenHeight) return;
        ulong[] fresh = new ulong[2];
        nint[][] textures = new nint[2][];
        try
        {
            for (int side = 0; side < 2; side++) fresh[side] = CreateSwapchain(screenFormat, width, height, out textures[side]);
        }
        catch (Exception e)
        {
            foreach (ulong chain in fresh) if (chain != 0) Xr.DestroySwapchain(chain);
            FrameCopy.Disable($"headset images of {width}x{height} could not be made: {e.Message}");
            return;
        }
        for (int side = 0; side < 2; side++)
        {
            oldChains.Add((swapchains[side], now + (long)(OldChainSeconds * Stopwatch.Frequency)));
            swapchains[side] = fresh[side];
            imageTextures[side] = textures[side];
        }
        Log.Write($"RA01 OpenXR screen: the game is now {width}x{height} (was {screenWidth}x{screenHeight}), new headset images made");
        screenWidth = width;
        screenHeight = height;
        Array.Clear(lastPicture); // RA01: the images that held pictures of the old size are gone
        Array.Clear(lastStamp);
        Array.Clear(lensCut);     // RA01: M16-A - their lens squares too
    }

    // RA01: takes the next image of a swapchain and waits until the runtime is done with it (100 ms at a time, the time is in nanoseconds). Every acquired image must be released again with ReleaseImage.
    // Cost - CPU: low (per new picture per eye per headset frame: two runtime calls, which only block when the runtime is still using the image, waiting is not real CPU work);
    // GPU: none of its own (it waits for the runtime's earlier GPU use of the image to finish).
    static uint AcquireImage(ulong chain)
    {
        Xr.SwapchainImageAcquireInfo acquireInfo = new() { Type = Xr.TypeSwapchainImageAcquireInfo };
        uint index;
        Check(Xr.AcquireSwapchainImage(chain, &acquireInfo, &index), "xrAcquireSwapchainImage");

        Xr.SwapchainImageWaitInfo waitInfo = new() { Type = Xr.TypeSwapchainImageWaitInfo, Timeout = 100_000_000 };
        int result = Xr.TimeoutExpired;
        for (int tries = 0; tries < 20 && result == Xr.TimeoutExpired; tries++)
            result = Xr.WaitSwapchainImage(chain, &waitInfo);
        Check(result, "xrWaitSwapchainImage");
        if (result == Xr.TimeoutExpired) throw new InvalidOperationException("xrWaitSwapchainImage kept timing out");
        return index;
    }

    // RA01: gives an image taken with AcquireImage back to the runtime so it can be shown.
    // Cost - CPU: extremely low (one small runtime call per new picture per eye per headset frame); GPU: none.
    static void ReleaseImage(ulong chain)
    {
        Xr.SwapchainImageReleaseInfo releaseInfo = new() { Type = Xr.TypeSwapchainImageReleaseInfo };
        Check(Xr.ReleaseSwapchainImage(chain, &releaseInfo), "xrReleaseSwapchainImage");
    }

    // RA01: M5a-11 - once per session, when AutoFov is on: reads the headset's own field of view cone from OpenXR and remembers it, so TryAutoFovScale can work out how much wider than its own the game's field of view should be made.
    // this needs a real headset frame to have already happened (lastDisplayTime is only set once xrWaitFrame has returned), so it quietly does nothing and tries again next frame until it succeeds.
    // M5a-12 - reads BOTH eyes from the one xrLocateViews call (it used to read only eyes[0] and use that same cone for both eyes), because the two eyes' cones are mirror images of each other, not identical.
    // M5a-13 - the picture frame no longer takes its shape from this cone (see the note near the top of the file); only the left/right edges of the both-eyes cone are kept, for TryAutoFovScale.
    // Cost - CPU: extremely low (skipped entirely once known; before that, one xrLocateViews call per headset frame until it succeeds, then never called again); GPU: none.
    static void TryCaptureHeadsetFov()
    {
        if ((!Tuning.AutoFov && Tuning.PictureMode != 0) || headsetFovKnown || lastDisplayTime == 0) return; // RA01: M5b-2 - Projection mode always needs the cone
        Xr.ViewLocateInfo locateInfo = new() { Type = Xr.TypeViewLocateInfo, ViewConfigurationType = Xr.ViewConfigurationPrimaryStereo, DisplayTime = lastDisplayTime, Space = viewSpace };
        Xr.ViewState viewState = new() { Type = Xr.TypeViewState };
        Xr.View* eyes = stackalloc Xr.View[2];
        eyes[0] = new() { Type = Xr.TypeView };
        eyes[1] = new() { Type = Xr.TypeView };
        uint count;
        if (Xr.LocateViews(session, &locateInfo, &viewState, 2, &count, eyes) < 0 || count < 2) return;
        for (int i = 0; i < 2; i++)
        {
            Xr.Fovf fov = eyes[i].Fov;
            if (!(fov.AngleRight > fov.AngleLeft && fov.AngleUp > fov.AngleDown)) return; // RA01: a sane cone has a real width and height; anything else is not a usable answer yet from either eye, try again next frame
        }
        for (int i = 0; i < 2; i++)
        {
            Xr.Fovf fov = eyes[i].Fov;
            headsetFovL[i] = fov.AngleLeft; headsetFovR[i] = fov.AngleRight; headsetFovU[i] = fov.AngleUp; headsetFovD[i] = fov.AngleDown;
        }
        FitCone(eyes);
        headsetFovBothL = Math.Min(headsetFovL[0], headsetFovL[1]);
        headsetFovBothR = Math.Max(headsetFovR[0], headsetFovR[1]);
        headsetFovKnown = true;
        const float degrees = 180f / MathF.PI;
        Log.Write($"RA01 headset field of view: left eye L{headsetFovL[0] * degrees:F0} R{headsetFovR[0] * degrees:F0} U{headsetFovU[0] * degrees:F0} D{headsetFovD[0] * degrees:F0}, right eye L{headsetFovL[1] * degrees:F0} R{headsetFovR[1] * degrees:F0} U{headsetFovU[1] * degrees:F0} D{headsetFovD[1] * degrees:F0} degrees "
            + "(from OpenXR; used only to work out how wide to make the game's own field of view, see the 'RA01 AutoFov:' line)");
    }

    // RA01: M5b-2 - THE HEADSET'S VIEW, IN THE HEAD'S OWN DIRECTIONS. Each eye's cone (OpenXR) is a window frame in front of that eye, but on some headsets (Index, Pimax)
    // each eye's screen is turned slightly outward, and the game's eye cameras look straight ahead with the head. So each of the window's four corners is turned
    // by that eye's own turn and measured again from straight ahead: how far right or left (x) and up or down (y) per metre forward - a slope, like a roof's pitch.
    // The largest slope sideways and up/down over both eyes is what a centred game view must reach. (A window's corners are always its outermost points, so
    // four corners per eye is exact.) On a headset with straight screens (Quest 3) the turn is none and this is just the cone itself. Logs the result.
    // Cost - CPU: extremely low (once per session: eight corners, each a few dozen multiplies); GPU: none.
    static void FitCone(Xr.View* eyes)
    {
        float maxX = 0, maxY = 0;
        for (int i = 0; i < 2; i++)
        {
            Xr.Fovf f = eyes[i].Fov;
            Xr.Quaternionf turn = eyes[i].Pose.Orientation;
            float length = turn.X * turn.X + turn.Y * turn.Y + turn.Z * turn.Z + turn.W * turn.W;
            if (!(length > 0.98f && length < 1.02f)) turn = new Xr.Quaternionf { W = 1 }; // RA01: no usable turn reported: treat the screen as straight
            for (int corner = 0; corner < 4; corner++)
            {
                Xr.Vector3f ray = new() { X = MathF.Tan(corner % 2 == 0 ? f.AngleLeft : f.AngleRight), Y = MathF.Tan(corner < 2 ? f.AngleDown : f.AngleUp), Z = -1f };
                Xr.Vector3f head = HeadPose.Turn(turn, ray);
                if (!(head.Z < -0.05f)) continue;          // RA01: a corner pointing sideways or behind is not a real lens; skip it
                maxX = Math.Max(maxX, MathF.Abs(head.X / -head.Z));
                maxY = Math.Max(maxY, MathF.Abs(head.Y / -head.Z));
            }
        }
        fitTanX = maxX; fitTanY = maxY;
        const float degrees = 180f / MathF.PI;
        Log.Write($"RA01 view fit: the headset shows up to {MathF.Atan(maxX) * degrees:F1} degrees to each side and {MathF.Atan(maxY) * degrees:F1} up/down of straight ahead (both eyes, in the head's directions)");
    }

    // RA01: M5b-2 - the vertical field of view the game draws with in Projection mode: just enough for its centred picture to reach the headset's widest edges (FitCone),
    // RA01: times View coverage. The picture's own shape decides which edge is the tight one: the half-height must reach the up/down slope, and the half-width
    // RA01: (the half-height's slope times the shape, width / height) must reach the sideways slope - so the half-height's slope is the larger of 'up/down' and
    // RA01: 'sideways / shape'. Coverage shrinks the slopes (the size of the view), never bends anything. 0 = not in use (Flat screen mode, or not measured yet).
    // RA01: Read by CameraHook on the game's camera threads. Cost - CPU: extremely low (a read); GPU: none.
    public static float FitFov => Volatile.Read(ref fitFov);

    // RA01: M5b-2 - works FitFov out again (the picture's shape or View coverage may change), once per headset frame. Logs when it changes by a degree or more.
    // RA01: M5b-2b - then holds the view's WIDTH to Tuning.GameWidthLimitDeg (default 96): the game's visibility system is built for views up to about 100 degrees, and past that
    // RA01: rooms vanish. Same triangle sum backwards: the widest half-width slope is tan(limit / 2); divided by the picture's shape it gives the tallest half-height slope.
    // RA01: M5b-2c - the height is also held to 90 degrees (a half-height slope of at most 1, like a builder's square): past that the game's culling breaks.
    // RA01: M21-9 - 130 degrees while Core\Video\DistanceScaleFix.cs is on (it removes the cause), 90 again if it is off or not installed.
    // RA01: Cost - CPU: extremely low (a division, an arctangent and a comparison per headset frame); GPU: none.
    static float loggedFit;
    static bool capped;             // RA01: M5b-2b - true while the width limit is what decides the view
    static void UpdateFit()
    {
        float fit = 0f;
        if (Tuning.PictureMode == 0 && fitTanY > 0 && screenWidth > 0 && screenHeight > 0)
        {
            float shape = (float)screenWidth / screenHeight;
            float slope = Math.Max(fitTanY, fitTanX / shape) * (Tuning.ViewCoverage / 100f);
            float widest = MathF.Tan(Tuning.GameWidthLimitDeg * (MathF.PI / 360f)) / shape; // RA01: M5b-2b - the half-height slope at which the width reaches the limit
            float tallest = MathF.Tan(Tuning.MaxGameTallDeg * (MathF.PI / 360f)); // RA01: M5b-2c - and never taller than Tuning.MaxGameTallDeg (90 degrees, the slope of 45 degrees is 1; M21-9: 130 while the distance fix works)
            capped = slope > Math.Min(widest, tallest);
            if (capped) slope = Math.Min(widest, tallest);
            fit = Math.Clamp(2f * MathF.Atan(slope) * (180f / MathF.PI), MinPictureFov, MaxPictureFov);
        }
        Volatile.Write(ref fitFov, fit);
        if (MathF.Abs(fit - loggedFit) >= 1f)
        {
            loggedFit = fit;
            Log.Write(fit > 0 ? $"RA01 view fit: the game draws {fit:F1} degrees tall, {2f * MathF.Atan(MathF.Tan(fit * (MathF.PI / 360f)) * screenWidth / screenHeight) * (180f / MathF.PI):F1} wide (picture shape {(float)screenWidth / screenHeight:F2}, View coverage {Tuning.ViewCoverage:F0}%{(capped ? $", held to the {Tuning.GameWidthLimitDeg:F0}-degree width / {Tuning.MaxGameTallDeg:F0}-degree height limit" : "")})" : "RA01 view fit: off (Flat screen mode) - the game field of view settings apply");
        }
    }

    // RA01: M5a-11 - once per session, when AutoFov is on and the headset's field of view is known: picks a Tuning.FovScale that widens the game's own field of view to roughly match the headset's, instead of just cropping a bigger frame around the same narrow view.
    // done once, not every frame, because the game's own field of view narrows on its own for aiming and zoom, and re-solving for those moments would fight the player instead of helping them; the Game field of view slider still adjusts the result from here exactly as before.
    // M5a-12 - uses the cone that covers both eyes (not just the left eye's) so the game is widened enough for whichever eye needs the most, not whichever eye happened to be read first.
    // Cost - CPU: extremely low (a couple of trig calls, once per session, the moment a usable camera field of view first appears); GPU: none.
    static void TryAutoFovScale()
    {
        if (!Tuning.AutoFov || autoFovScaleDone || !headsetFovKnown || Tuning.PictureMode == 0) return; // RA01: M5b-2 - Projection mode fits the view itself
        float widened = CameraHook.Fov;
        if (!(widened > 20f && widened < 150f)) return; // RA01: no camera built by the game yet; try again next frame
        float baseFov = CameraHook.UnwidenFov(widened, Tuning.FovScale);
        float headsetWide = headsetFovBothR - headsetFovBothL; // RA01: radians; how wide the cone covering both eyes is, left edge to right edge
        float scale = MathF.Tan(headsetWide / 2f) / MathF.Tan(baseFov * (MathF.PI / 360f));
        Tuning.FovScale = Tuning.AutoFovScale; // RA01: M13-19, M13-20 - always Tuning.AutoFovScale (2.00) now; the full-match 'scale' above is still logged
        autoFovScaleDone = true;
        Log.Write($"RA01 AutoFov: the game's own field of view is {baseFov:F0} degrees, the headset wants {headsetWide * (180f / MathF.PI):F0} degrees wide, a full match would be x{scale:F2}; game field of view is set to x{Tuning.FovScale:F2} (the Game field of view slider still works from here)");
    }

    // RA01: once per headset frame: the field of view the pictures are sized for follows the game's a tenth of the way per frame, so the size does not jump.
    // it is done once for the frame, not per eye, so both eyes' pictures always have the same size.
    // M5a-4 - the same for the menu blend: while no camera is being built or the mouse says menu (MenuSense: a menu, a loading screen, a pause) the picture is sized for Tuning.GameMenuFov instead, whatever the game's last field of view was.
    // M5a-11 - also tries, once, to capture the headset's field of view and auto-pick a matching FovScale, when AutoFov is on (see TryCaptureHeadsetFov / TryAutoFovScale; both do nothing once they have succeeded, or whenever AutoFov is off).
    // Cost - CPU: extremely low (once per headset frame: a few multiplications, a clamp and a clock read, plus the two calls above which cost nothing once settled); GPU: none.
    static void SmoothFov()
    {
        TryCaptureHeadsetFov();
        TryAutoFovScale();
        UpdateFit();
        float fov = CameraHook.Fov > 0 ? CameraHook.Fov : DefaultPictureFov;
        fov = Math.Clamp(fov, MinPictureFov, MaxPictureFov);
        pictureFov = pictureFov == 0 ? fov : pictureFov + (fov - pictureFov) * FovSmoothing;
        long now = Stopwatch.GetTimestamp();
        float menu = Tuning.MenuView == 2 ? 0f : MenuSense.ShowsMenu || CameraHook.CinematicCloseUp || !CameraHook.Building(now) || GameAnchor.Loading ? 1f : 0f; // RA01: M5a-8 - a menu when the mouse says so (MenuSense, which also follows the F10 row and F9), a cinematic close-up (M82), or when no camera is built (M14-0c: or the game says a loading screen is up); "Game" (2) forces the game size
        menuBlend += (menu - menuBlend) * FovSmoothing;
    }

    // RA01: M5b-1 - the field of view to lay a Projection-mode picture out with: what the game draws with right now (CameraHook.Fov, already widened), unsmoothed so
    // it keeps up with aiming in and out, kept within the same sane range as the panel's.
    // Cost - CPU: extremely low (a read and a clamp); GPU: none.
    static float ExactFov() => Math.Clamp(CameraHook.Fov > 0 ? CameraHook.Fov : DefaultPictureFov, MinPictureFov, MaxPictureFov);

    // RA01: M5b-1 - PROJECTION LAYER: when both eye panels of this frame (screens 0 and 1, left and right) are exact pictures (DescribeScreen), they are handed to
    // the headset instead as one projection layer: each eye's image with the eye pose it was drawn from and the viewing cone it was drawn through. That cone,
    // from the game's vertical field of view v and its picture's shape (aspect = width / height): up +v/2, down -v/2, right +h, left -h, where
    // tan(h) = tan(v/2) x aspect - the same lens, a wider print. The headset then lays the picture out itself, exactly, so nothing slides when the head turns.
    // Anything that doesn't qualify (a menu, mono, Flat screen mode, an eye with no picture yet) keeps the panels, exactly as before. Logs when it starts or stops.
    // Cost - CPU: extremely low (per headset frame: two tangents, an arctangent and two small structures); GPU: none here (the runtime's compositor draws it).
    static bool BuildProjection(Xr.CompositionLayerProjection* projection, Xr.CompositionLayerProjectionView* views, Xr.CompositionLayerQuad* screens, int count)
    {
        bool ok = count >= 2 && screens[0].EyeVisibility == 1 && screens[1].EyeVisibility == 2 && exactOk[1] && exactOk[2];
        if (ok != wasProjection) { wasProjection = ok; Log.Write(ok ? "RA01 picture: projection layer ON - each eye's picture is that eye's own view" : "RA01 picture: projection layer off - flat panels (a menu, mono, Flat screen mode or no picture yet)"); }
        if (!ok) return false;
        float aspect = (float)screenWidth / screenHeight;
        for (int v = 0; v < 2; v++)
        {
            int eye = v + 1;
            float halfUp = usedFov[eye] * (MathF.PI / 360f), halfSide = MathF.Atan(MathF.Tan(halfUp) * aspect);
            views[v] = new Xr.CompositionLayerProjectionView
            {
                Type = Xr.TypeCompositionLayerProjectionView, Pose = exactPose[eye],
                Fov = new Xr.Fovf { AngleLeft = -halfSide, AngleRight = halfSide, AngleUp = halfUp, AngleDown = -halfUp },
                SubImage = screens[v].SubImage
            };
        }
        *projection = new Xr.CompositionLayerProjection { Type = Xr.TypeCompositionLayerProjection, Space = floorSpace, ViewCount = 2, Views = views };
        return true;
    }

    // RA01: fills in the quad layer: the swapchain image, which eye sees it, its size in metres, and where it stands. 'stamp' says which pose the game's picture in the image was built from.
    // size (from SmoothFov): the picture is as big as the game's own field of view makes it at ScreenDistance, so its perspective is true (times Tuning.SizeScale); its shape always follows the game's own back-buffer aspect ratio.
    // M5a-13 - M5a-11/12 shaped this frame straight from the headset's own field-of-view cone instead, while AutoFov was on (often a squarish shape). Real headset testing found it vertically compressed and ugly, and it left the Game field of view slider with nothing to do and broke the menu's own sizing, so it is gone: the frame always follows the game's own aspect ratio now, exactly as before M5a-11.
    // Cost - CPU: extremely low (per layer per headset frame: one tangent, a pose lookup and some multiplications, a few microseconds); GPU: none here (it only describes the quad, the runtime draws it).
    static void DescribeScreen(Xr.CompositionLayerQuad* screen, ulong chain, int eye, long stamp)
    {
        Xr.Posef tag = default;
        int tagEye = 0;
        bool tagged = HeadPose.Ready && HeadPose.Steering && HeadPose.PoseOf(stamp, out tag, out tagEye) && (eye == 0 || tagEye == WantedTagEye(eye));
        bool exact = Tuning.PictureMode == 0 && tagged && eye != 0 && menuBlend < 0.001f; // RA01: M5b-1 - Projection mode, a stereo eye picture with its exact pose, in play (not a menu)
        float fov = exact ? ExactFov() : pictureFov;
        usedFov[eye] = fov;
        exactOk[eye] = exact;
        if (exact) exactPose[eye] = tag;
        float gameHeight = 2 * ScreenDistance * MathF.Tan(fov * (MathF.PI / 360f)) * (exact ? 1f : Tuning.SizeScale); // RA01: M5b-1 - exact: the true size (this panel is not shown, but the scope lens and crosshair are placed from it)
        float menuHeight = 2 * ScreenDistance * MathF.Tan(Tuning.GameMenuFov * (MathF.PI / 360f));
        float height = gameHeight + (menuHeight - gameHeight) * menuBlend; // RA01: M5a-4 - the size for a game menu counts more the longer no camera has been built
        float width = height * screenWidth / screenHeight;
        *screen = new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad, EyeVisibility = eye,
            SubImage = { Swapchain = chain, ImageRect = { Extent = { Width = screenWidth, Height = screenHeight } } },
            Size = { Width = width, Height = height }
        };
        // RA01: the head steers the game: the picture stands in the room, in front of the pose (the head's or one eye's) the game was given when it built the camera for it, so it stays
        // RA01: where the world it shows is until a newer picture comes. The stamp is the exact pose the game built it from (found through CameraHook), so nothing is guessed from times.
        // RA01: with one eye's pose the picture is built from that eye's spot, and standing straight ahead of that spot it is seen by that eye exactly as it was drawn.
        if (tagged)
        {
            screen->Space = floorSpace;
            screen->Pose = HeadPose.TaggedScreenPose(tag, ScreenDistance);
            pictureMode = exact ? "each eye's picture given to the headset as that eye's own view (projection layer)" : eye == 0 ? "standing in the room where the head was when the game built it" : "each eye's picture standing in the room where that eye was when the game built it";
            if (exact) return;                            // RA01: M5b-1 - exact: no shifts or trims - the picture is where it was drawn from
        }
        else if (HeadPose.Ready && !HeadPose.Steering)
        {
            screen->Space = floorSpace; // RA01: with the head not steering the game, the screen stands in the room, its bottom edge exactly on the floor
            screen->Pose = HeadPose.ScreenPose(ScreenDistance, height);
            pictureMode = "standing in the room in front of the recentre point (steering off)";
        }
        else
        {
            screen->Space = viewSpace;  // RA01: before the first recentre, or while there is no pose tag yet, it floats in front of the head, like in M3
            screen->Pose = new Xr.Posef { Orientation = { W = 1 }, Position = { Z = -ScreenDistance } }; // RA01: OpenXR looks down -Z, so 'in front' is negative
            pictureMode = "floating in front of the head";
        }
        // RA01: M5a-5 - the picture moved by the settings (Tuning.ShiftRightDeg, ShiftUpDeg): it is turned about the eye, by exactly those angles, so it still faces the eye (M5a-4 slid it in its own plane, which looked tilted).
        if (Tuning.ShiftRightDeg != 0 || Tuning.ShiftUpDeg != 0)
            screen->Pose = HeadPose.Orbited(screen->Pose, ScreenDistance, Tuning.ShiftRightDeg * (MathF.PI / 180f), Tuning.ShiftUpDeg * (MathF.PI / 180f));
        // RA01: M13-63 - the game's menus moved up or down (Tuning.GameMenuUpDeg), turned about the eye the same way - only as far as the picture counts as a menu
        // RA01: (menuBlend), so it eases in with the menu size and gameplay is never moved. M38 - and left or right (Tuning.GameMenuRightDeg), the same way.
        if ((Tuning.GameMenuUpDeg != 0 || Tuning.GameMenuRightDeg != 0) && menuBlend > 0.001f)
            screen->Pose = HeadPose.Orbited(screen->Pose, ScreenDistance, Tuning.GameMenuRightDeg * menuBlend * (MathF.PI / 180f), Tuning.GameMenuUpDeg * menuBlend * (MathF.PI / 180f));
        // RA01: M5a-12 - a small extra turn for one eye only, on top of the shared one above, the same way (turned about the eye, so it still faces it). Eye 0 (mono, both eyes) gets no trim: it would only be right for one eye anyway.
        float trimRight = eye == 1 ? Tuning.LeftEyeShiftRightDeg : eye == 2 ? Tuning.RightEyeShiftRightDeg : 0f;
        float trimUp = eye == 1 ? Tuning.LeftEyeShiftUpDeg : eye == 2 ? Tuning.RightEyeShiftUpDeg : 0f;
        if (trimRight != 0 || trimUp != 0)
            screen->Pose = HeadPose.Orbited(screen->Pose, ScreenDistance, trimRight * (MathF.PI / 180f), trimUp * (MathF.PI / 180f));
    }

    // RA01: M5a-2 - where an eye (1 = left, 2 = right) is in the head's own axes: what the runtime said, or with an IPD set (Tuning.IpdMm) moved half of the difference each way,
    // so the two eyes end up exactly that far apart sideways.
    // Cost - CPU: extremely low (a few additions and multiplications, called once or twice per headset frame in stereo); GPU: none.
    static Xr.Vector3f EyeLocal(int eye)
    {
        Xr.Vector3f local = eyeOffset[eye - 1];
        if (Tuning.IpdMm > 0) local.X += (eye == 1 ? -0.5f : 0.5f) * (Tuning.IpdMm * 0.001f - (eyeOffset[1].X - eyeOffset[0].X));
        return local;
    }

    // RA01: where each eye is in the head's own axes (asked in the view space), so the game's camera can be moved to each eye in turn. A failed or odd answer keeps the last one.
    // Cost - CPU: low (once per headset frame in stereo: one xrLocateViews call, a few microseconds to tens of microseconds inside the runtime, plus a few comparisons); GPU: none.
    static void LocateEyes(long displayTime)
    {
        Xr.ViewLocateInfo locateInfo = new() { Type = Xr.TypeViewLocateInfo, ViewConfigurationType = Xr.ViewConfigurationPrimaryStereo, DisplayTime = displayTime, Space = viewSpace };
        Xr.ViewState viewState = new() { Type = Xr.TypeViewState };
        Xr.View* eyes = stackalloc Xr.View[2];
        eyes[0] = new() { Type = Xr.TypeView };
        eyes[1] = new() { Type = Xr.TypeView };
        uint count;
        if (Xr.LocateViews(session, &locateInfo, &viewState, 2, &count, eyes) < 0 || count < 2 || (viewState.ViewStateFlags & Xr.ViewStatePositionValid) == 0) return;
        for (int i = 0; i < 2; i++)
        {
            Xr.Vector3f p = eyes[i].Pose.Position;
            if (!(MathF.Abs(p.X) < MaxEyeOffset && MathF.Abs(p.Y) < MaxEyeOffset && MathF.Abs(p.Z) < MaxEyeOffset)) return; // RA01: also false for NaN
        }
        eyeOffset[0] = eyes[0].Pose.Position;
        eyeOffset[1] = eyes[1].Pose.Position;
        Tuning.HeadsetIpdMm = (eyeOffset[1].X - eyeOffset[0].X) * 1000f;
    }

    // RA01: M5a-3 - F10: opens or closes the settings window. Opening takes the mouse from the game (MenuInput) and puts the window where the head faces (PlaceMenu, next frame).
    // Cost - CPU: extremely low (runs only when F10 is pressed: a few flag changes, one log line and the mouse hook start or stop); GPU: none.
    static void ToggleMenu()
    {
        if (menuChain == 0 || menuFailed)
        {
            Log.Write("RA01 menu: the F10 window is not available (the lines about the menu earlier in the log say why)");
            return;
        }
        SettingsMenu.Toggle();
        if (SettingsMenu.Open)
        {
            menuShown = false;
            menuPlaceWanted = true;
            MenuInput.Start(FrameCopy.GameWindow);
            Log.Write($"RA01 menu: opened (F10, or {ControllerMapping.SettingsChordText}). Move the mouse, or point the right hand at it and squeeze the trigger to click; hover over things for their explanation; F10, the same chord again, or the X closes it"); // RA01: M28-3 - the chord follows left-hand mode
        }
        else MenuInput.Stop();
    }

    // RA01: M5a-3 - puts the window one MenuDistance straight ahead of the head at head height, turned to face it, standing in the room (it stays there when the head turns).
    // with no usable head pose (or looking straight up or down) it is fixed in front of the headset instead.
    // Cost - CPU: extremely low (runs once each time the window is opened: a sine, a cosine and a few additions); GPU: none.
    static void PlaceMenu(bool haveHead, in Xr.Posef head)
    {
        menuPlaceWanted = false;
        if (haveHead && HeadPose.YawOf(head.Orientation, out float yaw))
        {
            menuSpace = floorSpace;
            menuPose = new Xr.Posef
            {
                Orientation = { Y = MathF.Sin(yaw / 2), W = MathF.Cos(yaw / 2) },
                Position = { X = head.Position.X - MathF.Sin(yaw) * MenuDistance, Y = head.Position.Y, Z = head.Position.Z - MathF.Cos(yaw) * MenuDistance }
            };
        }
        else
        {
            menuSpace = viewSpace;
            menuPose = new Xr.Posef { Orientation = { W = 1 }, Position = { Z = -MenuDistance } };
        }
    }

    // RA01: M10-1 - the HUD panel: fills in its layer and returns true when the HUD layer has been found (HudLayer), the HUD tab's switch is on and no game menu shows.
    // Each headset frame our HUD sheet is copied into the panel's next image. M10-2 - HudLayer takes the HUD off the game's picture (PanelLive) as soon as the
    // panel is wanted, but the panel itself is only shown once our sheet holds a finished HUD and an image has had it copied in (a fresh texture holds leftover
    // video memory, the "TV fuzz") - at most a frame or two without a HUD; any failure hands the HUD straight back to the picture. The panel is a see-through quad the headset draws at its real distance, so both eyes agree on it.
    // Cost - CPU: low (two runtime calls and one copy submit per headset frame, tens of microseconds); GPU: low (one screen-sized copy per headset frame).
    // M13-61 - in play (M21-10: always, not only while the game shows a crosshair), the HUD's centre square is cut out of the panel and shown as its own small
    // layer ('crosshairLayer', true in 'crosshairShown') on the middle of the game's picture ('picture') - where the game really aims - at the picture's
    // own depth and scale, so it lines up exactly with what you see. Any failure just leaves the crosshair on the panel, as before.
    // M23-0 - A HUD PER EYE: in 3D (and "HUD eye pairing" not "One eye") the panel is two panels in the same place, each seen by one eye only, each copied from
    // the HUD sheet of the eye the pairing names (HudLayer.CopyTo with an eye) - so world markers sit at their true depth instead of doubling. 'layers' gets
    // the panel (or the left then the right panel), then the crosshair; 'count' says how many were filled.
    // Cost - CPU: low (two or three runtime calls and copy submits per headset frame); GPU: low (one screen-sized copy per panel per headset frame).
    static bool ShowHud(Xr.CompositionLayerQuad* layers, bool haveHead, in Xr.Posef head, Xr.CompositionLayerQuad* picture, out int count, out bool crosshairShown)
    {
        Xr.CompositionLayerQuad* layer = layers;
        count = 0;
        crosshairShown = false;
        bool perEye = Tuning.Stereo && Tuning.HudEyePairing != 2;                   // RA01: M23-0
        int leftFrom = Tuning.HudEyePairing == 1 ? 2 : 1, rightFrom = Tuning.HudEyePairing == 1 ? 1 : 2; // RA01: M23-0 - 1 = swapped: each eye gets the other eye's frame's HUD
        if (hudFailed || !Tuning.HudPanel || MenuSense.ShowsMenu || !PlayerSkeleton.InGame || !HudLayer.Ready(out int width, out int height, out int version)) { HudLayer.PanelLive = false; NoteHudLive(false); return false; }
        NoteHudLive(true);                                                            // RA01: M74 look
        HudLayer.PanelLive = true; // RA01: M10-2 - from here the HUD is taken onto our sheet; the panel itself waits until the sheet holds a finished HUD (no "TV fuzz")
        try
        {
            HudLayer.Tidy();                                                          // RA01: M23-3 - frees old HUD sheets after a resolution change
            if (version != hudChainVersion && hudChain != 0 && width == hudChainWidth && height == hudChainHeight) hudChainVersion = version; // RA01: M23-3 - a new HUD layer of the
                                                                                         // RA01: same size (the game makes one at nearly every menu switch): the panels are kept, not rebuilt
            if (hudChain == 0 || version != hudChainVersion)
            {
                if (screenFormat != 28 && screenFormat != 29) { hudFailed = true; HudLayer.PanelLive = false; Log.Write($"RA01 HUD panel: the headset image format {FrameCopy.FormatName(screenFormat)} can't take the HUD layer (needs RGBA8), so the HUD stays in the picture"); return false; }
                if (hudChain != 0) oldChains.Add((hudChain, Stopwatch.GetTimestamp() + Stopwatch.Frequency)); // RA01: the runtime may still show its last image a moment
                hudChain = CreateSwapchain(screenFormat, width, height, out hudTextures);
                hudFilled = new bool[hudTextures.Length];
                if (hudChain2 != 0) oldChains.Add((hudChain2, Stopwatch.GetTimestamp() + Stopwatch.Frequency)); // RA01: M23-0 - the right eye's panel, made with the left's
                hudChain2 = CreateSwapchain(screenFormat, width, height, out hudTextures2);
                hudFilled2 = new bool[hudTextures2.Length];
                hudChainVersion = version; hudChainWidth = width; hudChainHeight = height;
                Log.Write($"RA01 HUD panel: ready ({width}x{height}, {hudTextures.Length} images)");
                if (aimChain != 0) oldChains.Add((aimChain, Stopwatch.GetTimestamp() + Stopwatch.Frequency)); // RA01: M13-61 - the crosshair's chain follows the HUD's size
                aimSide = (int)(height * CrosshairShare) & ~1;
                aimChain = CreateSwapchain(screenFormat, aimSide, aimSide, out aimTextures);
                aimFilled = new bool[aimTextures.Length];
            }
            uint index = AcquireImage(hudChain);
            bool split = aimChain != 0 && menuBlend < 0.02f && GameAnchor.SceneTier < CutsceneTier && (!BlackboardProbe.OverlayShown || GameAnchor.InTrainingSim); // RA01: M21-10 - ALWAYS cut the centre square out in play (it was only while a crosshair
                                                                                         // RA01: was showing, !HideCrosshair || OnStandIn): with guns aimed physically HideCrosshair is on, yet the game
                                                                                         // RA01: still draws centre marks (the camera-view dot, scanner and interaction dots), and those rode along with
                                                                                         // RA01: a moved panel (HUD height / side) - off the true aim (Rob 2026-10-07, test B). An empty square costs nothing;
                                                                                         // RA01: M21-10 - but never in a cutscene (the game's scene tier 4-5, the same ones "VRIK in cutscenes" uses):
                                                                                         // RA01: there the centre can hold story pictures and on-screen text, which a moved panel would tear apart. It comes back by itself after.
                                                                                         // RA01: M21-11 - nor while the radio list, the car list, the phone or a popup is up (BlackboardProbe's whiteboard),
                                                                                         // RA01: for the same reason;
                                                                                         // RA01: M13-61 - cut it out onto its own layer;
                                                                                         // RA01: M20-8 - a stand-in body (the Militech sim, a gun always out, no phone) always cuts, so the scanner reticle sits on the true aim;
                                                                                         // RA01: M13-63 - only in play (a menu or loading screen IS the HUD picture there);
                                                                                         // RA01: M74 - in the Practice Makes Perfect training a popup doesn't stop it: its tutorial popups come and go
                                                                                         // RA01: every few seconds and each one left the crosshair riding the moved panel, off the true aim (Rob 2026-10-13)
            if (aimChain != 0 && menuBlend < 0.02f && split != splitWas) // RA01: M21-12 - log each change in play with its reason (menus are left out: they would flip it every visit)
            {
                splitWas = split;
                Log.Write($"RA01 crosshair cut-out: {(split ? "ON" : "off")} (scene tier {GameAnchor.SceneTier}, popup / phone {(BlackboardProbe.OverlayShown ? "shown" : "none")})");
            }
            uint aimIndex = split ? AcquireImage(aimChain) : 0;
            int fromEye = perEye ? leftFrom : 0;                                      // RA01: M23-0 - 0 = the newest sheet of any eye (2D, or "One eye")
            if (HudLayer.SheetFilled && (split ? HudLayer.CopySplitTo(hudTextures[index], aimTextures[aimIndex], aimSide, gameDevice, fromEye) : HudLayer.CopyTo(hudTextures[index], gameDevice, fromEye)))
            {
                hudFilled[index] = true; // RA01: when the GPU is busy a filled image keeps its last HUD picture, which is fine to show
                if (split) aimFilled[aimIndex] = true;
            }
            bool second = false;
            if (perEye && hudChain2 != 0)                                             // RA01: M23-0 - the right eye's panel (its centre cut out the same way; the crosshair image is simply refilled)
            {
                uint index2 = AcquireImage(hudChain2);
                if (HudLayer.SheetFilled && (split ? HudLayer.CopySplitTo(hudTextures2[index2], aimTextures[aimIndex], aimSide, gameDevice, rightFrom) : HudLayer.CopyTo(hudTextures2[index2], gameDevice, rightFrom)))
                    hudFilled2[index2] = true;
                ReleaseImage(hudChain2);
                second = hudFilled2[index2];
            }
            ReleaseImage(hudChain);
            if (split) ReleaseImage(aimChain);
            if (!hudFilled[index]) return false; // RA01: M10-2 - never show an image that has had no HUD copied in yet
            perEye = perEye && second;                                                // RA01: M23-0 - until the right panel has a HUD, one panel for both eyes as before
            Xr.CompositionLayerQuad* crosshairLayer = layers + (perEye ? 2 : 1);
            bool johnnyNoCentre = HudLayer.HideCrosshair && GameAnchor.InJohnnysBody && !BlackboardProbe.InBraindance && !GameAnchor.InTrainingSim; // RA01: M54-4 (M74: not the training) - Johnny with the crosshair rule "hidden": the
                                                                                         // RA01: square is still cut out of the panel but not drawn, so his crosshair (the game's Johnny HUD,
                                                                                         // RA01: which CrosshairProbe's switch never reaches) doesn't show. V: unchanged (always false).
                                                                                         // RA01: M72 - never in a braindance: its stand-in body is V's class too, so it looked like Johnny
                                                                                         // RA01: and the braindance's own crosshair / reticle was left out (Rob 2026-10-13).
            if (johnnyNoCentre != johnnyNoCentreWas) { johnnyNoCentreWas = johnnyNoCentre; Log.Write($"RA01 crosshair cut-out: centre square {(johnnyNoCentre ? "not drawn - Johnny's body, crosshair hidden (M54-4)" : "drawn again")}"); }
            if (split && aimFilled[aimIndex] && picture->Size.Width > 0 && !johnnyNoCentre)
            {
                *crosshairLayer = new Xr.CompositionLayerQuad
                {
                    Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha | Xr.LayerUnpremultipliedAlpha, EyeVisibility = Xr.EyeVisibilityBoth,
                    SubImage = { Swapchain = aimChain, ImageRect = { Extent = { Width = aimSide, Height = aimSide } } },
                    Space = picture->Space, Pose = picture->Pose,                    // RA01: the middle of the game's picture = where the game aims
                    Size = { Width = picture->Size.Width * aimSide / hudChainWidth, Height = picture->Size.Height * aimSide / hudChainHeight }
                };
                crosshairShown = true;
            }
        }
        catch (Exception e) { hudFailed = true; HudLayer.PanelLive = false; Log.Write($"RA01 HUD panel: switched off ({e.Message}); the HUD stays in the picture"); return false; }

        float panelWidth = Tuning.HudWidth, panelHeight = panelWidth * hudChainHeight / hudChainWidth;
        *layer = new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha | Xr.LayerUnpremultipliedAlpha, EyeVisibility = perEye ? 1 : Xr.EyeVisibilityBoth, // RA01: M23-0 - per eye: this one is the left eye's; M10-2 - unpremultiplied: the game's HUD pass leaves faint colour (a glitch/scanline effect) where alpha is 0, which a premultiplied panel added on top as a purple haze
            SubImage = { Swapchain = hudChain, ImageRect = { Extent = { Width = hudChainWidth, Height = hudChainHeight } } },
            Size = { Width = panelWidth, Height = panelHeight }
        };
        PlaceHud(layer, haveHead, head);
        count = 1;
        if (perEye)                                                                   // RA01: M23-0 - the right eye's panel: the same place and size, its own image
        {
            layers[1] = *layer;
            layers[1].EyeVisibility = 2;
            layers[1].SubImage.Swapchain = hudChain2;
            count = 2;
        }
        if (crosshairShown) count++;
        return true;
    }

    // RA01: M74 LOOK (log only) - each time the HUD panel goes live or stops: why, the menu blend, and the panel's size and place (breach protocol's tutorial
    // text is cut off at the bottom - is it on this panel?). At most MaxHudNotes lines a session. Cost - CPU: extremely low (a compare; a log line on a change); GPU: none.
    static void NoteHudLive(bool live)
    {
        if (live == hudLiveWas || hudNotes >= MaxHudNotes) return;
        hudLiveWas = live;
        hudNotes++;
        float height = Tuning.HudWidth * (hudChainWidth > 0 ? (float)hudChainHeight / hudChainWidth : 1f);
        Log.Write($"RA01 HUD panel (M74 look): {(live ? "LIVE - the HUD is on our panel" : "off - the HUD stays in the game's picture")} (menu shown {MenuSense.ShowsMenu}, menu blend {menuBlend:F2}, in game {PlayerSkeleton.InGame}, " +
            $"stand-in {GameAnchor.OnStandIn}); panel {Tuning.HudWidth:F2} x {height:F2} m at {Tuning.HudDistance:F2} m, moved {Tuning.HudHeight:F2} m up / {Tuning.HudSide:F2} m right, follows {(Tuning.HudFollow == 1 ? "V's body" : "the head (smooth)")}");
    }
    const int MaxHudNotes = 300;
    static bool hudLiveWas;
    static int hudNotes;

    // RA01: M10-1 - where the HUD panel stands. "Head (smooth)": in front of where your head points, but easing after it (a little lag, like a camera on a soft
    // spring), so glancing around doesn't shake it. "V's body": in front of where V's body faces (the recentre direction plus every turn body-follow has made),
    // level, riding along with your head's position. Before a recentre or without a head pose it floats fixed in front of the headset.
    // The panel is Distance ahead, Height up and Side to the right along its own facing, turned to face you: position = head + turn x (side, height, -distance).
    // Cost - CPU: extremely low (a few sines, cosines and multiplies per headset frame); GPU: none.
    static void PlaceHud(Xr.CompositionLayerQuad* layer, bool haveHead, in Xr.Posef head)
    {
        float distance = Tuning.HudDistance, up = Tuning.HudHeight, side = Tuning.HudSide; // RA01: M21-5 - side: + right
        if (!haveHead || !HeadPose.Ready || !HeadPose.YawOf(head.Orientation, out float headYaw))
        {
            layer->Space = viewSpace;
            layer->Pose = new Xr.Posef { Orientation = { W = 1 }, Position = { X = side, Y = up, Z = -distance } };
            hudAimed = false;
            return;
        }
        Xr.Quaternionf q = head.Orientation;
        float headPitch = MathF.Asin(Math.Clamp(2 * (q.W * q.X - q.Y * q.Z), -1f, 1f)); // RA01: how far the head looks up (the forward arrow's height)
        float yaw, pitch;
        if (Tuning.HudFollow == 1) { yaw = HeadPose.Yaw + CameraHook.FollowShift; pitch = 0; hudAimed = false; }
        else
        {
            if (!hudAimed) { hudYaw = headYaw; hudPitch = headPitch; hudAimed = true; }
            float turn = headYaw - hudYaw;
            turn -= 2 * MathF.PI * MathF.Round(turn / (2 * MathF.PI));
            hudYaw += turn * HudSmoothing;
            hudPitch += (headPitch - hudPitch) * HudSmoothing;
            yaw = hudYaw; pitch = hudPitch;
        }
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw), sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float y1 = up * cp + distance * sp, z1 = up * sp - distance * cp;  // RA01: (0, up, -distance) tipped up by the pitch...
        float x2 = side * cy + z1 * sy, z2 = -side * sy + z1 * cy;          // RA01: ...then (with the sideways step, which tipping up leaves alone) turned by the yaw (OpenXR: y up, forward is -z, left turns positive)
        float hy = MathF.Sin(yaw / 2), hc = MathF.Cos(yaw / 2), ps = MathF.Sin(pitch / 2), pc = MathF.Cos(pitch / 2);
        layer->Space = floorSpace;
        layer->Pose = new Xr.Posef
        {
            Orientation = { X = hc * ps, Y = hy * pc, Z = -hy * ps, W = hc * pc }, // RA01: yaw turn, then pitch turn
            Position = { X = head.Position.X + x2, Y = head.Position.Y + y1, Z = head.Position.Z + z2 }
        };
    }

    // RA01: M13-47 - notes which way the crosshair lies from your head, for AimProbe's aim line (Actions\FireArmAiming.cs decides when it is used). The game draws
    // its crosshair in the middle of its HUD, so with the panel up the crosshair is the panel's centre: the arrow from your head to it, turned back by your
    // head's own turn, is "where the crosshair lies as seen from inside your head" - like reading the angle of a gun sight off the helmet it's bolted to.
    // OpenXR's axes (x right, y up, -z ahead) become the view's (x right, y ahead, z up). Without a panel (HUD tab off, a menu) the crosshair is in the
    // middle of the picture: straight ahead. Three plain floats: a read torn across two frames mixes two nearly equal arrows, which is harmless.
    // Cost - CPU: extremely low (one vector turn and a square root per headset frame); GPU: none.
    static void NoteReticle(Xr.CompositionLayerQuad* panel, bool haveHead, in Xr.Posef head)
    {
        System.Numerics.Vector3 local = new(0, 0, -1);
        if (panel != null && panel->Space == viewSpace) local = new(panel->Pose.Position.X, panel->Pose.Position.Y, panel->Pose.Position.Z); // RA01: placed on the headset itself
        else if (panel != null && haveHead)
        {
            System.Numerics.Vector3 gap = new(panel->Pose.Position.X - head.Position.X, panel->Pose.Position.Y - head.Position.Y, panel->Pose.Position.Z - head.Position.Z);
            var q = head.Orientation;
            local = System.Numerics.Vector3.Transform(gap, System.Numerics.Quaternion.Conjugate(new(q.X, q.Y, q.Z, q.W)));
        }
        float length = local.Length();
        if (!(length > 0.01f)) return;
        reticleX = local.X / length; reticleY = -local.Z / length; reticleZ = local.Y / length;
    }

    // RA01: M13-47 - the latest NoteReticle arrow (unit length, the view's own axes: x right, y ahead, z up). Straight ahead until the headset has drawn a frame.
    // RA01: Cost - CPU: extremely low (three reads); GPU: none.
    public static System.Numerics.Vector3 ReticleLook => new(reticleX, reticleY, reticleZ);

    // RA01: M24-4 - THE HAND STRIP (the laser sight - see Core\HandStrip.cs): while Visuals holds one, its beam and dot panels hung on that controller's live pose
    // this frame, seen by both eyes. Its image is painted once, and again when the colour or brightness changes. Returns how many layers were filled (0 or 2).
    // Not over game menus. Any failure: logged, no laser for the rest of the session (the game and the rest of the headset view carry on).
    // Cost - CPU: extremely low per headset frame (HandStrip.Place; a paint and one small copy submit only on a colour change); GPU: extremely low (two tiny panels).
    static int ShowStrip(Xr.CompositionLayerQuad* layers, bool haveHead, in Xr.Posef head)
    {
        if (stripFailed || !haveHead || !Tuning.LaserSight || MenuSense.ShowsMenu || !HandStrip.TryRead(out int hand, out System.Numerics.Vector3 start, out System.Numerics.Vector3 direction, out bool toward)) return 0;
        if (!(hand == 0 ? Controllers.LeftGripPoseValid : Controllers.RightGripPoseValid)) return 0;
        Xr.Posef handPose = hand == 0 ? Controllers.LeftGripPose : Controllers.RightGripPose;
        try
        {
            if (stripChain == 0)
            {
                stripChain = CreateSwapchain(screenFormat, HandStrip.ImageWidth, HandStrip.ImageHeight, out stripTextures);
                Log.Write($"RA01 laser: ready ({HandStrip.ImageWidth}x{HandStrip.ImageHeight}, {stripTextures.Length} images)");
            }
            int look = HandStrip.Look;
            if (look != stripLook)
            {
                byte* pixels = FrameCopy.BeginSmall();
                if (pixels != null)                                                  // RA01: null = the GPU still reads the last one: tried again next frame
                {
                    HandStrip.Paint(pixels, FrameCopy.Bgra);
                    uint index = AcquireImage(stripChain);
                    bool copied = FrameCopy.EndSmall(stripTextures[index], screenFormat, HandStrip.ImageWidth, HandStrip.ImageHeight);
                    ReleaseImage(stripChain);
                    if (copied) stripLook = look;
                }
            }
        }
        catch (Exception e) { stripFailed = true; Log.Write($"RA01 laser: switched off for this session ({e.Message})"); return 0; }
        if (stripLook < 0 || !HandStrip.Place(handPose, head, start, direction, toward, out Xr.Posef beam, out float beamWidth, out float beamLength, out Xr.Posef dot, out float dotSize)) return 0;
        layers[0] = new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha | Xr.LayerUnpremultipliedAlpha, EyeVisibility = Xr.EyeVisibilityBoth,
            SubImage = { Swapchain = stripChain, ImageRect = { Extent = { Width = HandStrip.Half, Height = HandStrip.ImageHeight } } },
            Space = floorSpace, Pose = beam, Size = { Width = beamWidth, Height = beamLength }
        };
        layers[1] = layers[0];
        layers[1].SubImage.ImageRect.Offset.X = HandStrip.Half;                     // RA01: the image's right half: the dot
        layers[1].Pose = dot;
        layers[1].Size = new Xr.Extent2Df { Width = dotSize, Height = dotSize };
        return 2;
    }

    // RA01: M27-5c - THE MAG POUCH GLOW (Core\PouchGlow.cs): while Actions shows it, one round panel at your off-hand hip, hung from your live head this frame,
    // seen by both eyes. M27-6g - and, while a new magazine is carried, a second, much smaller panel at the gun's magazine well (where it goes in) -
    // the same painted ball (one image, two panels). Its image is painted once, and again when the colour changes. Returns how many layers were filled
    // (0-2). Not over game menus. Any failure: logged, no balls for the rest of the session (everything else carries on).
    // Cost - CPU: extremely low per headset frame (PouchGlow.TryPlace / TryPlaceWell; a paint and one small copy submit only on a colour change); GPU: extremely low (one or two small panels).
    static int ShowPouch(Xr.CompositionLayerQuad* layer, bool haveHead, in Xr.Posef head)
    {
        if (pouchFailed || !haveHead || !Tuning.PouchGlow || MenuSense.ShowsMenu) return 0;
        bool hip = PouchGlow.TryPlace(head, out Xr.Posef hipPose, out float hipSize), well = PouchGlow.TryPlaceWell(head, out Xr.Posef wellPose, out float wellSize);
        if (!hip && !well) return 0;
        try
        {
            if (pouchChain == 0)
            {
                pouchChain = CreateSwapchain(screenFormat, PouchGlow.ImageSize, PouchGlow.ImageSize, out pouchTextures);
                Log.Write($"RA01 mag pouch glow: ready ({PouchGlow.ImageSize}x{PouchGlow.ImageSize}, {pouchTextures.Length} images)");
            }
            int look = PouchGlow.Look;
            if (look != pouchLook)
            {
                byte* pixels = FrameCopy.BeginSmall();
                if (pixels != null)                                                  // RA01: null = the GPU still reads the last one: tried again next frame
                {
                    PouchGlow.Paint(pixels, FrameCopy.Bgra);
                    uint index = AcquireImage(pouchChain);
                    bool copied = FrameCopy.EndSmall(pouchTextures[index], screenFormat, PouchGlow.ImageSize, PouchGlow.ImageSize);
                    ReleaseImage(pouchChain);
                    if (copied) pouchLook = look;
                }
            }
        }
        catch (Exception e) { pouchFailed = true; Log.Write($"RA01 mag pouch glow: switched off for this session ({e.Message})"); return 0; }
        if (pouchLook < 0) return 0;
        int n = 0;
        if (hip) layer[n++] = BallLayer(hipPose, hipSize);
        if (well) layer[n++] = BallLayer(wellPose, wellSize);
        return n;
    }

    // RA01: M76 - THE UNARMED DOT (Core\UnarmedDot.cs): while Actions says you're unarmed (UnarmedDot.Wanted), one tiny panel at the centre of the game's picture -
    // the same place and depth the crosshair cut-out uses, so it sits exactly where the game aims - only in play: not over a game menu, a cutscene (CutsceneTier)
    // or a popup / phone. Painted once. Returns true when the layer was filled. Any failure: logged, no dot this session (everything else carries on).
    // Cost - CPU: extremely low per headset frame (a few compares and one struct fill; one paint and copy, once); GPU: extremely low (one tiny panel).
    static bool ShowDot(Xr.CompositionLayerQuad* layer, Xr.CompositionLayerQuad* picture)
    {
        bool show = !dotFailed && UnarmedDot.Wanted && !MenuSense.ShowsMenu && menuBlend < 0.02f && GameAnchor.SceneTier < CutsceneTier && !BlackboardProbe.OverlayShown
                    && picture->Size.Height > 0;
        if (show != dotWas) { dotWas = show; Log.Write(show ? "RA01 unarmed dot: shown (nothing in hand - the game draws no crosshair) (M76)" : "RA01 unarmed dot: hidden (M76)"); }
        if (!show) return false;
        try
        {
            if (dotChain == 0)
            {
                dotChain = CreateSwapchain(screenFormat, UnarmedDot.ImageSize, UnarmedDot.ImageSize, out dotTextures);
                Log.Write($"RA01 unarmed dot: ready ({UnarmedDot.ImageSize}x{UnarmedDot.ImageSize}, {dotTextures.Length} images)");
            }
            if (!dotPainted)
            {
                byte* pixels = FrameCopy.BeginSmall();
                if (pixels != null)                                                  // RA01: null = the GPU still reads the last small image: tried again next frame
                {
                    UnarmedDot.Paint(pixels, FrameCopy.Bgra);
                    uint index = AcquireImage(dotChain);
                    dotPainted = FrameCopy.EndSmall(dotTextures[index], screenFormat, UnarmedDot.ImageSize, UnarmedDot.ImageSize);
                    ReleaseImage(dotChain);
                }
            }
        }
        catch (Exception e) { dotFailed = true; Log.Write($"RA01 unarmed dot: switched off for this session ({e.Message})"); return false; }
        if (!dotPainted) return false;
        float size = 2 * ScreenDistance * MathF.Tan(UnarmedDot.SizeDeg * (MathF.PI / 360f));
        *layer = new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha | Xr.LayerUnpremultipliedAlpha, EyeVisibility = Xr.EyeVisibilityBoth,
            SubImage = { Swapchain = dotChain, ImageRect = { Extent = { Width = UnarmedDot.ImageSize, Height = UnarmedDot.ImageSize } } },
            Space = picture->Space, Pose = picture->Pose,                            // RA01: the middle of the game's picture = where the game aims
            Size = { Width = size, Height = size }
        };
        return true;
    }

    // RA01: M27-6g - one ball panel (the pouch glow's image) at this pose and size, in the room. Cost - CPU: extremely low (one struct fill); GPU: none here.
    static Xr.CompositionLayerQuad BallLayer(in Xr.Posef pose, float size) => new()
    {
        Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha | Xr.LayerUnpremultipliedAlpha, EyeVisibility = Xr.EyeVisibilityBoth,
        SubImage = { Swapchain = pouchChain, ImageRect = { Extent = { Width = PouchGlow.ImageSize, Height = PouchGlow.ImageSize } } },
        Space = floorSpace, Pose = pose, Size = { Width = size, Height = size }
    };

    // RA01: M27-1 - THE BANNER (Core\Banner.cs): while a message is up, one panel fixed to your view; its image is painted again only for a new message.
    // Returns true when the layer was filled. Any failure: logged, no banners this session (everything else carries on).
    // Cost - CPU: extremely low per headset frame (a lock and a compare; one paint and one upload per message); GPU: extremely low (one panel).
    static bool ShowBanner(Xr.CompositionLayerQuad* layer)
    {
        if (bannerFailed || !Banner.TryCurrent(out string text, out int version)) return false;
        try
        {
            if (bannerChain == 0) bannerChain = CreateSwapchain(screenFormat, Banner.Width, Banner.Height, out bannerTextures);
            if (version != bannerVersion)
            {
                byte* pixels = FrameCopy.BeginSmall();
                if (pixels == null) return false;                                   // RA01: the GPU still reads the last upload: next frame
                bool painted;
                lock (MenuDraw.Gate) painted = MenuDraw.PaintBanner((uint*)pixels, Banner.Width, Banner.Height, text, FrameCopy.Bgra); // RA01: M29-1 - one painter at a time
                if (!painted) { bannerFailed = true; return false; }
                uint index = AcquireImage(bannerChain);
                bool copied = FrameCopy.EndSmall(bannerTextures[index], screenFormat, Banner.Width, Banner.Height);
                ReleaseImage(bannerChain);
                if (!copied) return false;
                bannerVersion = version;
            }
        }
        catch (Exception e) { bannerFailed = true; Log.Write($"RA01 banner: switched off for this session ({e.Message})"); return false; }
        *layer = new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha, EyeVisibility = Xr.EyeVisibilityBoth, Space = viewSpace,
            SubImage = { Swapchain = bannerChain, ImageRect = { Extent = { Width = Banner.Width, Height = Banner.Height } } },
            Pose = new Xr.Posef { Orientation = { W = 1 }, Position = { Y = Banner.PanelUpM, Z = -Banner.PanelAheadM } },
            Size = { Width = Banner.PanelWidthM, Height = Banner.PanelWidthM * Banner.Height / Banner.Width }
        };
        return true;
    }

    // RA01: M5a-3 - fills in the window's layer and returns true when it is open and has a picture. A new picture is painted only when something in the window changed (SettingsMenu.NeedsDraw).
    // Cost - CPU: extremely low on most frames (a few flag checks, and nothing at all while the window is closed); the repaint cost is in DrawMenu, only when something in the window changed;
    // GPU: none of its own (the runtime draws the quad).
    static bool ShowMenu(Xr.CompositionLayerQuad* layer)
    {
        if (!SettingsMenu.Open || menuChain == 0 || menuFailed) return false;
        if (SettingsMenu.NeedsDraw() || !menuShown) DrawMenu();
        else if (Tuning.MenuOnMonitor) lock (MenuDraw.Gate) MenuDraw.ToMonitor();  // RA01: M29-1 - a monitor copy that was busy last time is retried
        if (!menuShown) return false;
        *layer = new Xr.CompositionLayerQuad
        {
            Type = Xr.TypeCompositionLayerQuad, LayerFlags = Xr.LayerBlendTextureSourceAlpha, Space = menuSpace, EyeVisibility = Xr.EyeVisibilityBoth,
            SubImage = { Swapchain = menuChain, ImageRect = { Extent = { Width = SettingsMenu.PanelWidth, Height = SettingsMenu.PanelHeight } } },
            Pose = menuPose, Size = { Width = MenuPanelWidth, Height = MenuPanelWidth * SettingsMenu.PanelHeight / SettingsMenu.PanelWidth }
        };
        return true;
    }

    // RA01: paints the window into an upload buffer and copies it into the next image of the window's swapchain. Nothing happens when the GPU is still busy with the last one (it is tried again next frame).
    // Cost - CPU: medium when it runs (the window is painted pixel by pixel on the CPU into an upload buffer, up to about a millisecond), but only while the window is open and something in it changed;
    // GPU: low (one small panel-sized texture copy).
    static void DrawMenu()
    {
        byte* pixels = FrameCopy.BeginMenu();
        if (pixels == null)
        {
            if (FrameCopy.MenuLost) FailMenu(); // RA01: nothing will ever paint it again (the reason is in the log)
            return;                             // RA01: otherwise the GPU is still busy: tried again next frame
        }
        lock (MenuDraw.Gate)                                                  // RA01: M29-1 - one painter at a time (FlatMenu)
        {
            if (!MenuDraw.Paint((uint*)pixels, FrameCopy.Bgra)) { FailMenu(); return; }
            if (Tuning.MenuOnMonitor) MenuDraw.ToMonitor();                    // RA01: M29-1 - and the same picture to the monitor
        }
        uint index = AcquireImage(menuChain);
        bool copied = FrameCopy.EndMenu(menuTextures[index]);
        ReleaseImage(menuChain);
        menuShown = copied; // RA01: an image that got no picture is not shown (it is painted again next frame)
        if (copied) SettingsMenu.MarkDrawn();
    }

    // RA01: the window cannot be shown: it is closed, the mouse goes back to the game, and F10 says it is not available from now on.
    // Cost - CPU: extremely low (runs at most once per session, only when painting the window has failed for good); GPU: none.
    static void FailMenu()
    {
        menuFailed = true;
        SettingsMenu.Toggle();
        MenuInput.Stop();
    }

    // RA01: the bindings are in controls_mapping\Keys.cs. Home recentres, F10 opens the settings window (M5a-3), End switches head steering on or off.
    // M5a-12 - stereo, swap eyes, the game menu view and every tuning value used to also have a direct key; those keys are gone, so all of that is now only ever changed from the F10 menu (SettingsMenu).
    // Cost - CPU: low (once per headset frame: a few key state checks and the settings-save poll, and, while the window is open, the mouse moves, tens of microseconds); GPU: none.
    static void PollKeys()
    {
        long now = Stopwatch.GetTimestamp();

        if (Keys.Pressed(Keys.Recentre)) recentreWanted = true;
        if (Keys.Pressed(Keys.ToggleSteering))
        {
            HeadPose.Steering = !HeadPose.Steering;
            Log.Write($"RA01 head steering is now {(HeadPose.Steering ? "ON: the head steers the game's camera and the picture stands in the room where the head was when the game built it" : "OFF: the game's camera is its own and the picture stands in the room in front of the recentre point")} (End)");
        }
        bool gestureToggle = MenuPointer.TogglePressed(); // RA01: M6-1 - always called, so its edge-latch stays current, whether or not F10 was also pressed this frame
        if (Keys.Pressed(Keys.Menu) || gestureToggle) ToggleMenu();
        if (Keys.Pressed(Keys.BoneFind)) BoneFinder.Start(); // RA01: M6-6 - F11, see BoneFinder.cs
        BoneFinder.Poll(now); // RA01: M6-6 - does nothing unless a hunt's narrowing window is open
        if (SettingsMenu.Open)
        {
            // RA01: the VR pointer goes first, and reports back whether the pointing hand was tracked at all. Only when it was NOT do we let the mouse take this frame instead -
            // RA01: M6-2: they used to both run every frame no matter what, and since the mouse button is almost always up, that told SettingsMenu "the mouse just let go" one
            // RA01: instant after the pointer's own trigger-down had already grabbed a slider - a real release, not a flicker, just repeated every headset frame, so a slider held
            // RA01: with the pointer could never actually be dragged (see MenuPointer's class comment for the fuller explanation). Skipped in the rare fallback where the panel
            // RA01: had to be placed in view space instead (no head pose when it opened) - the floor-space hand poses can't be measured against a view-space plane, so the mouse
            // RA01: is the only way in then, same as before.
            bool pointerTracked = menuSpace == floorSpace && MenuPointer.Update(menuPose, MenuPanelWidth, MenuPanelWidth * SettingsMenu.PanelHeight / SettingsMenu.PanelWidth);
            if (!pointerTracked)
            {
                MenuInput.TakeMoves(out int dx, out int dy);
                SettingsMenu.Input(dx, dy, Keys.Held(Keys.MouseLeft));
            }
            MenuPointer.StickScroll(); // RA01: M8-3 - either thumbstick up/down scrolls the list
            if (SettingsMenu.TakeRecentre()) recentreWanted = true;
            if (!SettingsMenu.Open) MenuInput.Stop(); // RA01: closed with the X
        }
        if (Tuning.SwapEyes != swapSeen)
        {
            swapSeen = Tuning.SwapEyes;
            Array.Clear(lastPicture); // RA01: the images in the swapchains were chosen for the other way round
        }
        SettingsFile.Poll(now); // RA01: M5a-2 - saves the values a moment after they stop changing
    }

    // RA01: where the head is right now, measured in the floor space. False when the runtime cannot say (headset off, tracking lost).
    // 'report' false is for the extra look ahead: its failure is not logged and does not touch the note for the main one.
    // Cost - CPU: extremely low (up to twice per headset frame: one xrLocateSpace call, a few microseconds, and a flag test); GPU: none.
    static bool LocateHead(long displayTime, out Xr.Posef head, out ulong flags, bool report = true)
    {
        Xr.SpaceLocation location = new() { Type = Xr.TypeSpaceLocation };
        int result = Xr.LocateSpace(viewSpace, floorSpace, displayTime, &location);
        if (result < 0) // RA01: this runs every frame, so one bad answer must not end the headset session
        {
            if (report && !locateFailLogged) Log.Write($"RA01 OpenXR: xrLocateSpace failed ({result}), the head is treated as not located until it works again");
            if (report) locateFailLogged = true;
            head = default;
            flags = 0;
            return false;
        }
        if (report) locateFailLogged = false;
        head = location.Pose;
        flags = location.LocationFlags;
        const ulong both = Xr.SpaceLocationOrientationValid | Xr.SpaceLocationPositionValid;
        return (flags & both) == both;
    }

    // RA01: the head's spot and facing become the centre, and its height above the floor is remembered (seated and standing both work).
    // if the head looks straight up or down there is no facing to take, so the wish stays until the next level look.
    // Cost - CPU: extremely low (runs only when a recentre is wanted: Home, the runtime's own recentre or the first focus; a little arithmetic and one log line); GPU: none.
    static void RecentreNow(in Xr.Posef head)
    {
        if (!HeadPose.Recentre(head))
        {
            if (!waitingLogged) Log.Write("RA01 recentre is waiting: look straight ahead, not up or down");
            waitingLogged = true;
            return;
        }
        CameraHook.NoteRecentre(); // RA01: M6-0 - the body-follows-head gap resets alongside the head's own recentre, so no leftover catch-up turn fires right after this
        recentreWanted = false;
        waitingLogged = false;
        Log.Write($"RA01 recentred: head {HeadPose.EyeHeight:F2} m above the floor at ({HeadPose.OriginX:F2}, {HeadPose.OriginZ:F2}) m, facing {HeadPose.Yaw * (180f / MathF.PI):F0} degrees (left of the space's own forward is positive), "
            + $"floor at height {HeadPose.FloorY:F2} m ({(HeadPose.FloorAssumed ? "ESTIMATED, the runtime has no floor" : "from the runtime's STAGE space")}), screen {ScreenDistance} m ahead with its bottom edge on the floor when head steering is off");
    }

    // RA01: every 10 seconds, write where each eye is (in the floor space, so the height is above the floor) and where the head is compared
    // with the recentre point. Move your head and the numbers must change, and the height can be checked with a tape measure.
    // Cost - CPU: extremely low (every headset frame only a clock check; every 10 seconds one xrLocateViews call and a few log lines of string formatting, tens of microseconds); GPU: none.
    static void LogPose(long displayTime, bool haveHead, in Xr.Posef head, ulong headFlags)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < nextPoseLog) return;
        nextPoseLog = now + 10 * Stopwatch.Frequency; // RA01: M4a - every 10th second is enough now, to keep the log short

        Xr.ViewLocateInfo locateInfo = new() { Type = Xr.TypeViewLocateInfo, ViewConfigurationType = Xr.ViewConfigurationPrimaryStereo, DisplayTime = displayTime, Space = floorSpace };
        Xr.ViewState viewState = new() { Type = Xr.TypeViewState };
        Xr.View* eyes = stackalloc Xr.View[2];
        eyes[0] = new() { Type = Xr.TypeView };
        eyes[1] = new() { Type = Xr.TypeView };
        uint count;
        Check(Xr.LocateViews(session, &locateInfo, &viewState, 2, &count, eyes), "xrLocateViews");
        if (count < 2) { Log.Write($"RA01 OpenXR pose: only {count} eye(s) reported"); return; }

        Xr.Vector3f left = eyes[0].Pose.Position, right = eyes[1].Pose.Position;
        float dx = right.X - left.X, dy = right.Y - left.Y, dz = right.Z - left.Z;
        float ipdMm = MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 1000f;
        Xr.Fovf fov = eyes[0].Fov;
        const float degrees = 180f / MathF.PI;
        Log.Write($"RA01 OpenXR pose: left eye ({left.X:F3}, {left.Y:F3}, {left.Z:F3}) m, right eye ({right.X:F3}, {right.Y:F3}, {right.Z:F3}) m, "
            + $"IPD {ipdMm:F1} mm, tracking flags 0x{viewState.ViewStateFlags:X}, left eye field of view L{fov.AngleLeft * degrees:F0} R{fov.AngleRight * degrees:F0} U{fov.AngleUp * degrees:F0} D{fov.AngleDown * degrees:F0} degrees");

        if (!haveHead) { Log.Write($"RA01 OpenXR head: not located (space flags 0x{headFlags:X})"); return; }
        if (!HeadPose.Ready)
        {
            Log.Write(HeadPose.FloorAssumed ? "RA01 OpenXR head: not recentred yet, so no floor estimate yet (Home recentres)" : $"RA01 OpenXR head: {head.Position.Y:F2} m above the floor, not recentred yet (Home recentres)");
            return;
        }
        HeadPose.Relative(head, out float ahead, out float aside, out float height, out float turnedLeft);
        string turn = float.IsNaN(turnedLeft) ? "looking straight up or down" : $"turned {turnedLeft:F0} degrees to the left (negative = right)";
        Log.Write($"RA01 OpenXR head: {height:F2} m above the {(HeadPose.FloorAssumed ? "estimated floor" : "floor")}, {ahead:F2} m forward and {aside:F2} m right of the recentre point, {turn}; head steering {(HeadPose.Steering ? "ON" : "off")}");
        Log.Write($"RA01 picture: {pictureMode}; sized for a field of view of {pictureFov:F1} degrees (the game says {CameraHook.Fov:F1}) times {Tuning.SizeScale:F2}, game menu {menuBlend * 100:F0}% (size {Tuning.GameMenuFov:F0} degrees), moved {Tuning.ShiftRightDeg:F1} right / {Tuning.ShiftUpDeg:F1} up "
            + $"(left eye trim {Tuning.LeftEyeShiftRightDeg:F1} right / {Tuning.LeftEyeShiftUpDeg:F1} up, right eye trim {Tuning.RightEyeShiftRightDeg:F1} right / {Tuning.RightEyeShiftUpDeg:F1} up), AutoFov {(Tuning.AutoFov ? "ON" : "off")}; "
            + $"head prediction {Tuning.PredictionMs:F0} ms ({(predicting ? "in use" : "NOT in use")}), frame lag {Tuning.FrameLag}, {(Tuning.IpdMm == 0 ? "IPD the headset's own" : $"IPD {Tuning.IpdMm:F1} mm")}; the newest game picture was {pickupAgeMs:F1} ms old when the headset took it");
        LogStereo(now);
    }

    // RA01: M5a - how each eye is doing over the time since the last line: new pictures per second (about half the game's frame rate each, while the eyes alternate),
    // and headset frames when no picture built for that eye could be found (a lot of those means the frame lag is off, or the eyes need swapping with /).
    // Cost - CPU: extremely low (runs every 10 seconds, called from LogPose: a little arithmetic and one log line); GPU: none.
    static void LogStereo(long now)
    {
        double seconds = Math.Max((now - statsSince) / (double)Stopwatch.Frequency, 0.001);
        statsSince = now;
        // RA01: builds the log text for one eye: new pictures a second and the headset frames without a picture built for it.
        // RA01: Cost - CPU: extremely low (one string format, two or three times every 10 seconds); GPU: none.
        string Text(int eye) => $"{freshPictures[eye] / seconds:F1} new pictures a second, {missedFrames[eye]} headset frames without one built for it";
        if (wasStereo)
        {
            Xr.Vector3f a = EyeLocal(1), b = EyeLocal(2);
            float spacing = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z)) * 1000f;
            Log.Write($"RA01 stereo ON{(Tuning.SwapEyes ? " (eyes swapped)" : "")}: left eye {Text(1)}; right eye {Text(2)}; the game's camera moves {spacing:F1} mm between the eyes");
            LogEyeTiming();                                                       // RA01: M63
        }
        else Log.Write($"RA01 stereo {(Tuning.Stereo ? "ON, but no head-steered picture right now (waiting for the head steering and a recentre, or a menu or loading screen)" : "OFF")}: both eyes see one picture, {Text(0)}");
        Array.Clear(freshPictures);
        Array.Clear(missedFrames);
    }

    // RA01: M63 - LOOK ONLY (no behaviour change): the left eye blurs and doubles under SteamVR when the head moves (Rob 23:13). The eyes' pictures are drawn
    // in turn, so they come from slightly different moments; each headset frame with the projection layer this adds up, per eye, how old its picture is
    // and how far the head has turned since that picture was drawn (its tagged pose against the head now), plus how far apart the two pictures were
    // drawn. Like timing two runners' laps separately instead of only the leader's: it shows whether one eye is always the stale one.
    // Cost - CPU: extremely low (per headset frame: a few subtractions and three quaternion dot products); GPU: none.
    static void NoteEyeTiming(Xr.Posef head)
    {
        long now = Stopwatch.GetTimestamp();
        double gapMs = Math.Abs(lastPicture[1] - lastPicture[2]) * 1000.0 / Stopwatch.Frequency;
        if (lastPicture[1] == 0 || lastPicture[2] == 0 || gapMs > 200) return;
        for (int eye = 1; eye <= 2; eye++)
        {
            eyeAgeMs[eye] += (now - lastPicture[eye]) * 1000.0 / Stopwatch.Frequency;
            float turned = TurnDegrees(exactPose[eye].Orientation, head.Orientation);
            eyeTurned[eye] += turned;
            if (turned > eyeTurnedMost[eye]) eyeTurnedMost[eye] = turned;
        }
        eyeGapMs += gapMs;
        eyeGapTurned += TurnDegrees(exactPose[1].Orientation, exactPose[2].Orientation);
        eyeTimingFrames++;
    }

    // RA01: M63 - the angle between two turns, in degrees (how far one has to turn to become the other).
    // Cost - CPU: extremely low (one dot product and an arc cosine); GPU: none.
    static float TurnDegrees(Xr.Quaternionf a, Xr.Quaternionf b)
    {
        float dot = MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        return 2f * MathF.Acos(MathF.Min(dot, 1f)) * (180f / MathF.PI);
    }

    // RA01: M63 - one line with the averages since the last stereo line, then starts again. Nothing is written without projection-layer frames.
    // Cost - CPU: extremely low (every 10 seconds: a little arithmetic and one log line); GPU: none.
    static void LogEyeTiming()
    {
        if (eyeTimingFrames > 0)
        {
            double n = eyeTimingFrames;
            Log.Write($"RA01 eye timing (M63): left eye picture {eyeAgeMs[1] / n:F1} ms old when shown, head turned {eyeTurned[1] / n:F2} degrees since it was drawn (most {eyeTurnedMost[1]:F1}); "
                + $"right eye {eyeAgeMs[2] / n:F1} ms, {eyeTurned[2] / n:F2} degrees (most {eyeTurnedMost[2]:F1}); the two pictures drawn {eyeGapMs / n:F1} ms apart, head turned {eyeGapTurned / n:F2} degrees between them; {eyeTimingFrames} headset frames");
        }
        Array.Clear(eyeAgeMs); Array.Clear(eyeTurned); Array.Clear(eyeTurnedMost);
        eyeGapMs = eyeGapTurned = 0; eyeTimingFrames = 0;
    }
    static readonly double[] eyeAgeMs = new double[3], eyeTurned = new double[3];
    static readonly float[] eyeTurnedMost = new float[3];
    static double eyeGapMs, eyeGapTurned;
    static int eyeTimingFrames;

    // RA01: hand everything back to the runtime in reverse order. Errors here are ignored, the game is usually closing anyway.
    // Cost - CPU: low (runs once when the session ends: a handful of destroy calls; the 250 ms sleep is waiting, not CPU work); GPU: low (frees the swapchain images once, after the last copy has finished).
    static void Cleanup()
    {
        try
        {
            // RA01: stop the copying first and give the GPU a moment to finish any copy into the headset images before they are destroyed.
            FrameCopy.Disable("OpenXR session ended");
            SettingsFile.Flush(); // RA01: M5a-2 - a change made in the last half second is written now
            MenuInput.Uninstall(); // RA01: M5a-3 - the game window's message handler is put back
            Thread.Sleep(250);
            foreach (ulong chain in swapchains) if (chain != 0) Xr.DestroySwapchain(chain);
            foreach (var old in oldChains) Xr.DestroySwapchain(old.Chain);
            if (menuChain != 0) Xr.DestroySwapchain(menuChain);
            foreach (ulong chain in lensChains) if (chain != 0) Xr.DestroySwapchain(chain); // RA01: M16-A
            Controllers.Cleanup(); // RA01: M6-0 - its action spaces, actions and set are all session-scoped, so this must run before the session itself is destroyed below
            if (viewSpace != 0) Xr.DestroySpace(viewSpace);
            if (floorSpace != 0) Xr.DestroySpace(floorSpace);
            if (session != 0) Xr.DestroySession(session);
            if (instance != 0) Xr.DestroyInstance(instance);
        }
        catch { }
    }

    // RA01: a negative result is a failure. The runtime's own name for it (like XR_ERROR_RUNTIME_UNAVAILABLE) goes into the message.
    // Cost - CPU: extremely low (called after every OpenXR call, a dozen or more per headset frame: one comparison, the name lookup and the throw happen only on a failure); GPU: none.
    static void Check(int result, string what)
    {
        if (result >= 0) return;
        string name = result.ToString();
        if (Xr.ResultToString != null && instance != 0)
        {
            byte* text = stackalloc byte[64]; // RA01: XR_MAX_RESULT_STRING_SIZE
            if (Xr.ResultToString(instance, result, text) >= 0) name = Xr.GetText(text);
        }
        throw new InvalidOperationException($"{what} failed: {name} ({result})");
    }

    // RA01: the folder RA01.dll was loaded from, found by asking Windows which module owns one of our own functions.
    // Cost - CPU: extremely low (runs a few times at startup: two Windows calls and a string copy); GPU: none.
    public static string OwnFolder()
    {
        Win32.GetModuleHandleExW(0x6, (nint)(delegate* unmanaged<nint, uint>)&Entry.Start, out nint self); // RA01: 0x6 = look up by address, do not add a reference
        char* buffer = stackalloc char[1024];
        string path = new(buffer, 0, (int)Win32.GetModuleFileNameW(self, buffer, 1024));
        return Path.GetDirectoryName(path) ?? "";
    }
}
