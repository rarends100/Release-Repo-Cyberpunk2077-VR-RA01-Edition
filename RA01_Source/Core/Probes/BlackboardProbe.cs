using static RA01.Core.Win32;

namespace RA01.Core;

// RA01: M22-0 - THE BLACKBOARD PROBE (LOG ONLY, Dev row "Blackboard probe (log)"). The game keeps many of its "what is going on" switches on shared notice
// boards called blackboards, not on V herself: the radio list (UI_System.Popup_Radio_IsShown), the car list (Popup_VehiclesManager_IsShown), any popup
// (Popup_IsShown), the phone, and swimming (PlayerStateMachine.Swimming: surface / diving). M22's swimming, throws and the crosshair cut-out rule need them.
// Every value written to any blackboard - by the game's scripts or its C++ - passes through one of ten small "set a typed value" helpers (2.31: Bool at
// exe+0x32BD8C, Int at +0x1B93BC, and eight more types), all with the same first 47 bytes. Each gets a tiny MACHINE-CODE detour (no C# on the game's
// threads at all - see the M21-8 stack-overflow note in the Known Quirks): it copies four numbers - which board, which field (its 8-byte id), the first
// 8 bytes of the new value, and which helper - into a ring of 8192 slots, then runs the helper's own first two instructions and jumps back.
// A background thread (only while the Dev row is on) reads the ring 20 times a second and logs each field whose value's first byte changed (bools and the
// small enums such as Swimming fit in one byte). Fields that change all the time (health, timers) are logged once as noisy and then dropped. The first
// 3 seconds after switching on only learn (no lines), so the log starts quiet; then open the phone, the radio, the car list, swim - each shows as a line.
// M22-0b - the first test (Rob, 2026-10-07 08:47) showed the field ids ARE the field names, hashed the game's usual way (FNV-1a 64, GameAnchor.NameCode):
// Popup_Radio_IsShown, Popup_VehiclesManager_IsShown and the phone's ContactsActive were all read straight off. It also showed why that run hit its 3000-line
// cap in two minutes: every NPC has its own boards (IsInWater, HighLevel on 40-60 of them), each logged as NEW. So the reader now logs only a WATCH LIST of
// named fields, NEW only when not zero, and the player-state-machine fields (HighLevel, Swimming, Locomotion...) only from V's own board - the board that
// writes UpperBody / LocomotionDetailed, which only the player's state machine has. Changes nothing in the game.
// M21-11 - THE OVERLAY WHITEBOARD (always on, Rob 2026-10-07): the detours now go in at start-up and the reader always runs. Besides the log (still only
// while the Dev row is on) it keeps the latest value of four switches - the radio list, the car list, the phone's contacts and "a popup is shown" - and
// OverlayShown is true while any is on. XrSession reads it: no crosshair centre cut-out over those screens (a moved panel would tear them apart). The
// whiteboard is wiped when V's own board changes (a save loaded), so a popup that was open at a load can't leave the cut-out off for good.
// M21-13 - a fifth switch: the radial wheel (UI_System.UIRadialContextRequest - the weapon wheel held on Y, Rob 2026-10-07: it had the hole in its middle).
// M22-1 - the whiteboard also keeps V's swimming (SwimState, for Actions\PhysicalSwim.cs): HighLevel 6 = in the water, Swimming 1 surface / 2 diving
// (gamePSMHighLevel / gamePSMSwimming in the RED4ext SDK reference; Rob's test 2026-10-07 12:39). And V's board is now marked by LocomotionDetailed ONLY:
// in that test another board wrote UpperBody (5, ForceEmptyHands) and was taken for V's for a second.
// M23-2 - and V's device switches (PlayerStateMachine, names from the game's scripts): IsUIZoomDevice (the view zoomed onto a device screen - a PC),
// IsInteractingWithDevice, IsControllingDevice (a camera or turret taken over). AtDeviceScreen (IsUIZoomDevice) lets XrSession flatten the view there:
// a PC's pointer is placed from the game camera, one eye late in turns, so it jumped between two spots on the in-world monitor (Rob 2026-10-07).
// M24-1 - BRAINDANCE AND TAKEN-OVER CAMERAS (for the right stick's look and the camera height pause, RemoteView): V's IsControllingDevice (a camera
// or turret taken over) from her own board, and the braindance board's IsActive. "IsActive" is a common name, so it only counts on the board that also
// writes the braindance's own fields (IsFPP, EnableExit, PlaybackSpeed, PlaybackDirection, SectionTime - from the game's BraindanceBlackboardDef, its
// scripts). Each board's last IsActive is kept, so a braindance whose IsActive came before its other fields is still caught. Logged on every change.
// M24-3 - and V's LocomotionDetailed itself (Locomotion: 10 Ladder, 11 LadderSprint, 12 LadderSlide, 13 LadderJump - gamePSMDetailedLocomotionStates).
// Builder picture: a carbon-copy sheet under every notice board's pen - whatever anyone writes, a copy drops in our tray; we only read the tray.
// Cost - CPU: extremely low per blackboard write (about 20 machine instructions, a few thousand writes a second at most); the reader: low (a short loop
// 20 times a second, a dictionary lookup per write; a few log lines more while the Dev row is on); GPU: none.
internal static unsafe class BlackboardProbe
{
    // RA01: the shared first bytes of the ten typed setters: mov r11,rsp / mov [r11+8],rbx / mov [r11+10h],rsi / mov [r11+18h],rdi / push r14 / sub rsp,40h /
    // RA01: mov rax,[rcx] / mov rsi,rdx / mov r14,rcx / mov rdx,r8 / lea rcx,[r11-28h] / mov dil,r9b / mov rbx,[rax+110h] (the board's own "set" slot) / call.
    static ReadOnlySpan<byte> Setter => new byte[] { 0x4C, 0x8B, 0xDC, 0x49, 0x89, 0x5B, 0x08, 0x49, 0x89, 0x73, 0x10, 0x49, 0x89, 0x7B, 0x18, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x40,
        0x48, 0x8B, 0x01, 0x48, 0x8B, 0xF2, 0x4C, 0x8B, 0xF1, 0x49, 0x8B, 0xD0, 0x49, 0x8D, 0x4B, 0xD8, 0x41, 0x8A, 0xF9, 0x48, 0x8B, 0x98, 0x10, 0x01, 0x00, 0x00, 0xE8 };
    const int Replaced = 7;                 // RA01: the two instructions the jump covers (mov r11,rsp / mov [r11+8],rbx), run again in the detour

    // RA01: the detour, checked with a disassembler (capstone) before delivery:
    // RA01:  00 xor r10d,r10d / test rdx,rdx / je +3 / mov r10,[rdx]        - the field id (0 if no pointer)
    // RA01:  0B mov r11, <ring>  (8 bytes at +0x0D)
    // RA01:  15 mov eax,1 / lock xadd [r11],eax / and eax,1FFFh / shl eax,5 / add rax,r11   - take the next slot (32 bytes each, after a 0x40-byte header)
    // RA01:  2A mov [rax+40h],rcx (board) / mov [rax+48h],r10 (field id)
    // RA01:  32 xor r10d,r10d / test r8,r8 / je +3 / mov r10,[r8] / mov [rax+50h],r10   - the value's first 8 bytes (0 if no pointer)
    // RA01:  41 mov qword [rax+58h], <which setter> (4 bytes at +0x45)
    // RA01:  49 mov r11,rsp / mov [r11+8],rbx  - the setter's own first two instructions
    // RA01:  50 jmp [rip+0] -> the 8-byte address at +0x56 (the setter, 7 bytes in)
    // RA01: Only rax, r10, r11 and the flags change - none of them carry anything into a function's first instruction.
    static ReadOnlySpan<byte> Detour => new byte[] { 0x45, 0x31, 0xD2, 0x48, 0x85, 0xD2, 0x74, 0x03, 0x4C, 0x8B, 0x12, 0x49, 0xBB, 0, 0, 0, 0, 0, 0, 0, 0,
        0xB8, 0x01, 0x00, 0x00, 0x00, 0xF0, 0x41, 0x0F, 0xC1, 0x03, 0x25, 0xFF, 0x1F, 0x00, 0x00, 0xC1, 0xE0, 0x05, 0x4C, 0x01, 0xD8, 0x48, 0x89, 0x48, 0x40,
        0x4C, 0x89, 0x50, 0x48, 0x45, 0x31, 0xD2, 0x4D, 0x85, 0xC0, 0x74, 0x03, 0x4D, 0x8B, 0x10, 0x4C, 0x89, 0x50, 0x50, 0x48, 0xC7, 0x40, 0x58, 0, 0, 0, 0,
        0x4C, 0x8B, 0xDC, 0x49, 0x89, 0x5B, 0x08, 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
    const int RingAt = 0x0D, IndexAt = 0x45, BackAt = 0x56;
    const int Slots = 8192, SlotBytes = 32, HeaderBytes = 0x40;
    const int LearnMs = 3000, ReadEveryMs = 50, NoisyAfter = 200, MaxLines = 3000;

    // RA01: M22-0b - the fields worth a line, by name (their ids are these names hashed). PlayerOnly = a player-state-machine field: only V's own board counts.
    // RA01: M21-11 - Overlay = one of the whiteboard's switches (see the class comment).
    static readonly (string Name, bool PlayerOnly, bool Overlay)[] Watched =
    {
        ("Popup_Radio_IsShown", false, true), ("Popup_VehiclesManager_IsShown", false, true), ("Popup_IsShown", false, true), ("ContactsActive", false, true),
        ("Popup_IsModal", false, false), ("UIRadialContextRequest", false, true), ("ActivatePhoneElements", false, false), ("isDisplayingMessage", false, false),
        ("IsPhoneCallActive", false, false), ("IsInWater", false, false),
        ("HighLevel", true, false), ("Swimming", true, false), ("Locomotion", true, false), ("LocomotionDetailed", true, false), ("UpperBody", true, false),
        ("Weapon", true, false), ("CombatGadget", true, false), ("Vehicle", true, false), ("Takedown", true, false), ("LeftHandCyberware", true, false),
        ("Zoom", true, false), ("SceneTier", true, false),
        ("IsUIZoomDevice", true, false), ("IsInteractingWithDevice", true, false), ("IsControllingDevice", true, false),   // RA01: M23-2
        ("IsActive", false, false), ("IsFPP", false, false), ("EnableExit", false, false), ("PlaybackSpeed", false, false), ("PlaybackDirection", false, false), ("SectionTime", false, false), // RA01: M24-1
        ("MagazineAmmoCount", false, false),                                                                                 // RA01: M27-7a
        ("IsInMenu", false, false),                                                                                          // RA01: M37 - UI_System: the game's own "a menu is open"
        ("Crosshair", true, false),                                                                                          // RA01: M48-0 - V's crosshair state (gamePSMCrosshairStates; the reticles' OnState_Reload) - physical reload probe
    };
    static readonly Dictionary<long, (string Name, bool PlayerOnly, bool Overlay)> watch = Watched.ToDictionary(w => (long)GameAnchor.NameCode(w.Name), w => w);
    static readonly long PlayerMark = (long)GameAnchor.NameCode("LocomotionDetailed");  // RA01: M22-1 - only V's own state machine writes it (UpperBody was not enough)
    static readonly long HighLevelField = (long)GameAnchor.NameCode("HighLevel"), SwimmingField = (long)GameAnchor.NameCode("Swimming");
    static readonly long DeviceZoomField = (long)GameAnchor.NameCode("IsUIZoomDevice");   // RA01: M23-2
    static readonly long InteractingField = (long)GameAnchor.NameCode("IsInteractingWithDevice"); // RA01: M86
    static readonly long ControllingField = (long)GameAnchor.NameCode("IsControllingDevice"), ActiveField = (long)GameAnchor.NameCode("IsActive"); // RA01: M24-1
    static readonly long MagazineField = (long)GameAnchor.NameCode("MagazineAmmoCount");  // RA01: M27-7a - UI_ActiveWeaponData: the rounds the HUD shows in V's magazine
    static readonly long InMenuField = (long)GameAnchor.NameCode("IsInMenu");             // RA01: M37 - UI_System.IsInMenu
    static readonly HashSet<long> BraindanceMarks = new[] { "IsFPP", "EnableExit", "PlaybackSpeed", "PlaybackDirection", "SectionTime" }.Select(n => (long)GameAnchor.NameCode(n)).ToHashSet();
    const int MaxActiveBoards = 256;                                                       // RA01: M24-1 - boards whose IsActive is remembered (more: the list starts over)
    const int HighLevelSwimming = 6;

    static nint ring;                       // RA01: the ring: +0 the write count (4 bytes), slots from +0x40
    static nint[] setters = Array.Empty<nint>();
    static int installed;
    static volatile bool on;
    static volatile bool overlayShown;

    // RA01: M21-11 - true while the radio list, the car list, the phone's contacts or any popup is shown (false if the probe could not be installed).
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static bool OverlayShown => overlayShown;

    static volatile int highLevel, swimLevel;     // RA01: M22-1 - V's HighLevel and Swimming, as last written on her board
    static volatile bool deviceZoom;              // RA01: M23-2 - V's IsUIZoomDevice
    static volatile bool controlling, braindance; // RA01: M24-1 - V's IsControllingDevice; the braindance board's IsActive
    static volatile int locomotion;               // RA01: M24-3 - V's LocomotionDetailed
    static volatile bool gameMenu;                // RA01: M37 - the game's own UI_System.IsInMenu, as last written

    // RA01: M37 - true while the game itself says a menu is open (inventory, weapon mods, map, ...). MenuSense keeps menu mode while it is on, so a stick
    // RA01: turning a 3D preview can't be taken for play. Wiped when a save loads (a new V board). Cost - CPU: extremely low (one read); GPU: none.
    public static bool GameMenu => gameMenu;

    // RA01: M27-7a - the rounds in V's magazine as the HUD last showed them (UI_ActiveWeaponData.MagazineAmmoCount, written by the game on every shot, reload
    // RA01: and weapon switch); -1 until the game has written it this session. Cost - CPU: extremely low (one read); GPU: none.
    public static int MagazineAmmo => magazineAmmo;
    static volatile int magazineAmmo = -1;

    // RA01: M81 - THE GUN WENT EMPTY (Rob 2026-10-14, Dogtown 03:28): the HUD count is read again the moment a new magazine goes in, and during quick weapon swaps
    // RA01: it can already show something else - then the empty-gun refill (GameCall, M48-2) didn't open the game's reload and the gun stayed "reloading".
    // RA01: So a 0 seen once is remembered here until GameCall takes it at the next refill (TakeSawEmpty). Like a chalk mark on an empty crate: it stays until refilled.
    // RA01: Cost - CPU: extremely low (one store per magazine write; one exchange per refill); GPU: none.
    static volatile int sawEmpty;
    // RA01: M81 - true if a 0 was seen since the last call, and clears the mark. Cost - CPU: extremely low (one exchange); GPU: none.
    public static bool TakeSawEmpty() => Interlocked.Exchange(ref sawEmpty, 0) != 0;

    // RA01: M24-1 - true while a braindance is playing or being edited. Cost - CPU: extremely low (one read); GPU: none.
    public static bool InBraindance => braindance;

    // RA01: M24-1 - true while V looks through a camera or turret she has taken over. Cost - CPU: extremely low (one read); GPU: none.
    public static bool ControllingDevice => controlling;

    // RA01: M86 - true while V uses a device (V's own board, IsInteractingWithDevice): an arcade machine, a terminal... With GameMenu it marks an arcade game
    // RA01: (Rob's probe 2026-10-15 03:37: the inventory sets only IsInMenu; the arcade sets this first, then IsInMenu). Wiped on a load.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static bool InteractingWithDevice => interacting;
    static volatile bool interacting;

    // RA01: M24-1 - the view belongs to a braindance or a taken-over camera, and the VR Controls row "Braindance & camera look" is on: the right stick looks
    // RA01: up and down there and the camera height shift pauses (ControllerMapping, CameraBodyProbe).
    // RA01: M24-5 - in the Militech training sim ("Practice Makes Perfect") V "controls" the training body like a device, and the flag stayed on 48 s after it
    // RA01: (Rob 2026-10-07 18:42: the view pushed down and never levelled). So a taken-over camera only counts while the game's camera really belongs to
    // RA01: something else (GameAnchor.CameraNotV) that is not a body (GameAnchor.OnStandIn - the training avatar is played like V).
    // RA01: M24-6 - the braindance on its own, with no stand-in check: a real braindance also hands the camera to a stand-in body (Rob's 18:54 log, the same
    // RA01: body class as the training avatar), and M24-5's stand-in check switched braindance look off there.
    // RA01: Cost - CPU: extremely low (a few reads); GPU: none.
    public static bool RemoteView => Tuning.RemoteViewControls && (braindance || (controlling && GameAnchor.CameraNotV && !GameAnchor.OnStandIn));

    // RA01: M24-3 - V's detailed movement state as the game last wrote it (0 until then): 10 on a ladder, 11 climbing fast, 12 sliding down, 13 jumping off.
    // RA01: Cost - CPU: extremely low (one read); GPU: none.
    public static int Locomotion => locomotion;

    // RA01: M23-2 - true while V's view is zoomed onto a device screen (a PC, a terminal). Cost - CPU: extremely low (one read); GPU: none.
    public static bool AtDeviceScreen => deviceZoom;

    // RA01: M22-1 - V in the water: 0 = no, 1 = on the surface, 2 = diving (3 = climbing out, if the game ever sets it). While she is in the water but the
    // RA01: game is between two swim states (it writes 0 for a moment), this says 1. Cost - CPU: extremely low (two reads); GPU: none.
    public static int SwimState => highLevel == HighLevelSwimming ? Math.Max(1, swimLevel) : 0;

    // RA01: the Dev row: the log on / off (the detours and the whiteboard run either way since M21-11). Not saved.
    // RA01: Cost - CPU: extremely low (a bool and a log line); then as the class comment; GPU: none.
    public static bool On
    {
        get => on;
        set
        {
            on = value && ring != 0;
            Log.Write(on ? "RA01 blackboard probe: ON - learning for 3 s, then every change is logged (open the phone, radio, car list; swim; quick-throw a grenade)" : ring == 0 ? "RA01 blackboard probe: not installed (see the start-up line)" : "RA01 blackboard probe: off");
        }
    }

    // RA01: finds the ten setters, makes the ring and one detour each, and starts the reader thread. Any problem: logged, nothing is hooked.
    // M21-11 - called once at start-up by Entry (it was on the Dev row's first switch-on).
    // Cost - CPU: low, once (one byte search of the exe and ten 5-byte writes); GPU: none.
    public static void Install()
    {
        if (Interlocked.Exchange(ref installed, 1) == 1) return;
        try
        {
            List<nint> found = ExeScan.FindAll(Setter);
            if (found.Count == 0 || found.Count > 16) { Log.Write($"RA01 blackboard probe NOT installed: the blackboard setters were found {found.Count} times (expected about 10)"); HookRegistry.Off(HookRegistry.Blackboard, $"found {found.Count} times (expected about 10)"); return; }
            ring = VirtualAlloc(0, (nuint)(HeaderBytes + Slots * SlotBytes), 0x3000, 0x04); // RA01: MEM_COMMIT|MEM_RESERVE, read/write
            if (ring == 0) { Log.Write("RA01 blackboard probe NOT installed: no memory for its ring"); HookRegistry.Off(HookRegistry.Blackboard, "no memory for its ring"); return; }
            List<nint> hooked = new();
            for (int i = 0; i < found.Count; i++)
            {
                nint site = found[i];
                nint detour = CameraHook.AllocateNear(site);
                if (detour == 0) { Log.Write($"RA01 blackboard probe: setter {i} at RVA 0x{site - ExeScan.Base:X} skipped - no memory within reach"); continue; }
                byte* code = (byte*)detour;
                Detour.CopyTo(new Span<byte>(code, Detour.Length));
                *(long*)(code + RingAt) = ring;
                *(int*)(code + IndexAt) = i;
                *(long*)(code + BackAt) = site + Replaced;
                if (CameraHook.WriteJump(site, detour)) hooked.Add(site);
                else Log.Write($"RA01 blackboard probe: setter {i} at RVA 0x{site - ExeScan.Base:X} NOT hooked (the jump could not be written)");
            }
            setters = found.ToArray();
            if (hooked.Count > 0) HookRegistry.Found(HookRegistry.Blackboard, found.Contains(ExeScan.Base + HookRegistry.Rows[HookRegistry.Blackboard].Rva231) ? ExeScan.Base + HookRegistry.Rows[HookRegistry.Blackboard].Rva231 : found[0], $"{hooked.Count} of {found.Count} hooked");   // RA01: M43 - reported to the Rehooker
            else HookRegistry.Off(HookRegistry.Blackboard, "none could be hooked");
            Log.Write($"RA01 blackboard probe: {hooked.Count} of {found.Count} blackboard setters hooked, at RVA {string.Join(", ", found.Select((s, i) => $"[{i}] 0x{s - ExeScan.Base:X}"))} (2.31: 0x32BD8C = Bool, 0x1B93BC = Int)");
            new Thread(Reader) { IsBackground = true, Name = "RA01 blackboard probe" }.Start();
        }
        catch (Exception e) { Log.Write($"RA01 blackboard probe failed: {e.Message}"); }
    }

    // RA01: M48-1 - THE RELOAD WINDOW (log only, Dev row "Blackboard probe (log)", Rob approved 2026-10-11): hunting the field that leaves some reticles
    // RA01: (the Guillotine's, smart guns') showing "reloading" after a physical reload from an empty magazine. While the probe logs, EVERY blackboard write -
    // RA01: any board, any field, not only the watch list - is kept for a moment when its value changes. The moment the HUD's magazine count goes UP (our refill,
    // RA01: the game's own reload, a weapon switch), a window opens: the last WindowBeforeMs of changes are logged, then every change for WindowAfterMs more.
    // RA01: A field that changes more than WindowFlapAfter times in one window is summed up in one line when it closes (the Weapon field's 6/4 flip-flop).
    // RA01: Fields are logged as their 8-byte name codes (FNV-1a 64 of the name, GameAnchor.NameCode) unless the watch list names them; the names are worked out
    // RA01: offline from the game's script file, so the mod carries no name list. Builder picture: a security camera that keeps the last three seconds and saves
    // RA01: the tape whenever the magazine counter jumps.
    // RA01: Cost - CPU: low to medium, only while the Dev row is on (a dictionary update per changed write, a few thousand a second; up to WindowMaxLines log
    // RA01: lines per window); nothing at all with the Dev row off; GPU: none.
    const long WindowBeforeMs = 3000, WindowAfterMs = 3000;
    const int WindowMaxLines = 1500, WindowFlapAfter = 6, HistoryMax = 20000, LastValueMax = 200000;
    static readonly Dictionary<(long, long), long> lastValue = new();                                          // RA01: (board, field) -> last value seen
    static readonly Queue<(long Ms, long Board, long Field, long Value, int Setter)> history = new();           // RA01: the last WindowBeforeMs of changes
    static readonly Dictionary<(long, long), (int Count, long First, long Last)> windowChanges = new();         // RA01: changes per field in the open window
    static bool windowOpen;
    static long windowStart, windowUntil;
    static int windowLines;

    // RA01: M48-1 - one blackboard write: dropped if its value didn't change; opens the window when the magazine count goes up; else logged (window open) or
    // kept in the history. Reader thread only. Cost - CPU: extremely low per write (two dictionary look-ups); GPU: none.
    static void Capture(long now, long board, long field, long value, int setter)
    {
        var key = (board, field);
        if (lastValue.TryGetValue(key, out long old) && old == value) return;
        if (lastValue.Count >= LastValueMax) lastValue.Clear();
        lastValue[key] = value;
        if (field == MagazineField && (int)(uint)value > magazineAmmo && !windowOpen) OpenWindow(now, board, value);
        if (windowOpen) { WindowLine(now, board, field, value, setter); return; }
        history.Enqueue((now, board, field, value, setter));
        while (history.Count > HistoryMax || (history.Count > 0 && now - history.Peek().Ms > WindowBeforeMs)) history.Dequeue();
    }

    // RA01: M48-1 - opens the window and logs the kept history first. Cost - CPU: low, once per window (up to a few thousand short lines); GPU: none.
    static void OpenWindow(long now, long board, long value)
    {
        windowOpen = true; windowStart = now; windowUntil = now + WindowAfterMs; windowLines = 0; windowChanges.Clear();
        Log.Write($"RA01 blackboard window: OPEN - the magazine count went up to {(uint)value} (board 0x{board:X}, was {magazineAmmo}); every blackboard change {WindowBeforeMs / 1000} s before and {WindowAfterMs / 1000} s after follows (unnamed fields as their name codes)");
        while (history.Count > 0) { var h = history.Dequeue(); WindowLine(h.Ms, h.Board, h.Field, h.Value, h.Setter); }
    }

    // RA01: M48-1 - one change inside the window: logged, unless that field has already changed WindowFlapAfter times (then only counted). Cost - CPU: extremely low; GPU: none.
    static void WindowLine(long ms, long board, long field, long value, int setter)
    {
        var key = (board, field);
        var c = windowChanges.TryGetValue(key, out var was) ? (was.Count + 1, was.First, value) : (1, value, value);
        windowChanges[key] = c;
        if (c.Item1 > WindowFlapAfter || windowLines++ >= WindowMaxLines) return;
        string name = watch.TryGetValue(field, out var w) ? w.Name : $"0x{(ulong)field:X16}";
        Log.Write($"RA01 blackboard window: {(ms - windowStart) / 1000.0:+0.000;-0.000} s board 0x{board:X} {name} = 0x{(ulong)value:X} (setter {setter})");
    }

    // RA01: M48-1 - closes the window: one line per field that kept changing. Cost - CPU: extremely low (one pass over the window's fields); GPU: none.
    static void CloseWindow()
    {
        windowOpen = false;
        foreach (var (key, c) in windowChanges)
            if (c.Count > WindowFlapAfter)
                Log.Write($"RA01 blackboard window: board 0x{key.Item1:X} {(watch.TryGetValue(key.Item2, out var w) ? w.Name : $"0x{(ulong)key.Item2:X16}")} changed {c.Count} times (first 0x{(ulong)c.First:X}, last 0x{(ulong)c.Last:X})");
        Log.Write($"RA01 blackboard window: CLOSED ({windowLines} lines{(windowLines > WindowMaxLines ? ", the rest cut" : "")})");
        windowChanges.Clear();
    }

    // RA01: the reader: every 50 ms takes the new slots from the ring. Always: keeps the whiteboard (M21-11). While the Dev row is on: logs watched fields whose
    // value's first byte changed (the first 3 s after switching on only learn). Never throws.
    // Cost - CPU: low (a short loop over a few hundred slots, a dictionary lookup each); GPU: none.
    static void Reader()
    {
        Dictionary<(int, long, long), (byte First, uint Dword, int Changes)> seen = new();     // RA01: (setter, board, field) -> last value, for the log
        Dictionary<(long, long), bool> shown = new();                                         // RA01: M21-11 - the whiteboard: (board, field) -> on
        uint next = (uint)Volatile.Read(ref *(int*)ring);
        long learnUntil = 0;
        bool wasOn = false;
        int lines = 0;
        Dictionary<long, bool> activeOn = new();                                              // RA01: M24-1 - board -> its last IsActive
        long braindanceBoard = 0;                                                             // RA01: M24-1 - the board that writes the braindance's own fields
        long playerBoard = 0;                // RA01: M22-0b - V's own state-machine board, once it has written LocomotionDetailed (M22-1)
        while (true)
        {
            Thread.Sleep(ReadEveryMs);
            try
            {
                uint count = (uint)Volatile.Read(ref *(int*)ring);
                bool logging = on;
                if (logging && !wasOn) { learnUntil = Environment.TickCount64 + LearnMs; seen.Clear(); }
                wasOn = logging;
                if (count - next > Slots) { if (logging) Log.Write($"RA01 blackboard probe: {count - next - Slots} writes came too fast to read (skipped)"); next = count - Slots; }
                bool learning = Environment.TickCount64 < learnUntil;
                long now = Environment.TickCount64;                                          // RA01: M48-1 - the reload window's clock
                for (; next != count; next++)
                {
                    byte* slot = (byte*)ring + HeaderBytes + (next & (Slots - 1)) * SlotBytes;
                    long board = *(long*)slot, field = *(long*)(slot + 8), value = *(long*)(slot + 16);
                    int setter = *(int*)(slot + 24);
                    if (logging && !learning) Capture(now, board, field, value, setter);  // RA01: M48-1 - every write, for the reload window (before the watch-list filter)
                    if (!watch.TryGetValue(field, out var named)) continue;                 // RA01: M22-0b - only the watch list
                    if (field == PlayerMark && board != playerBoard)
                    {
                        playerBoard = board;
                        shown.Clear();                                                      // RA01: M21-11 - a new V (a load): wipe the whiteboard
                        highLevel = swimLevel = 0;
                        deviceZoom = false;                                                 // RA01: M23-2
                        controlling = false; locomotion = 0; interacting = false;                                // RA01: M24-1, M24-3 (the braindance board is not V's: kept)
                        gameMenu = false;                                                   // RA01: M37 - a load never leaves a menu switch stuck on
                        if (logging && lines++ < MaxLines) Log.Write($"RA01 blackboard probe: V's state-machine board is 0x{board:X}");
                    }
                    byte first = (byte)value;
                    if (named.Overlay) shown[(board, field)] = first != 0;                  // RA01: M21-11 - bools: the first byte is the whole value
                    if (board == playerBoard && field == HighLevelField) highLevel = first;  // RA01: M22-1 - small enums: the first byte is the whole value
                    if (board == playerBoard && field == SwimmingField) swimLevel = first;
                    if (board == playerBoard && field == DeviceZoomField) deviceZoom = first != 0;   // RA01: M23-2
                    if (board == playerBoard && field == ControllingField) controlling = first != 0; // RA01: M24-1
                    if (board == playerBoard && field == InteractingField) interacting = first != 0; // RA01: M86
                    if (board == playerBoard && field == PlayerMark) locomotion = first;             // RA01: M24-3
                    if (field == MagazineField) { magazineAmmo = (int)(uint)value; if ((uint)value == 0) sawEmpty = 1; } // RA01: M27-7a - a Uint32: the low four bytes (M81: a 0 is remembered)
                    if (field == InMenuField && (first != 0) != gameMenu) { gameMenu = first != 0; Log.Write($"RA01 blackboard: the game says a menu is {(gameMenu ? "OPEN" : "closed")}"); } // RA01: M37 - always logged (rare)
                    if (field == ActiveField)                                                       // RA01: M24-1
                    {
                        if (activeOn.Count >= MaxActiveBoards && !activeOn.ContainsKey(board))      // RA01: M24-7 - full: start over, but keep the braindance board's
                        {                                                                           // RA01: switch (wiping it mid-braindance would end braindance look)
                            bool keep = activeOn.TryGetValue(braindanceBoard, out bool kept);
                            activeOn.Clear();
                            if (keep) activeOn[braindanceBoard] = kept;
                        }
                        activeOn[board] = first != 0;
                    }
                    if (BraindanceMarks.Contains(field) && board != braindanceBoard) { braindanceBoard = board; Log.Write($"RA01 blackboard: the braindance board is 0x{board:X}"); }
                    if (!logging || (named.PlayerOnly && board != playerBoard)) continue;
                    var key = (setter, board, field);
                    uint dword = (uint)value;
                    if (!seen.TryGetValue(key, out var was))
                    {
                        seen[key] = (first, dword, 0);
                        if (!learning && first != 0 && lines++ < MaxLines) Log.Write($"RA01 blackboard probe: {named.Name} = {first} (board 0x{board:X}, setter {setter}, new)");
                        continue;
                    }
                    if (was.First == first || was.Changes < 0) continue;
                    int changes = was.Changes + 1;
                    if (changes > NoisyAfter)
                    {
                        seen[key] = (first, dword, -1);
                        if (!learning && lines++ < MaxLines) Log.Write($"RA01 blackboard probe: {named.Name} (board 0x{board:X}) keeps changing - ignored from now on");
                        continue;
                    }
                    seen[key] = (first, dword, changes);
                    if (!learning && lines++ < MaxLines) Log.Write($"RA01 blackboard probe: {named.Name} {was.First} -> {first} (board 0x{board:X}, setter {setter})");
                }
                bool inBraindance = braindanceBoard != 0 && activeOn.TryGetValue(braindanceBoard, out bool active) && active; // RA01: M24-1
                if (inBraindance != braindance) Log.Write(inBraindance ? "RA01 blackboard: braindance started" : "RA01 blackboard: braindance ended");
                braindance = inBraindance;
                bool any = false;
                foreach (bool v in shown.Values) any |= v;
                if (any != overlayShown && logging && lines++ < MaxLines) Log.Write($"RA01 blackboard probe: overlay {(any ? "shown - no crosshair cut-out" : "gone - cut-out back")}");
                overlayShown = any;
                if (windowOpen && Environment.TickCount64 >= windowUntil) CloseWindow();   // RA01: M48-1
                if (!logging) { history.Clear(); lastValue.Clear(); }                       // RA01: M48-1 - nothing kept while the Dev row is off
            }
            catch (Exception e) { Log.Write($"RA01 blackboard probe: skipped a read ({e.Message})"); }
        }
    }
}
