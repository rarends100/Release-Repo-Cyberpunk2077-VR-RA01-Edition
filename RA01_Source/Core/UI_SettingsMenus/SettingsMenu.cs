using System.Globalization;
using System.Linq;
using static RA01.Core.Win32; // RA01: Rect and Point

namespace RA01.Core;

// RA01: M5a-3, M5a-4 - the F10 settings window: what is in it, where everything is, and what the mouse does to it. Nothing in here draws or talks to Windows (MenuDraw paints it, MenuInput takes the mouse over),
// so all of it can be tested without a headset. The window sits on a 1280 x 960 panel; everything inside it is laid out from the window's size, so dragging an edge resizes the window and its contents with it.
// M5a-12 - every keyboard adjustment key is gone, so every row below has to exist here or the setting cannot be reached at all.
// M5a-14 - the list scrolls instead of shrinking: at most VisibleRows (6) rows are shown at once, however many Items there are, with two small arrow spots at the right edge of the list to move up and down. The window's own size still only changes how big those 6 rows are, not how many of them show.
// Cost - CPU: low (only while the F10 window is open: layout, hit-testing and drag math each headset frame, a few microseconds); GPU: none (no GPU work, it never draws).
internal static class SettingsMenu
{
    public const int PanelWidth = Tuning.MaxMenuWidth, PanelHeight = Tuning.MaxMenuHeight; // RA01: the panel is as big as the window can get
    public const int KindOption = 0, KindSlider = 1, KindButton = 2, KindClose = 3, KindScrollUp = 4, KindScrollDown = 5, KindTab = 6, KindItemButton = 7; // RA01: what a clickable spot is - M6-6 added KindItemButton, a per-row push button (see Item.Trigger)
    static int IpdItem => Array.FindIndex(Items, i => i.Name == "Eye distance (IPD)"); // RA01: the IPD slider's place in Items (it shows "the headset's own" when no IPD is set). M24-2 - found by name: the old fixed 3 had gone stale
    public const int VisibleRows = 6;                                                      // RA01: M5a-14 - at most this many rows of the current tab's items are laid out and drawn at once; ScrollTop picks which
    const float Sensitivity = 1.5f;  // RA01: panel pixels the cursor moves per mouse count
    const int Border = 7;            // RA01: pixels along the window's edges where a drag resizes it
    const int ScrollColumn = 40;     // RA01: units of room kept clear on the right of every row for the two scroll arrows (see Layout)
    public const int ScrollStep = 5; // RA01: how many rows one click of an arrow - or one push of a stick (MenuPointer) - moves: M19-3, a page (was 3; the stick was 1)
    const int TabBarUnits = 34;      // RA01: M6-1 - the height, in layout units, of the strip of tab buttons between the title bar and the settings list

    // RA01: one setting. Options = the choices of a radio row (its value is the choice's number), or null for a slider from Min to Max. Get and Set read and write the value in Tuning.
    // M6-6 - Status and Trigger are for a row that is neither a slider nor a set of choices: Status (if set) shows its own live text instead of a value, with no clickable spot
    // at all - a label row. Trigger (if set) makes the whole row one push button that calls it when clicked, instead of drawing a slider or choices. Every existing row leaves
    // both null and is unchanged. A Status or Trigger row still needs something in Options/Min/Max/Format/Get/Set to fit the record's shape; those go unused, so
    // null/0/1/""/()=>0/_=>{} is the usual filler for one (see DevItems below).
    // M24-2 - Text (if set) is a slider's own value text in place of its number (the Auto tune picker shows a headset's name).
    // Cost - CPU: extremely low (a plain data holder, made once at start-up); GPU: none (no GPU work).
    public sealed record Item(string Name, string Tip, string[]? Options, float Min, float Max, string Format, Func<float> Get, Action<float> Set, Func<string>? Status = null, Action? Trigger = null, Func<string>? Text = null);

    // RA01: one spot the mouse can click: its Kind, which Item (or button) it belongs to, the Value it sets (a radio choice), and its box in panel pixels.
    // Cost - CPU: extremely low (a plain data holder, 40 of them reused every layout); GPU: none (no GPU work).
    public struct Hit { public int Kind, Item, Value; public Rect Box; }

    static readonly string[] OnOff = { "On", "Off" };
    static readonly string[] MonowireLooks = { "X", "Straight" };   // RA01: M38-1d - the "Monowire look" choices

    // RA01: M6-1 - the two top tabs and what each one holds. VideoItems is exactly what used to be the one flat Items list; ControlItems is new. Items itself stays one flat list
    // RA01: underneath (Rows, Hits and every lookup by index still work exactly as before) - TabStart/TabCount just say where each tab's own slice of it begins and how long it is.
    public static readonly string[] TabNames = { "VR Video Settings", "VR Controls", "VRIK", "HUD", "Weapons", "Vehicle", "Performance", "Log control", "Dev Only Tools", "Dev_Rehook" }; // RA01: M43 - the Dev_Rehook tab (last, so no other tab moves) // RA01: M41 - the Log control tab // RA01: M20-12 - the Performance tab // RA01: M19-3 - the Vehicle tab // RA01: M13-2 - the Weapons tab // RA01: M10-1 - the HUD tab // RA01: M6-6 - added the third tab; keep this in step with TabStart/TabCount above or the tab bar (Layout) mis-sizes itself
    static readonly Item[] VideoItems =
    {
        new("Auto tune: headset", "M24-2 - pick your headset (it starts on the one the mod recognised, if it did), then press 'Auto tune: apply' below. It sets View coverage, Game view width limit and Game resolution to a good starting point for that headset: how much of its view is worth drawing and how many pixels each eye gets (the resolution aims for a picture height, so it comes out the same on any runtime). Your field of view itself always comes from the headset. Every value can still be changed by hand afterwards. The starting values come from each headset's published specs, not measurements - fine-tune by eye.",
            null, 0, AutoTune.Last, "0", () => AutoTune.Choice, v => AutoTune.Choice = v, Text: AutoTune.Text),
        new("Auto tune: apply", "M24-2 - sets View coverage, Game view width limit and Game resolution for the headset picked above. The resolution takes hold the next time the game starts (or right away if you apply any resolution in the game's Options > Video).",
            null, 0, 1, "", () => 0, _ => { }, Trigger: AutoTune.Apply),
        new("Game resolution", "M24-6 - moved up here, next to Auto tune, which sets it. The game's own resolution, as a percent of your headset's per-eye size, in your headset's own shape (Quest 3 at 50% = 1248x1344). It is made the ONLY resolution the game can pick, so nothing in the game's own menus can change it. Takes hold the next time the game starts, or right away if you apply any resolution in the game's Options > Video. The longer side never goes past 3840 pixels (4K). With a headset the mod hasn't seen before, it takes hold from the second start. WARNING: higher uses much more GPU power - the cost grows with the square: 100% draws 4x the pixels of 50%. Lower it if your frame rate drops.", null, Tuning.MinGameResolutionPercent, Tuning.MaxGameResolutionPercent, "0'% of headset'",
            () => Tuning.GameResolutionPercent, v => Tuning.GameResolutionPercent = MathF.Round(v / 5f) * 5f), // RA01: M12-2 - steps of 5%
        new("Picture mode", "M5b - how the game's picture reaches the headset. Projection: each eye's picture is handed to the headset as that eye's own view, through the exact lens the game drew it with - nothing bends when you turn your head, and Picture size, Picture left/right, up/down and the eye trims do nothing (there is nothing left to guess). Flat screen (old): the picture hangs as a flat panel in front of each eye, sized and moved by those settings. Menus and loading screens always use the flat panel.",
            new[] { "Projection", "Flat screen (old)" }, 0, 1, "", () => Tuning.PictureMode, v => Tuning.PictureMode = Math.Clamp((int)MathF.Round(v), 0, 1)),
        new("Game menu size", "M38 - moved up here, under Picture mode (the old flat-screen rows below are only for Flat screen mode). How big the game's own menus are (start screen, pause menu, loading screens): the height of the picture in degrees of your view. It does not depend on the picture size or field of view above.", null, Tuning.MinGameMenuFov, Tuning.MaxGameMenuFov, "0' deg'",
            () => Tuning.GameMenuFov, v => Tuning.GameMenuFov = MathF.Round(v)),
        new("Game menu height", "M13-63 - moves the game's own menus (start screen, pause menu, map, inventory, loading screens) up (+) or down (-) in your view, in degrees. Gameplay is not moved.", null, -Tuning.MaxGameMenuUpDeg, Tuning.MaxGameMenuUpDeg, "0' deg'",
            () => Tuning.GameMenuUpDeg, v => Tuning.GameMenuUpDeg = MathF.Round(v)),
        new("Game menu left / right", "M38 - moves the game's own menus (start screen, pause menu, map, inventory, loading screens) right (+) or left (-) in your view, in degrees. Gameplay is not moved.", null, -Tuning.MaxGameMenuRightDeg, Tuning.MaxGameMenuRightDeg, "0' deg'",
            () => Tuning.GameMenuRightDeg, v => Tuning.GameMenuRightDeg = MathF.Round(v)),
        new("Game menu view", "Auto tries to notice when the game shows a menu and then uses the Game menu size above. If it misses one, pick Menu while the menu is up and Game when you are back in play.", new[] { "Auto", "Menu", "Game" }, 0, 2, "",
            () => Tuning.MenuView, v => Tuning.MenuView = (int)MathF.Round(v)),
        new("View coverage", "M5b-2 - Projection mode only: the game's view is fitted to your headset by itself, exactly as wide and tall as your headset can show (measured from the headset at start-up, any headset). 100% = all of it, no dark rim. Lower draws a slightly smaller view: a thin dark rim at the very edge of your vision, but less GPU work and fewer rooms vanishing at wide views. It never bends or stretches either way. Game field of view and Auto field of view below only apply to Flat screen mode. The Game view width limit below still caps the width. RECOMMENDED: most headsets (Quest 2 / 3 / 3S, Pico 4, Valve Index, Vive / Vive Pro, Reverb G2, Bigscreen Beyond, PSVR2 on PC) 100%. Wide headsets (Vive Pro 2, Pimax Crystal / Crystal Light, Varjo Aero) 95-100%. Ultra-wide headsets (Pimax 5K / 8K / 8KX / 12K, Crystal Super wide lenses): 85-90% - a flat picture spends most of its pixels on the outermost rim of a very wide view, so trimming that rim gives a big speed-up for little you can see. WARNING - too LOW (under about 80%): a dark border closes in around your view (the 'looking through a box' feeling); below 80% is for testing only.", null, Tuning.MinViewCoverage, Tuning.MaxViewCoverage, "0'%'",
            () => Tuning.ViewCoverage, v => Tuning.ViewCoverage = MathF.Round(v)),
        new("Game view width limit", "M5b-2b - Projection mode only: the game's view is never drawn wider than this, in degrees per eye. If it is wider than your headset can show, it does nothing - the view is fitted to the headset by itself (so 160 = no limit). M21-9: walls and rooms no longer vanish at wide views while the Wide-view distance fix is on (Dev tab, on by default); with it off, the view is held to 90 degrees tall as before. Default 100. RECOMMENDED: most headsets (Quest 2 / 3 / 3S, Pico 4, Valve Index, Vive / Vive Pro, Reverb G2, Bigscreen Beyond, PSVR2 on PC) 85-100 - a Quest 3's lenses show nothing past about 85-100, so a lower value here costs you nothing you can see and saves GPU work. Wide headsets (Vive Pro 2, Pimax Crystal / Crystal Light, Varjo Aero) 120-130. Ultra-wide headsets (Pimax 5K / 8K / 8KX / 12K, Crystal Super wide lenses) 140-160, with View coverage 85-90%. Lower = a sharper picture and less GPU work, but a dark rim at the edge of your vision. WARNING - too LOW (under about 85-90): a dark border closes in around your view (the 'looking through a box' feeling). Very wide views (over about 130) cost a lot of GPU and stretch the very edge of the picture.", null, Tuning.MinGameWidthDeg, Tuning.MaxGameWidthDeg, "0' deg'",
            () => Tuning.GameWidthLimitDeg, v => Tuning.GameWidthLimitDeg = MathF.Round(v)),
        new("3D depth", "Each eye gets its own picture, so things have real depth. Off = one flat picture for both eyes, which shows whether a problem comes from the 3D.", OnOff, 0, 1, "",
            () => Tuning.Stereo ? 0 : 1, v => Tuning.Stereo = v < 0.5f),
        new("Flat view at PC screens", "M23-2 - On: while V's view is zoomed onto a computer or terminal screen, both eyes get the same flat picture, so the screen's mouse pointer is one steady pointer (in 3D the game places it for each eye in turn, one frame late, and it looked doubled). 3D comes back the moment you step away. Off: 3D at screens too.",
            OnOff, 0, 1, "", () => Tuning.FlatAtDevices ? 0 : 1, v => Tuning.FlatAtDevices = v < 0.5f),
        new("PC screen distance", "M23-2c - while V's view is zoomed onto a computer or terminal screen (with 'Flat view at PC screens' on), her view sits this much further back from the screen, in centimetres, so it isn't right in your face and you see the screen at a wider angle. 8 cm (default) is about three inches; much more and you may see V's own (headless) body. 0 = where the game puts it.",
            null, 0f, 40f, "0' cm'", () => Tuning.DeviceScreenBackCm, v => Tuning.DeviceScreenBackCm = MathF.Round(v)),
        new("Swap eyes", "Only use this if the depth looks inside out (near things seem far away). It gives each eye the other eye's picture.", new[] { "Normal", "Swapped" }, 0, 1, "",
            () => Tuning.SwapEyes ? 1 : 0, v => Tuning.SwapEyes = v > 0.5f),
        new("Settings window on monitor", "M29-1 - CONTROLS: none. On: while this window is open in VR it is also shown on your desktop monitor, centred (so it shows in screen recordings and streams); the headset still shows it as its own floating panel. Off: headset only. Without a headset (flat screen) F10 opens it on the monitor either way - use the mouse there. Needs the game's picture in 8-bit (HDR off).",
            OnOff, 0, 1, "", () => Tuning.MenuOnMonitor ? 0 : 1, v => Tuning.MenuOnMonitor = v < 0.5f),
        new("Monitor shows", "What your desktop monitor (and a screen recording of it) shows while 3D depth is on. Both eyes: the game's own pictures in turn, which shakes on a flat screen. Left or Right eye: only that eye's picture, steady - pick your dominant eye. The headset is never affected.",
            new[] { "Both eyes", "Left eye", "Right eye" }, 0, 2, "", () => Tuning.DesktopEye, v => Tuning.DesktopEye = (int)MathF.Round(v)),
        new("Exact picture pairing", "M26-1 - On (recommended): every game picture is matched to the exact head pose it was drawn with, read straight off the game's renderer each frame - so Frame lag no longer matters and the random jumping (the 1-6% of frames the old counting got wrong) is gone. Frame lag and its auto-fix below only step in where this can't (menus, loading screens, camera cuts), or if a game update breaks it (the log then says so). Off: the old counting with Frame lag, as before.",
            OnOff, 0, 1, "", () => Tuning.ExactPairing ? 0 : 1, v => Tuning.ExactPairing = v < 0.5f),
        new("Frame lag", "How many camera steps behind the game really draws. Pick the number where the picture looks most solid, with nothing doubled or ghosting. It can be different on every PC.", new[] { "0", "1", "2", "3" }, 0, 3, "",
            () => Tuning.FrameLag, v => Tuning.FrameLag = (int)MathF.Round(v)),
        new("Frame lag auto-fix", "M25-1 - On: about twice a second the mod compares a thin strip of your left and right eye's pictures. Near things always sit further right in the left eye's picture - if they sit the other way, the eyes have been swapped (one frame of Frame lag off, after a menu, a loading screen or a stutter), and after a few seconds of clear 'swapped' looks it moves Frame lag by one by itself (written in the log). It can't see being two off (a slight wobble when you turn) - set that by hand. Off: Frame lag stays exactly where you put it.",
            OnOff, 0, 1, "", () => Tuning.FrameLagAuto ? 0 : 1, v => Tuning.FrameLagAuto = v < 0.5f),
        new("Eye distance (IPD)", "The distance between your eyes. Bigger makes the world look smaller and further away, smaller makes it look bigger and closer. The Own IPD button goes back to what your headset measured.", null, Tuning.MinIpdMm, Tuning.MaxIpdMm, "0.0 'mm'",
            () => Tuning.IpdMm == 0 ? Math.Clamp(Tuning.HeadsetIpdMm, Tuning.MinIpdMm, Tuning.MaxIpdMm) : Tuning.IpdMm, v => Tuning.IpdMm = MathF.Round(v * 10) / 10),
        new("Picture size", "How big the game's picture is in front of you. Bigger = a bigger screen and less of the black border, smaller = a smaller screen.", null, Tuning.MinSize, Tuning.MaxSize, "'x'0.00",
            () => Tuning.SizeScale, v => Tuning.SizeScale = MathF.Round(v * 100) / 100),
        new("Picture left / right", "Moves the picture sideways, in degrees of your view. It turns around you, so it still faces you. Use it if the picture looks off centre. Plus = right, minus = left. (It moves the picture only, not where the game aims.)", null, -Tuning.MaxShiftDeg, Tuning.MaxShiftDeg, "+0.0' deg';-0.0' deg';0.0' deg'",
            () => Tuning.ShiftRightDeg, v => Tuning.ShiftRightDeg = MathF.Round(v * 10) / 10),
        new("Picture up / down", "Moves the picture up or down, in degrees of your view. It turns around you, so it still faces you. Plus = up, minus = down.", null, -Tuning.MaxShiftDeg, Tuning.MaxShiftDeg, "+0.0' deg';-0.0' deg';0.0' deg'",
            () => Tuning.ShiftUpDeg, v => Tuning.ShiftUpDeg = MathF.Round(v * 10) / 10),
        new("Mirror eye trims", "On = remembers the four eye-trim sliders below exactly as they are, then sets all four to 0. Off = puts back whatever they were the moment you last turned this on (not just now, even after a restart) - a quick way to see the picture with no per-eye trim and get back to where you were.", OnOff, 0, 1, "",
            () => Tuning.EyeTrimsZeroed ? 0 : 1, v => Tuning.SetEyeTrimsZeroed(v < 0.5f)),
        new("Left eye left / right", "A small extra nudge for the LEFT eye's picture only, on top of Picture left/right above, in degrees. Use it if one eye's picture looks very slightly off from the other's, not to move the whole picture. Plus = right, minus = left.", null, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg, "+0.0' deg';-0.0' deg';0.0' deg'",
            () => Tuning.LeftEyeShiftRightDeg, v => Tuning.LeftEyeShiftRightDeg = MathF.Round(v * 10) / 10),
        new("Left eye up / down", "The same, up or down, for the LEFT eye only. Plus = up, minus = down.", null, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg, "+0.0' deg';-0.0' deg';0.0' deg'",
            () => Tuning.LeftEyeShiftUpDeg, v => Tuning.LeftEyeShiftUpDeg = MathF.Round(v * 10) / 10),
        new("Right eye left / right", "The same idea, for the RIGHT eye's picture only. Plus = right, minus = left.", null, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg, "+0.0' deg';-0.0' deg';0.0' deg'",
            () => Tuning.RightEyeShiftRightDeg, v => Tuning.RightEyeShiftRightDeg = MathF.Round(v * 10) / 10),
        new("Right eye up / down", "The same, up or down, for the RIGHT eye only. Plus = up, minus = down.", null, -Tuning.MaxEyeTrimDeg, Tuning.MaxEyeTrimDeg, "+0.0' deg';-0.0' deg';0.0' deg'",
            () => Tuning.RightEyeShiftUpDeg, v => Tuning.RightEyeShiftUpDeg = MathF.Round(v * 10) / 10),
        new("Auto field of view", "Flat screen mode only (Projection mode fits the view by itself - see View coverage). On = once, automatically, sets Game field of view below to x2.00. Off = Game field of view stays wherever you leave it. Either way the picture always follows the game's own aspect ratio.", OnOff, 0, 1, "",
            () => Tuning.AutoFov ? 0 : 1, v => Tuning.AutoFov = v < 0.5f),
        new("Game field of view", "Flat screen mode only (Projection mode fits the view by itself - see View coverage). How wide a view the game itself draws. Higher fills more of your headset's view and shrinks the black border without zooming everything in. Too high looks stretched at the edges. If buildings or streets pop in and out at wide settings, start the game with the RA01 launcher (it adds a small game setting that stops that) or lower this. With Auto field of view on, this is set for you but can still be fine-tuned here.", null, Tuning.MinFovScale, Tuning.MaxFovScale, "'x'0.00",
            () => Tuning.FovScale, v => Tuning.FovScale = MathF.Round(v * 100) / 100),
        new("Head prediction", "How far ahead your head movement is guessed, in milliseconds, to hide the delay of the game's pictures. Too low: the picture drags behind your head. Too high: it swings ahead.", null, 0, Tuning.MaxPredictionMs, "0' ms'",
            () => Tuning.PredictionMs, v => Tuning.PredictionMs = MathF.Round(v)),
        new("Head steering", "On: your head turns the game's camera. Off: the game keeps its own camera and its picture is just a fixed screen in the room.", OnOff, 0, 1, "",
            () => HeadPose.Steering ? 0 : 1, v => HeadPose.Steering = v < 0.5f),
    };

    // RA01: M6-1 - the "VR Controls" tab. M9-8 - the speed slider became the body-turn dead zone, plus the stick look up/down switch. M6-2 added the on/off row above the slider, so "body follows head" can be switched off outright rather than only tuned down; more
    // RA01: controller tuning can join it later the same way.
    static readonly Item[] ControlItems =
    {
        new("Left-hand mode", "M28-3 - WARNING - READ BEFORE SWITCHING: this swaps EVERY button and both sticks the moment you change it, including how to open this window. With it ON, open this window with: hold your RIGHT A, squeeze your RIGHT trigger, then push your LEFT stick LEFT (or press F10). To switch back, open the window that way and turn this off. CONTROLS (On): everything mirrors. FIRE / ATTACK with your LEFT trigger, AIM / BLOCK with your RIGHT trigger; MOVE with your RIGHT stick, TURN with your LEFT stick (crouch is the left stick click); the grips swap (left grip = RB, right grip = LB); the face buttons swap sides (A and X trade places, B and Y trade places - so RELOAD is your right A and JUMP your left X; gesture mode is a 5 s hold of your left Y). THE SETTINGS WINDOW AND GAME MENUS MOVE TOO: hold your right A, then squeeze your RIGHT trigger, then push your LEFT stick LEFT to open this window (UP for the game menu, DOWN for the pause menu). On: V holds every gun and melee weapon in her LEFT hand, gripped as a left-hander would (the game only animates right-handed, so the mod moves the weapon across each frame); aiming, the two-handed grip (now your right hand), weapon weight, the laser, the shoulder scope, physical melee, blocking, power attacks and throws all follow it. DRIVING: your LEFT trigger is the throttle and your RIGHT trigger the brake / reverse (with a gun out, your left grip squeeze is the throttle instead, as deep as you squeeze). Menus and this window keep the triggers and grips in their normal places. Off: right-handed, as the game is.",
            OnOff, 0, 1, "", () => Tuning.LeftHanded ? 0 : 1, v => Tuning.LeftHanded = v < 0.5f),
        new("Body follows head", "CONTROLS: turn your head past the dead zone and V's body turns after it; the right stick still turns her. On: look around freely inside the dead zone below - only your head turns. Turn your head past it and V's body turns to line up under your head again, while the world stays still. Your physical stick always wins. Off: only the physical stick turns V.",
            OnOff, 0, 1, "",
            () => Tuning.BodyFollowEnabled ? 0 : 1, v => Tuning.BodyFollowEnabled = v < 0.5f),
        new("Body turn dead zone", "How far your head can turn away from V's body, in degrees each side, before her body turns to follow. VR games commonly use 30-45; smaller makes V turn sooner, larger lets you look further over your shoulder first.",
            null, Tuning.MinBodyTurnDeadZoneDeg, Tuning.MaxBodyTurnDeadZoneDeg, "0' deg'",
            () => Tuning.BodyTurnDeadZoneDeg, v => Tuning.BodyTurnDeadZoneDeg = MathF.Round(v)),
        new("Stick look up/down", "CONTROLS: right stick up/down (only when On). Off (motion controls): only your head looks up and down; the right stick's up/down does nothing, so the view can't tilt away from your real head. On: the stick tilts the view as in the flat game - for a seated mode. Game menus always get the stick.",
            OnOff, 0, 1, "",
            () => Tuning.StickPitch ? 0 : 1, v => Tuning.StickPitch = v < 0.5f),
        new("Keep view level", "M20-5 - On: some cutscenes leave the game's camera tilted (looking down), and with motion controls the right stick can't tilt it back. This levels it for you after a moment, using the right stick's up/down behind the scenes. Your head still does all the real looking. Off: the game's camera tilt is left as the game sets it.",
            OnOff, 0, 1, "", () => Tuning.KeepViewLevel ? 0 : 1, v => Tuning.KeepViewLevel = v < 0.5f),
        new("Physical crouch", "CONTROLS: squat for real to crouch, stand up to stand; a quick right-stick click still crouches by hand. On: crouch down for real and V crouches; stand up and she stands. The view stays at your real head height - the game's own crouch never drops it further. Your standing height is taken when you recentre. Off: crouch by hand only - a quick click of the right stick (then the game's own crouch view is used). Needs the game's crouch set to Toggle.",
            OnOff, 0, 1, "",
            () => Tuning.PhysicalCrouch ? 0 : 1, v => Tuning.PhysicalCrouch = v < 0.5f),
        new("Crouch depth", "How far your head has to drop below where it was when you recentred to crouch V, in centimetres (she stands again once you're back within 10 cm less, so hovering near the line doesn't flicker). Higher = you must squat deeper.",
            null, Tuning.MinCrouchDepthCm, Tuning.MaxCrouchDepthCm, "0' cm'",
            () => Tuning.CrouchDepthCm, v => Tuning.CrouchDepthCm = MathF.Round(v)),
        new("Physical swimming", "M22-1 - CONTROLS: in water, pull your arms back to swim forward; sweep a hand out to the side to turn the other way; look down to dive, up to rise. On: in the water, swim with your arms. Any stroke that pulls water back works - front crawl, doggy paddle, breaststroke, butterfly or one arm - and V swims forward, gliding on between strokes. Sweep a hand out to one side and V turns the other way (push right, turn left) - only once that hand is out to the side past the 'Side stroke dead zone', so small wobbles in front of you never turn her; both hands to the same side turn faster, and a breaststroke's two outward sweeps cancel so she swims straight. Look down to dive, up to rise. The sticks still work - your thumb on the left stick always wins. Off: stick swimming only.",
            OnOff, 0, 1, "", () => Tuning.PhysicalSwim ? 0 : 1, v => Tuning.PhysicalSwim = v < 0.5f),
        new("Forward stroke strength", "M22-1b - how hard each pull-back stroke pushes V forward through the water. 100% = one long pull of about 60 cm is a good push; 500% = short, quick strokes reach V's top swim speed at once. The game sets that top speed - higher only gets her there sooner and keeps her there longer between strokes. Lower it if V shoots off too fast.",
            null, Tuning.MinSwimStrength * 100f, Tuning.MaxSwimForwardStrength * 100f, "0'%'", () => Tuning.SwimForwardStrength * 100f, v => Tuning.SwimForwardStrength = MathF.Round(v) / 100f),
        new("Side stroke strength", "M22-1b - how far a sideways hand sweep turns V (push right, turn left; both hands the same way turn 1.5 times as fast). Default 70%: turning gentler than swimming. Raise it for quicker turns, lower it if V spins too easily.",
            null, Tuning.MinSwimStrength * 100f, Tuning.MaxSwimSideStrength * 100f, "0'%'", () => Tuning.SwimSideStrength * 100f, v => Tuning.SwimSideStrength = MathF.Round(v) / 100f),
        new("Side stroke dead zone", "M22-1e - how far out to the side a hand must be before its sweep can turn V, in degrees from straight ahead (measured flat from your head, like a compass). Inside it, hand movement never turns her - so the small wobbles of a doggy paddle or the start of a crawl pull in front of your chest stay straight. A sweep still needs to be a real stroke (quick, and more sideways than backward). Lower = turns from closer in; higher = only wide sweeps turn. Default 20.",
            null, Tuning.MinSwimTurnDeadZoneDeg, Tuning.MaxSwimTurnDeadZoneDeg, "0' deg'", () => Tuning.SwimTurnDeadZoneDeg, v => Tuning.SwimTurnDeadZoneDeg = MathF.Round(v)),
        new("Gesture mode", "M27-1 - CONTROLS: hold B for 5 seconds to switch gesture mode ON (a banner says so); hold B 5 seconds again to switch it OFF. While it is on, each hand: GRIP + TRIGGER = middle finger, GRIP = pointing finger, TRIGGER = thumbs up, NOTHING PRESSED = open hand. Your triggers and grips then pose the fingers only - they do NOT shoot, aim or press LB/RB. Drawing a weapon switches it off at once (bare fists are fine); it pauses in vehicles, menus and dialogue. With this row on, B reaches the game only as a quick tap (under 0.4 s) during play, so the long hold never makes V do anything; menus, vehicles and dialogue get B as before. The fingers need the game's fist pose: if they don't move, draw bare fists for a second once. Off: B and the hands work exactly as before.",
            OnOff, 0, 1, "", () => Tuning.GestureMode ? 0 : 1, v => Tuning.GestureMode = v < 0.5f),
        new("Physical ladder", "M24-3 - CONTROLS: on a ladder, squeeze a grip and pull that hand DOWN to climb up, push it UP to climb down. On: on a ladder, squeeze a grip to hold a rung and pull your hand DOWN to climb V up (push it up to climb down), hand over hand like a real ladder. Only hands with the grip squeezed count, so reaching up for the next rung with an open hand does nothing; V keeps moving smoothly between pulls. While on the ladder the grips hold the rungs (no LB/RB). The left stick still works - your thumb always wins. Off: stick climbing only.",
            OnOff, 0, 1, "", () => Tuning.PhysicalLadder ? 0 : 1, v => Tuning.PhysicalLadder = v < 0.5f),
        new("Climb strength", "M24-3 - how far each ladder pull moves V. 100% = a pull of about 33 cm is full climbing speed. The game sets that top speed - higher only gets her there with shorter pulls. Lower it if she climbs too eagerly.",
            null, Tuning.MinLadderStrength * 100f, Tuning.MaxLadderStrength * 100f, "0'%'", () => Tuning.LadderStrength * 100f, v => Tuning.LadderStrength = MathF.Round(v) / 100f),
        new("Braindance & camera look", "M24-1 - CONTROLS: in a braindance or a taken-over camera, the right stick turns AND looks up/down. On: in a braindance, or looking through a camera or turret V has taken over, the right stick looks up and down as well as left and right (those views don't follow your body), body-follow turning pauses, and the VRIK tab's camera height / forward shift pauses so the view sits where the game puts it. Everything goes back the moment you leave. Off: those views behave like normal play.",
            OnOff, 0, 1, "", () => Tuning.RemoteViewControls ? 0 : 1, v => Tuning.RemoteViewControls = v < 0.5f),
    };

    // RA01: M6-6 - "Dev Only Tools": not a player-facing setting, a way to start and watch BoneFinder's one-off heap hunt without ever leaving the headset or touching the keyboard -
    // RA01: the F10 window can already be worked with a motion controller (MenuPointer), so this tab makes F11 reachable that way too, with its own status line instead of the log file.
    // RA01: M8-3 - the "VRIK" tab: V's arms following the motion controllers. M9-1 - the hand-offset sliders and straight-arm calibration are gone (hands are
    // RA01: placed from the real camera now). M9-2/M9-3 - T-pose calibration (span, arm & hand size, wrists; M9-6 arm & hand size rows; M9-7 forearm twist) and where the camera sits on V.
    static readonly Item[] VrikItems =
    {
        // RA01: M33-4 - Rob 12:17: the T-pose calibration first in the tab, so it is the easiest thing to reach.
        new("Calibrate arms (T-pose)", "CONTROLS: press, then within 3 s stand straight with both arms straight out to the sides for a second. Measures your arm span and your wrists once. Press this, then within 3 seconds stand straight with BOTH arms stretched straight out to the sides, level with your shoulders, PALMS FACING THE FLOOR, and hold still for 2 more seconds until the status below says done. It sets how your reach is fitted to V's, lines V's wrists up with yours, and then puts the camera at her eyes again (stand still one more second). Saved.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: () => { Tuning.ArmCalibrationDueMs = Environment.TickCount64 + 3000; Tuning.ArmCalibrationResult = "get into a T-pose, palms down..."; }),
        new("Calibration status", "What the last T-pose calibration measured.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => Tuning.ArmCalibrationDueMs > Environment.TickCount64 ? $"T-pose, palms down - measuring in {(Tuning.ArmCalibrationDueMs - Environment.TickCount64 + 999) / 1000} s" : Tuning.ArmCalibrationDueMs != 0 ? "measuring - hold still..." : Tuning.ArmCalibrationResult),
        new("VRIK in cutscenes", "M20-10 - Who moves V's body during the game's scenes. Off in cutscenes (default): in first-person and full cutscenes the game animates her body itself, so props and hand-offs look right. Off in all scenes: dialogue scenes too (where you can look around but the game moves your hands). Always on: VRIK keeps driving her arms in every scene.",
            new[] { "Off in cutscenes", "Off in all scenes", "Always on" }, 0, 2, "", () => Tuning.VrikInScenes, v => Tuning.VrikInScenes = Math.Clamp((int)MathF.Round(v), 0, 2)),
        new("Follow stand-in bodies", "M20-6 - On: when the game puts you in a different body than V's for a while (the Militech VR training in \"Practice Makes Perfect\"), your arms and hands drive that body instead of V's hidden one. Only a person-type body that has the first-person camera is ever used. Off: VRIK always drives V's own body.",
            OnOff, 0, 1, "", () => Tuning.FollowStandIn ? 0 : 1, v => Tuning.FollowStandIn = v < 0.5f),
        new("Arms follow controllers", "On: V's arms bend so her hands go where your motion controllers are. Off: her arms move only as the game animates them. Her wrists still keep the game's own angle for now; matching your wrist turn comes next.",
            OnOff, 0, 1, "",
            () => Tuning.ArmsFollowControllers ? 0 : 1, v => Tuning.ArmsFollowControllers = v < 0.5f),
        new("Rest-pose shoulders", "On: V's collarbones sit where and how they do when she stands naturally - the first-person animation otherwise slides them in toward the middle of her chest, which pinches the chest into a point and shortens her reach. Off: they move with the game's animation. Mainly for comparing.",
            OnOff, 0, 1, "",
            () => Tuning.RestShoulders ? 0 : 1, v => Tuning.RestShoulders = v < 0.5f),
        new("Auto arm & hand size", "On: V's arms and hands are sized from your T-pose calibration so her wrist-to-wrist span matches yours (her shoulders and body stay her own size). Off: the Arm & hand size slider below is used.",
            OnOff, 0, 1, "",
            () => Tuning.ArmSizeAuto ? 0 : 1, v => Tuning.ArmSizeAuto = v < 0.5f),
        new("Arm & hand size", "How big V's arms and hands are drawn, as a percentage of her own. Moving this turns Auto arm & hand size off, so your setting is used straight away. Your reach is re-fitted to match, so her arm is still straight exactly when yours is.",
            null, Tuning.MinArmSizePercent, Tuning.MaxArmSizePercent, "0'%'",
            () => Tuning.ArmSizePercent, v => { Tuning.ArmSizePercent = MathF.Round(v); Tuning.ArmSizeAuto = false; }), // RA01: M11-1d - moving the slider turns Auto off, or it would be silently ignored
        new("Upright spine", "On: V's back is held straight (her standing pose) and swung so her shoulders always sit under your head, so the game's forward lean when holding a weapon (or any other animation lean) never tips her chest into view and her arms stay steady while she walks; her hips and legs still walk and run as animated. Off: her back moves with the game's animation.",
            OnOff, 0, 1, "",
            () => Tuning.UprightSpine ? 0 : 1, v => Tuning.UprightSpine = v < 0.5f),
        new("Lean with your head", "On: lean your real body forward or sideways and V's back bends to follow your head, up to 35 degrees, so looking down over a table or around a corner keeps her body under you. Off: her back stays straight (or animated, with Upright spine off).",
            OnOff, 0, 1, "",
            () => Tuning.LeanWithHead ? 0 : 1, v => Tuning.LeanWithHead = v < 0.5f),
        new("Chest follows head", "On: when you turn your head (inside the body-turn dead zone), V's chest turns part of the way with it, like a real upper body, instead of staying locked forward. Off: her chest keeps the game's own animation.",
            OnOff, 0, 1, "",
            () => Tuning.ChestFollowsHead ? 0 : 1, v => Tuning.ChestFollowsHead = v < 0.5f),
        new("Forearm twist", "On: when you roll your wrist (like turning a key), the roll is shared out along V's forearm the way a real forearm does it, instead of all of it happening at her wrist and pinching the skin there. Needs the T-pose calibration (wrists). Off: the forearm keeps the game's own twist, for comparing.",
            OnOff, 0, 1, "",
            () => Tuning.ForearmTwist ? 0 : 1, v => Tuning.ForearmTwist = v < 0.5f),
        new("Per-hand cyberware", "On: Mantis Blades and Gorilla Arms stay folded until the hand that strikes pops its own blade or plates out - with the game's own strike motion - and they fold straight back in after. Both stay out while you hold the guard (a blade block needs blades), and your own right-trigger press opens both. Off: both arms behave as in the game (deployed while drawn).",
            OnOff, 0, 1, "",
            () => Tuning.PerHandCyberware ? 0 : 1, v => Tuning.PerHandCyberware = v < 0.5f),
        new("Put camera at V's eyes", "CONTROLS: press, then stand still for a second. The game's first-person camera sits low and in front of V's head (about chest-to-chin height on her). Press this and stand still for a second: it measures where her eyes are and moves the camera there. Fine-tune with the two sliders below.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: () => { Tuning.CameraToEyesDueMs = Environment.TickCount64 + 1000; Tuning.CameraToEyesResult = "measuring - stand still..."; }),
        new("Camera status", "What the last Put camera at V's eyes press did.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => Tuning.CameraToEyesDueMs != 0 ? "measuring - stand still..." : Tuning.CameraToEyesResult),
        new("Camera forward", "Moves the camera forward (+) or back (-) on V's body, in centimetres.",
            null, -Tuning.MaxCameraShiftCm, Tuning.MaxCameraShiftCm, "0' cm'",
            () => Tuning.CameraForwardCm, v => Tuning.CameraForwardCm = MathF.Round(v)),
        new("Camera up", "Moves the camera up (+) or down (-) on V's body, in centimetres.",
            null, -Tuning.MaxCameraShiftCm, Tuning.MaxCameraShiftCm, "0' cm'",
            () => Tuning.CameraUpCm, v => Tuning.CameraUpCm = MathF.Round(v)),
    };

    // RA01: M13-2 - the "Weapons" tab.
    // RA01: M19-3 - the "Vehicle" tab: physical steering of cars, tanks and motorcycles with your hands (Actions\PhysicalSteering.cs).
    static readonly Item[] VehicleItems =
    {
        new("Physical steering", "M19-1 - CONTROLS: put a hand on the wheel rim (or a handlebar), squeeze the grip to hold it, and turn. On: seated in a car, tank or on a motorcycle, squeeze a grip with your hand on the wheel's rim (or a handlebar grip) to hold it, and turn it. Two hands turn it like a real wheel; one hand can steer alone or rest while the other turns, and you can go hand over hand. While seated the grips are never LB/RB; your thumb on the left stick still wins. Needs \"Arms follow controllers\" (VRIK tab). Off: steer with the stick.",
            OnOff, 0, 1, "", () => Tuning.PhysicalSteering ? 0 : 1, v => Tuning.PhysicalSteering = v < 0.5f),
        new("Car view height", "M20-1 - Seated in a car, truck or tank (not a motorcycle): your view sits this far from the driver's eye point (minus = lower) and never rises above it, even if you sit up or stand - ducking and leaning still move it. Back to normal the moment you get out.",
            null, -20f, 10f, "0' cm'", () => Tuning.CarViewHeightCm, v => Tuning.CarViewHeightCm = MathF.Round(v)),
        new("Car view forward", "M46 - Seated in a car, truck or tank (not a motorcycle): your view moves this far forward along the car (minus = back), so V's own neck stays out of sight behind you. 0 = the game's driver eye point, as before. Back to normal the moment you get out.",
            null, -10f, 20f, "0' cm'", () => Tuning.CarViewForwardCm, v => Tuning.CarViewForwardCm = MathF.Round(v)),
        new("Steering feel", "PERFORMANCE (the default) handles like a sports car: a quick wheel (a small turn of your wrists is full lock), steering that follows your hands almost instantly, a direct, firm middle and a brisk return to straight - for fast driving and sharp manoeuvres. COMFORT drives like a relaxed cruiser: a slow wheel you turn a long way, unhurried steering, a soft, forgiving middle and a lazy return. Pick one to set the five sliders below; moving any of them makes it CUSTOM. This changes how your hands steer - not the car's own grip or speed.",
            new[] { "Performance", "Comfort", "Custom" }, 0, 2, "", () => Tuning.SteerFeel, v => Tuning.SetSteerFeel((int)MathF.Round(v))),
        new("Wheel lock", "How far you turn the steering wheel for full steering, in degrees. Smaller = a quicker, sportier wheel; larger = calmer. Performance 110, Comfort 200.",
            null, 60f, 360f, "0' deg'", () => Tuning.WheelLockDeg, v => { Tuning.WheelLockDeg = MathF.Round(v / 5f) * 5f; Tuning.SteerFeel = 2; }),
        new("Handlebar lock", "How far you swing a motorcycle's handlebars for full steering, in degrees. Performance 30, Comfort 40.",
            null, 15f, 60f, "0' deg'", () => Tuning.BarLockDeg, v => { Tuning.BarLockDeg = MathF.Round(v); Tuning.SteerFeel = 2; }),
        new("Steering speed", "The power-steering damper: how quickly the steering can follow your hands (full travels a second). Higher = snappier, lower = smoother. Performance 10, Comfort 3.",
            null, 1f, 20f, "0.0", () => Tuning.SteerSpeed, v => { Tuning.SteerSpeed = MathF.Round(v * 2f) / 2f; Tuning.SteerFeel = 2; }),
        new("Centre softness", "How gentle the steering is near straight ahead. 0% = direct (every degree of wheel counts the same); higher = small movements near centre do less, big turns still go all the way. Performance 15%, Comfort 50%.",
            null, 0f, 90f, "0'%'", () => Tuning.SteerSoftness * 100f, v => { Tuning.SteerSoftness = MathF.Round(v) / 100f; Tuning.SteerFeel = 2; }),
        new("Steering return", "With both hands off, the wheel turns itself back to straight like a real car's - quicker when turned far, slowing as it nears centre. Higher = it straightens sooner. Performance 3.5, Comfort 1.5.",
            null, 0.5f, 8f, "0.0", () => Tuning.SteerReturn, v => { Tuning.SteerReturn = MathF.Round(v * 2f) / 2f; Tuning.SteerFeel = 2; }),
    };

    static readonly Item[] WeaponItems =
    {
        new("Physical aiming", "CONTROLS: point the gun itself; shots follow the barrel while you pull the trigger. On: you aim with the gun itself, so the game's crosshair fades out while a gun is drawn (it would only show where your head points). Smart guns keep it (they lock on by themselves). Melee weapons, fists and Gorilla Arms always hide it; Monowire, Mantis Blades, the Projectile Launcher and empty hands keep it. Wherever it shows, it is drawn on the game's true aim, wherever the HUD panel sits. Off: the crosshair shows as in the flat game.",
            OnOff, 0, 1, "", () => Tuning.PhysicalAiming ? 0 : 1, v => Tuning.PhysicalAiming = v < 0.5f),
        new("Laser sight", "M24-4 - CONTROLS: none - it shows by itself while a gun is drawn. On: a laser beam shines from your gun's muzzle straight along the barrel, with a dot at its end - where the barrel points is where your shots go. It is drawn by the mod in your headset, not by the game, so it never needs a game update to keep working, but it also can't see walls: it always reaches 'Laser depth' and shows through anything closer. Only while a gun is drawn. Off: no laser.",
            OnOff, 0, 1, "", () => Tuning.LaserSight ? 0 : 1, v => Tuning.LaserSight = v < 0.5f),
        new("Laser colour", "M24-4 - the laser's colour.", HandStrip.Colours.Select(c => c.Name).ToArray(), 0, HandStrip.Colours.Length - 1, "",
            () => Tuning.LaserColour, v => Tuning.LaserColour = (int)MathF.Round(v)),
        new("Laser depth", "M24-4 - how far the beam reaches from the muzzle, in metres; the dot sits at its end. Set it to about how far you usually fight - indoors 5-10 m, streets 15-30 m. The beam can't stop at walls (see Laser sight), so a long beam shows through a wall close in front of you.",
            null, Tuning.MinLaserLengthM, Tuning.MaxLaserLengthM, "0' m'", () => Tuning.LaserLengthM, v => Tuning.LaserLengthM = MathF.Round(v)),
        new("Laser thickness", "M24-4 - the width of the beam's bright core, in millimetres (a soft glow around it is three times as wide). The dot grows with the depth so it stays easy to see far away.",
            null, Tuning.MinLaserThicknessMm, Tuning.MaxLaserThicknessMm, "0.0' mm'", () => Tuning.LaserThicknessMm, v => Tuning.LaserThicknessMm = MathF.Round(v * 2f) / 2f),
        new("Laser brightness", "M24-4 - how strong the beam and dot are: low is a faint hint, 100% a bright beam.",
            null, 20f, 100f, "0'%'", () => Tuning.LaserBrightness * 100f, v => Tuning.LaserBrightness = MathF.Round(v / 5f) * 5f / 100f),
        new("Weapon weight", "M27-4 - CONTROLS: none - just move. On: weapons have their real weight. V's weapon hand follows your controller through a soft spring: a pistol (1 kg) or a katana (1.2 kg) follows almost at once, a rifle (3.5 kg) lags a little behind quick swings, a sniper (7 kg) or a sledgehammer (6 kg) more, a heavy machine gun (30 kg) a lot - and held in one hand, its nose droops a few degrees. Hold it with both hands (the two-handed grip) and the droop halves and it follows twice as quickly. Your shots and the laser follow the weighted gun. Not with fists or cyberware, nor while seated in a vehicle. Off: weightless, as before.",
            OnOff, 0, 1, "", () => Tuning.WeaponWeight ? 0 : 1, v => Tuning.WeaponWeight = v < 0.5f),
        new("Weapon weight strength", "M27-4 - how heavy weapons feel, as a percentage of their real weight. 100% = real-world masses; 50% = half as heavy (less lag and droop); 200% = twice as heavy. 0% = no weight.",
            null, 0f, 200f, "0'%'", () => Tuning.WeaponWeightStrength * 100f, v => Tuning.WeaponWeightStrength = MathF.Round(v / 5f) * 5f / 100f),
        new("Physical reloading", "M27-5 - CONTROLS: HOLD X FOR ABOUT HALF A SECOND (0.45 s) = MAGAZINE RELEASE: with a gun out, holding X drops the magazine (on every gun); or, with your off hand on the magazine and its grip open, hold the off-hand trigger and pull it down, and let go of the trigger to drop it - one round stays in the chamber (revolvers and double-barrels empty fully; see Dropped magazine rounds). Then reach to your off-hand hip (the glowing ball, if Mag pouch glow is on) and squeeze the grip to take a new magazine - the grab zone there is generous; carry it to the small ball under the gun and it goes in (the game's own refill, no animation). On: the game's own reload is switched off (no auto-reload: an empty gun clicks; X doesn't reload). A quick press of X still opens doors and loots as normal and never drops the magazine - only a 1-second hold does. A squeeze on the gun is always the two-handed grip. Letting go of a new magazine before the gun drops it - take another from your hip. While that hand holds a magazine or is at your hip, its grip is not sent to the game. Guns only; not in menus, gesture mode or vehicles. Left-hand mode swaps the sides. Off: X only.",
            OnOff, 0, 1, "", () => Tuning.PhysicalReload ? 0 : 1, v => Tuning.PhysicalReload = v < 0.5f),
        new("Mag pouch glow", "M27-5c - CONTROLS: none - it shows by itself. On: while you need a new magazine (the old one is out), a softly glowing, half see-through ball marks the pouch at your off-hand hip - reach there and squeeze your grip. It hides again once you take one. Drawn by the mod in your headset, so it shows through V's arm. Needs Physical reloading. Off: no ball (the pouch still works).",
            OnOff, 0, 1, "", () => Tuning.PouchGlow ? 0 : 1, v => Tuning.PouchGlow = v < 0.5f),
        new("Mag pouch colour", "M27-5c - the pouch ball's colour.", PouchGlow.Colours.Select(c => c.Name).ToArray(), 0, PouchGlow.Colours.Length - 1, "",
            () => Tuning.PouchColour, v => Tuning.PouchColour = (int)MathF.Round(v)),
        new("Dropped magazine rounds", "M27-7 - physical reloading: when a magazine leaves the gun (pulled out or dropped with X) and it still had rounds, one stays in the chamber - the gun shows 1 (revolvers and double-barrel shotguns empty fully: opening them dumps every round). What happens to the rest: BACK TO RESERVE (default) - they return to your ammo, so a reload never costs more than the game's own. LOST WITH THE MAGAZINE - WARNING: they are gone for good, like dropping a real magazine: reloading a half-full gun throws away every round still in it, and you can run out of ammo much faster in long fights. An empty magazine (0 rounds) changes nothing.",
            new[] { "Back to reserve", "Lost with the magazine" }, 0, 1, "", () => Tuning.DroppedRounds, v => Tuning.DroppedRounds = (int)MathF.Round(v)),
        new("Reload sounds", "M27-6k - CONTROLS: none. On: physical reloading plays the game's own magazine sounds - out when you pull it or press X, in when the new one goes in, and a clatter when a dropped one lands (metal; plastic for Smart guns). Each gun uses its own sounds where the game has them, else its type's. Which sound each gun plays is in Settings\\RA01_GunSounds.txt (written the first time; edit it while the game runs - the next reload uses it). Off: silent reloads.",
            OnOff, 0, 1, "", () => Tuning.ReloadSounds ? 0 : 1, v => Tuning.ReloadSounds = v < 0.5f),
        new("Mag pouch size", "M27-6g - how big the hip ball looks (100% = about 9 cm). Only the look changes - the grab zone stays just as generous. The small ball that marks the magazine well on the gun (shown while you carry a new magazine) always stays small.",
            null, Tuning.MinPouchSize * 100f, Tuning.MaxPouchSize * 100f, "0'%'", () => Tuning.PouchSize * 100f, v => Tuning.PouchSize = MathF.Round(v / 5f) * 5f / 100f),
        new("Two-handed grip (guns)", "CONTROLS: reach your other hand to the gun and squeeze that hand's grip; let go to release. On: with a gun drawn, reach your other hand to anywhere along the gun and squeeze its grip to hold it with both hands - the gun then turns like a real rifle, its barrel following the line between your hands, and V's hand stays on the spot you grabbed. Let go of the grip to release. While that hand reaches for or holds the gun, LB and RB are not sent to the game. Needs Physical aiming.",
            OnOff, 0, 1, "", () => Tuning.TwoHandGrip ? 0 : 1, v => Tuning.TwoHandGrip = v < 0.5f),
        new("Two-handed melee", "CONTROLS: reach your other hand to the weapon and squeeze that hand's grip; let go to release. On: with a melee weapon drawn (katana, machete, bat, pipe, hammer...), squeeze your other hand's grip anywhere on it to hold it with both hands - it then turns with the line between your hands, and V's hand stays where you grabbed. Let go of the grip to release. While that hand holds it (or starts to squeeze near it), LB and RB are not sent to the game. Swings and blocks work as before. Not for fists or cyberware.",
            OnOff, 0, 1, "", () => Tuning.TwoHandMelee ? 0 : 1, v => Tuning.TwoHandMelee = v < 0.5f),
        new("Off hand opens and grips", "On: when your other hand comes close enough to grab the gun or melee weapon, V's hand opens; it closes as you squeeze the grip, and is a fist while it holds the weapon. Away from the weapon her fingers move as the game animates them. The first time, draw bare fists for a second so the mod can copy the game's own fist (it is saved for later sessions).",
            OnOff, 0, 1, "", () => Tuning.OffHandFingers ? 0 : 1, v => Tuning.OffHandFingers = v < 0.5f),
        new("Virtual stock", "CONTROLS: bring the back of the gun to your shoulder. On: bring the back of a gun to your shoulder (as if its stock were there) and it beds in: the barrel then follows the line from your shoulder through your hand - or through your other hand when holding with both - which steadies the aim the way a real stock does. Pull the gun away to release. Needs Physical aiming.",
            OnOff, 0, 1, "", () => Tuning.VirtualStock ? 0 : 1, v => Tuning.VirtualStock = v < 0.5f),
        new("Stock strength", "How much the bedded stock steers the gun. 0%: not at all (your hand alone aims). 100%: fully along the shoulder-to-hand line (steadiest, least wrist control). 70% is a good start.",
            null, 0f, 100f, "0'%'", () => Tuning.StockStrength * 100f, v => Tuning.StockStrength = MathF.Round(v) / 100f),
        new("No aim zoom", "M35 - CONTROLS: none. On: aiming (LT, or Shoulder to aim just below) never zooms the picture or pulls the gun, for every gun and scope - modded ones too - so your shots land where the gun points, with or without LT. The magnified picture inside a scope's lens stays; Kiroshi optics zoom is left alone. Done in the game's own data, so TweakXL and Aim Zoom Disabled are not needed for this. It is set up while the main menu shows 'PLEASE WAIT - LOADING GUNS' - wait for 'GUNS READY' before loading a save. After switching it on or off, go back to the main menu and load a save: guns you already carry keep their old aim until then. Off: the game's own aim zoom.",
            OnOff, 0, 1, "", () => Tuning.NoAimZoom ? 0 : 1, v => Tuning.NoAimZoom = v < 0.5f),
        new("Shoulder to aim", "M35-2 - CONTROLS: raise the gun to your cheek, pointing where you look. On: raise a gun to your cheek like a soldier - hand near your face, the gun pointing where you look - and V aims (the mod holds LT for you), so every scope and sight opens just as with LT. Lower it and she stops. Works with No aim zoom just above: with it on, shouldering aims with no zoom (best for VR); with it off, shouldering zooms like LT does. Your own LT still works as always. Needs Physical aiming. Off: only LT aims.",
            OnOff, 0, 1, "", () => Tuning.ShoulderScope ? 0 : 1, v => Tuning.ShoulderScope = v < 0.5f),
        new("Scope magnifier", "On: aiming through a long scope (a sniper scope, or any the game zooms x2.5 or more) shows a small round magnified view on the scope's line, about the size of a scope's eyepiece - the rest of your view stays unzoomed. It is the game's own picture blown up, so it is a little soft. Off: no magnification at all.",
            OnOff, 0, 1, "", () => Tuning.ScopeMagnifier ? 0 : 1, v => Tuning.ScopeMagnifier = v < 0.5f),
        new("Right stick D-pad while aiming", "CONTROLS: hold LT to aim, then push the right stick up/down for D-pad up/down. On: while you aim a gun down its sights (LT held), pushing the right stick up or down presses D-pad up or down instead; left and right still turn. Back to normal the moment you stop aiming (a stick still pushed then waits until you let it centre). Off: the right stick never changes.",
            OnOff, 0, 1, "", () => Tuning.AimStickDpad ? 0 : 1, v => Tuning.AimStickDpad = v < 0.5f),
        new("Scope lens for", "M16-A2 - the gun and scope the two sliders below change: the last one you aimed through with the magnifier showing. Each gun-and-scope pair keeps its own lens, saved in RA01_GunCalibration.ini.",
            null, 0, 1, "", () => 0, _ => { }, Status: ScopeLens.LensFor),
        new("Scope lens width", "How wide the magnified lens is, to match that scope's eyepiece. Change it, close F10, aim again to check.",
            null, GunCalibration.MinLensMetres * 100f, GunCalibration.MaxLensMetres * 100f, "0.0' cm'", () => ScopeLens.LensWidthCm, ScopeLens.SetLensWidthCm),
        new("Scope lens distance", "How far in front of your eye the lens sits (where the game puts your eye behind that scope, plus this), to match where its glass is. Change it, close F10, aim again to check.",
            null, GunCalibration.MinLensAhead * 100f, GunCalibration.MaxLensAhead * 100f, "0.0' cm'", () => ScopeLens.LensAheadCm, ScopeLens.SetLensAheadCm),
        new("Physical melee", "CONTROLS: hold a guard pose in front of your face to block (not with throwing knives - their LT is aim-to-throw, so they only stab); swing fast to strike. On: hold a guard pose to block (blade across your face; fists, Gorilla Arms or Mantis Blades raised in front of your face; Monowire held diagonally, at least a foot open) and swing fast to strike. With a gun, a fast swing of the gun hand does the game's quick melee. Your own buttons still work as normal.",
            OnOff, 0, 1, "", () => Tuning.PhysicalMelee ? 0 : 1, v => Tuning.PhysicalMelee = v < 0.5f),
        new("Swing speed to strike", "How fast a hand must move to count as a strike. Lower: lighter swings count (more accidental hits). Higher: only firm swings count.",
            null, Tuning.MinSwingSpeed, Tuning.MaxSwingSpeed, "0.0' m/s'", () => Tuning.SwingSpeed, v => Tuning.SwingSpeed = MathF.Round(v * 10f) / 10f),
        new("Physical power attacks", "M27-2 - CONTROLS: WIND-UP - pull one or both hands back BEHIND your shoulder (behind your head, out to the side) and hold it a moment (0.1 s); swing forward and the game's STRONG attack lands. GORILLA POUND - both hands up ABOVE and a little behind your head for a moment, then slam them down. With a melee weapon the weapon hand loads it (fists, Mantis Blades, Gorilla Arms and Monowire: either hand). While it loads, the mod holds the attack trigger (RT) for you, exactly like holding it yourself; the normal swing strikes of Physical melee wait, so one big swing is one strong attack. Not with guns, menus or gesture mode. Your own trigger still works. Off: strong attacks only by holding RT yourself.",
            OnOff, 0, 1, "", () => Tuning.PhysicalPowerAttacks ? 0 : 1, v => Tuning.PhysicalPowerAttacks = v < 0.5f),
        new("Monowire look", "M38-1d - CONTROLS: none. How the Monowire's wire lies between your hands while it rests. X: each hand's cable runs from its wrist into your other fist, so the two cross in an X - the VR look. Straight: one string from your left wrist into your right fist, exactly like the flat game. Either way it stays laid while you hold or charge RT, and is let go the moment an attack or whip fires, so attacks fly out as normal.",
            MonowireLooks, 0, 1, "", () => Tuning.MonowireX ? 0 : 1, v => Tuning.MonowireX = v < 0.5f),
        new("Physical grenade throw", "M22-4 - CONTROLS: hold the grip to take a grenade out, swing, and let go of the grip to throw. On: throw grenades with your arm. Hold the grip to take a grenade out as usual, then swing - overarm or underarm - and let go of the grip as your hand comes forward, like letting go of a ball. The grenade lands where a ball thrown that way would land: a soft lob goes high and near, a hard flat throw goes long, and you can throw a different way from where you look. A tap without a swing throws the game's own way. While on, the game's aiming arc and landing circle are hidden (M36). Off: the game's own throws only, with its usual arc.",
            OnOff, 0, 1, "", () => Tuning.PhysicalThrow ? 0 : 1, v => Tuning.PhysicalThrow = v < 0.5f),
        new("Throw strength", "M22-4 - how far your throws carry: your hand's speed at the moment you let go, times this, is the grenade's speed. A real arm throw indoors is about 5-9 m/s and the game's own throw is 25 m/s, so 300% puts a good throw in the game's normal range. Raise it if your play space is small or your throws fall short; lower it if grenades sail too far. Landing spots are capped at 40 m.",
            null, Tuning.MinThrowStrength * 100f, Tuning.MaxThrowStrength * 100f, "0'%'", () => Tuning.ThrowStrength * 100f, v => Tuning.ThrowStrength = MathF.Round(v) / 100f),
        new("Physical knife throw", "M22-5 - CONTROLS: hold to aim the knife as usual, then swing the knife hand forward hard. On: throw knives with your arm. Hold to aim the knife as usual, then swing the knife hand forward hard - past the 'Knife throw speed' the mod presses the throw for you and the knife flies along your swing (mixed with your look by 'Knife aim help'), as hard as your arm threw it ('Knife throw strength') - a flick lobs it, a hard throw flies fast and flatter. You can also press the throw yourself mid-swing. A press without a swing throws the game's own way, along your view. Off: the game's own knife throws only.",
            OnOff, 0, 1, "", () => Tuning.PhysicalKnifeThrow ? 0 : 1, v => Tuning.PhysicalKnifeThrow = v < 0.5f),
        new("Knife throw speed", "M22-5 - how fast your knife hand must swing forward (while the knife is aimed) to throw it, in metres a second. Separate from the melee swing speed. Lower: a lighter flick throws. Higher: only a firm throw does (fewer accidental throws while aiming).",
            null, Tuning.MinKnifeThrowSpeed, Tuning.MaxKnifeThrowSpeed, "0.0' m/s'", () => Tuning.KnifeThrowSpeed, v => Tuning.KnifeThrowSpeed = MathF.Round(v * 10f) / 10f),
        new("Knife throw strength", "M22-5b - how hard your knife throws carry: your hand's speed when the throw goes, times this, is the knife's speed. 300% puts a good arm throw at roughly 20-25 m/s - a flick lobs a few metres, a hard throw flies fast and flatter. Lower it if knives sail too far.",
            null, Tuning.MinThrowStrength * 100f, Tuning.MaxThrowStrength * 100f, "0'%'", () => Tuning.KnifeThrowStrength * 100f, v => Tuning.KnifeThrowStrength = MathF.Round(v) / 100f),
        new("Knife flight (lob - straight)", "M22-5c - how a thrown knife flies. 0% = LOB: the knife's own weight, it arcs and drops like a thrown stone. 100% = STRAIGHT: almost no drop, it flies like a dart along your throw. 50% (default) = in between. The game aims and flies the knife with the same setting, so where it lands matches the flight you see.",
            null, 0f, 100f, "0'%'", () => Tuning.KnifeFlight * 100f, v => Tuning.KnifeFlight = MathF.Round(v) / 100f),
        new("Knife aim help", "M22-5b - where a thrown knife goes: 0% = exactly along your swing; 100% = where you are looking (your arm then only sets how hard); 50% = halfway, so throwing roughly at what you look at lands it. Only the direction is mixed - the strength always comes from your arm.",
            null, 0f, 100f, "0'%'", () => Tuning.KnifeAimHelp * 100f, v => Tuning.KnifeAimHelp = MathF.Round(v) / 100f),
    };

    // RA01: M10-1 - the "HUD" tab: the HUD lifted off the game's picture onto its own floating panel (see HudLayer.cs and XrSession.ShowHud).
    static readonly Item[] HudItems =
    {
        new("HUD panel", "On: the HUD (health, ammo, minimap, quest tracker...) is taken off the game's picture and shown on its own see-through panel at a comfortable distance, so both eyes agree on it and it never fights with the world. Off: the HUD stays in the picture, as in the flat game. Game menus always stay in the picture.",
            OnOff, 0, 1, "", () => Tuning.HudPanel ? 0 : 1, v => Tuning.HudPanel = v < 0.5f),
        new("HUD eye pairing", "M23-0 - fixes doubled or jiggling world markers (mission pointers, text over NPCs, ammo-box and computer icons) in 3D. The game draws the eyes in turn and places those markers for each frame's eye; the panel now shows each eye its own HUD. 'Per eye, swapped' (default): each eye gets the HUD of the other eye's frame - right if the game places markers a frame late, as it looks. 'Per eye': each eye its own frame's HUD. 'One eye': one HUD for both eyes - steady, but markers sit a little off for one eye. Pick whichever makes an ammo box's icon one steady icon up close. In 2D this does nothing.",
            new[] { "Per eye", "Per eye, swapped", "One eye" }, 0, 2, "", () => Tuning.HudEyePairing, v => Tuning.HudEyePairing = Math.Clamp((int)MathF.Round(v), 0, 2)),
        new("HUD distance", "How far ahead of you the panel stands, in metres.",
            null, Tuning.MinHudDistance, Tuning.MaxHudDistance, "0.00' m'", () => Tuning.HudDistance, v => Tuning.HudDistance = MathF.Round(v * 20f) / 20f),
        new("HUD size", "How wide the panel is, in metres (its height follows the game's picture shape). Wider at the same distance = bigger text.",
            null, Tuning.MinHudWidth, Tuning.MaxHudWidth, "0.00' m'", () => Tuning.HudWidth, v => Tuning.HudWidth = MathF.Round(v * 20f) / 20f),
        new("HUD height", "Moves the panel up (+) or down (-), in metres.",
            null, -Tuning.MaxHudHeight, Tuning.MaxHudHeight, "0.00' m'", () => Tuning.HudHeight, v => Tuning.HudHeight = MathF.Round(v * 100f) / 100f),
        new("HUD left / right", "M21-5 - moves the panel right (+) or left (-), in metres. The crosshair does NOT move with it: it stays pinned to where the game really aims, so the reticle always shows where your shots land.",
            null, -Tuning.MaxHudHeight, Tuning.MaxHudHeight, "0.00' m'", () => Tuning.HudSide, v => Tuning.HudSide = MathF.Round(v * 100f) / 100f),
        new("HUD follow", "Head (smooth): the panel stays in front of where you look, easing after your head so glances don't shake it. V's body: it stays in front of where V faces, level, so you can look away from it and back.",
            new[] { "Head (smooth)", "V's body" }, 0, 1, "", () => Tuning.HudFollow, v => Tuning.HudFollow = (int)MathF.Round(v)),
    };

    // RA01: M20-12 - the "Performance" tab: live, read-only gauges (Core\PerfMonitor.cs) so a player can tune settings from inside the headset.
    static readonly Item[] PerfItems =
    {
        new("Game frame rate", "How many pictures a second the game makes. In stereo each eye gets about half of them. Higher is smoother; the headset fills the gaps between them.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => PerfMonitor.Frames),
        new("CPU", "How busy the processor is: Cyberpunk's own share, and the whole PC. Near 100% means the CPU is holding the frame rate back.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => PerfMonitor.Cpu),
        new("GPU", "How busy the graphics card is (the same number as Task Manager's 3D). Near 100% means the graphics card is the limit: lower Game resolution, DLSS quality or graphics presets.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => PerfMonitor.Gpu),
        new("GPU memory", "The graphics card's own memory in use, and how much of it Cyberpunk is using. Above about 90% the game stops loading detail - buildings turn into plain blocks and the ground goes flat. Lower Game resolution or texture quality.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => PerfMonitor.Memory),
        new("PC RAM", "M20-13 - the PC's own memory (not the graphics card's): how much is in use of the total, how much is free, and Cyberpunk's own share. Above about 90% Windows starts moving memory to the disk and the game hitches or stutters. Close background programs (browsers, game launchers, recording apps). For VR, 16 GB is the practical minimum and 32 GB is comfortable.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => PerfMonitor.Ram),
        new("Advice", "A one-line hint from the numbers above: what is holding performance back, and what to change first.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => PerfMonitor.Advice),
    };

    // RA01: M41 - the "Log control" tab: which log files are written in Logs\ (one per category, see Core\Log.cs). All off by default; "All logs" switches every
    // RA01: category at once. Crash & start-up is always written (a status row says so).
    static readonly Item[] LogItems =
    {
        new("All logs", "M41 - CONTROLS: none. Switches every log category below on or off at once. Shows On only while every category is on. Each category writes its own file in the mod's Logs folder (Cyberpunk2077-VR-RA01-Edition\\Logs); each game start begins fresh files and keeps the last run's as ..._previous_<date>.txt. Logs are for finding problems - leave them off to play.",
            OnOff, 0, 1, "", () => Array.TrueForAll(LogRange(), i => Tuning.LogOn[i]) ? 0 : 1, v => { foreach (int i in LogRange()) Tuning.LogOn[i] = v < 0.5f; }),
        new("Crash & start-up log", "M41 - RA01_Crash.txt: the mod starting up, and any serious fault (the crash watcher). Always written - it is tiny, and it is the file to send when the game crashes.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => "always on"),
        LogRow(Log.CoreVr, "RA01_CoreVR.txt: the headset session, the picture each eye gets, the camera, the HUD layer, game resolution, culling and performance."),
        LogRow(Log.Weapons, "RA01_Weapons.txt: guns, aiming, scopes, the crosshair, aim zoom, reloads, gun sounds, the two-handed grip, the laser, melee, power attacks and throws."),
        LogRow(Log.Vrik, "RA01_VRIK.txt: V's body and arms following you, her skeleton, cyberware poses, the Monowire cable and the hand targets."),
        LogRow(Log.Controls, "RA01_Controls.txt: the controller buttons the mod sends, the menu pointer, crouching, ladders, swimming and gestures."),
        LogRow(Log.Vehicles, "RA01_Vehicles.txt: getting in and out, seats, the physical steering wheel and the horn."),
        LogRow(Log.GameData, "RA01_GameData.txt: the mod's reads of the game's own data and its calls into the game (blackboards, menu sensing, exe scans)."),
        LogRow(Log.Menus, "RA01_Menus.txt: this window, the settings file, Auto tune and banners."),
    };
    static int[] LogRange() => new[] { Log.CoreVr, Log.Weapons, Log.Vrik, Log.Controls, Log.Vehicles, Log.GameData, Log.Menus };   // RA01: M41 - the switchable categories
    static Item LogRow(int category, string what) => new(Log.Names[category] + " log", "M41 - CONTROLS: none. On: writes " + what + " Off: not written (no file work at all). Pairs with All logs above.",
        OnOff, 0, 1, "", () => Tuning.LogOn[category] ? 0 : 1, v => Tuning.LogOn[category] = v < 0.5f);

    static readonly Item[] DevItems =
    {
        new("Culling lab: occlusion", "M21-1 TEST - the game hides whatever it thinks is behind 'blocker walls'. Off: it stops building that blocker picture, so nothing is hidden behind walls. If a vanishing room comes back with this off, the blockers are the cause (then the near plane below is the gentle fix). WARNING: off draws everything behind walls - heavy on the GPU, watch the Performance tab; a test, not a setting. Back on by itself at every start. Not saved.",
            OnOff, 0, 1, "", () => CullingLab.OcclusionOn ? 0 : 1, v => CullingLab.SetOcclusion(v < 0.5f)),
        new("Culling lab: near plane", "M21-1 TEST - how close the game's blocker test camera starts, in metres (the game's own: 0.20). At a wide view this 'front glass' gets wide enough to poke through a building's blocker walls next to you and hide your room. Smaller = the glass shrinks back toward your face; occlusion keeps working everywhere else. Stand where a room vanishes and lower it. Not saved.",
            null, 0.01f, 0.5f, "0.00' m'", CullingLab.GetNearPlane, v => CullingLab.SetNearPlane(MathF.Round(v * 100) / 100)),
        new("Culling lab: full test", "M21-1 TEST - the game's own 'Occlusion / ForceFullTest' switch (normally off): every object is tested against the blocker picture in full instead of by quick shortcuts. Not saved.",
            OnOff, 0, 1, "", () => CullingLab.GetSwitch(CullingLab.FullTest, false) ? 0 : 1, v => CullingLab.SetSwitch(CullingLab.FullTest, v < 0.5f)),
        new("Culling lab: shared views", "M21-1 TEST - the game's own 'Occlusion / MultiFrustumUseGlobalOcclusion' switch (normally on): several views share one blocker picture. Not saved.",
            OnOff, 0, 1, "", () => CullingLab.GetSwitch(CullingLab.SharedViews, true) ? 0 : 1, v => CullingLab.SetSwitch(CullingLab.SharedViews, v < 0.5f)),
        new("Culling lab: streaming culling", "M21-1 CONTROL - the game's own 'Streaming / Culling' switch (normally on). On foot it only drops posters and floor decals, and in a fast car it drops what's off to the sides of the road - it should NOT change a vanishing room (if it does, the reading of the code was wrong). Not saved.",
            OnOff, 0, 1, "", () => CullingLab.GetSwitch(CullingLab.Streaming, true) ? 0 : 1, v => CullingLab.SetSwitch(CullingLab.Streaming, v < 0.5f)),
        new("Culling lab: LOD uses render camera", "M21-2 TEST - the game's own 'Debug / LODUpdateUseRenderCamera' switch (normally on). The game picks each object's detail level by how big it looks on screen; a wide view makes everything look smaller, so rooms may switch to their 'seen from outside' version and vanish. Off: detail levels are chosen the game's other way instead of from the camera that draws the picture. If a vanishing room changes when you flip this, detail levels are the cause. Not saved.",
            OnOff, 0, 1, "", () => CullingLab.GetSwitch(CullingLab.LodRenderCamera, true) ? 0 : 1, v => CullingLab.SetSwitch(CullingLab.LodRenderCamera, v < 0.5f)),
        new("Culling camera probe (log)", "M21-3, LOG ONLY - every 5 s, logs the camera the game's blocker system culls with (its lens numbers), next to the picture's own field of view. Turn it on, stand where walls vanish for 10-20 s, turn it off, and send the log. Changes nothing. Not saved.",
            OnOff, 0, 1, "", () => CullingLab.ProbeOn ? 0 : 1, v => CullingLab.ProbeOn = v < 0.5f),
        new("Field name probe (log)", "M44, LOG ONLY - click it once in game, then for the next 30 seconds draw a gun and hold a grenade (no need to throw). Logs, for every object RA01 still reads by a fixed place (the crosshair manager, V, the player and UI systems, V's weapon, the grenade's projectile part), which named field of the game's catalogue - if any - sits at each of those places. The places with a name get found by name next (M44-2); the rest stay fixed. Changes nothing.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: FieldNames.Probe),
        new("Game function catalogue (log)", "M27-6c-1, LOG ONLY - click it once in game (on foot, not a menu). The mod opens the game's own catalogue of classes and functions and logs every function of the game's reload decisions and reload events, and the weapon's functions about reloading and ammunition, with their parameters - so the next step (refilling a gun directly, and stopping the game's own reloads) uses the game's exact names. Changes nothing. If the game was updated, the log says the catalogue needs finding again.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.LogCatalogue),
        new("Reload rule probe (log)", "M27-6c-3a, LOG ONLY - click it once in game. Logs the instructions of the game's own reload decision (the rule that decides 'reload now', for X and for an empty gun) and of a few tiny functions next to it, so the next step can switch that rule off while Physical reloading is on - with the game's own numbers, not guesses. Changes nothing.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.LogReloadRule),
        new("Gun sound probe (log)", "M27-6g-1 TEST - draw a gun and click this once. The mod asks the game (its own built-in functions only) for the sound system and for V's gun's audio name, writes both and the sound call's inputs to the log, then plays that gun's 'magazine in' sound once - listen for it. If the gun's name can't be used, it plays the Ajax rifle's instead (so you still hear whether playing works). Try two or three different guns and send the log. Changes nothing else.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.SoundProbe),
        new("Magazine ammo probe (log)", "M27-7a, LOG ONLY - draw a gun, fire a shot or two, then click this once. Logs the magazine count the HUD shows, the fields of the game's own weapon messages 'use up magazine rounds' and 'set ammo count', and the inputs of the functions that send a message to the gun and give / take ammo - so the 'one round in the chamber' step is built on the game's real layout. Changes nothing.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.AmmoProbe),
        new("Drop to 1 round (test)", "M27-7b TEST - draw a gun with several rounds in the magazine (fire one shot first so the HUD count is known), then click this once. The mod sends the gun the game's own 'use up magazine rounds' message so only 1 round stays (the chambered one) - watch the HUD ammo counter go to 1. With Weapons tab 'Dropped magazine rounds' on Back to reserve, the rest should appear back in your ammo. The log has each step. Save first - this is the first time the mod builds one of the game's own objects.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.ChamberTest),
        new("HMG crosshair probe (log)", "M27-8, LOG ONLY - rip an HMG off a turret, then click this once and keep holding it for about 10 seconds; then switch to a normal gun for the rest of the 20 seconds. Logs every live crosshair manager once a second (which crosshair it holds, its on/off switches, whether the mod switched it off) and what the HMG's own crosshair class is built on - so the HMG crosshair can be switched off too. Changes nothing.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.HmgCrosshairProbe),
        new("HMG crosshair off", "M27-9c - on (the default): while V carries an HMG ripped off a turret ON FOOT, its own crosshair is hidden (the laser shows where it hits). Never in or on a vehicle - vehicle combat keeps its crosshair. Turn it off if the HUD ever misbehaves with the HMG. Not saved.",
            OnOff, 0, 1, "", () => Tuning.DevHmgCrosshairOff ? 0 : 1, v => Tuning.DevHmgCrosshairOff = v < 0.5f),
        new("Refill delay", "M27-5g - physical reloading: when the new magazine goes in, the mod starts the game's own reload and finishes it this long after - the bullets go in at the finish. Raise it if a reload ever leaves the magazine unfilled (2500 ms always worked in the test). Not saved.",
            null, 100f, 3000f, "0' ms'", () => Tuning.RefillDelayMs, v => Tuning.RefillDelayMs = (int)(MathF.Round(v / 50f) * 50f)),
        new("Refill test (half-empty gun)", "M27-6c-2 TEST - the first time the mod CALLS the game's own functions. Fire a few shots so the magazine is part-empty (not empty - the game reloads an empty one by itself), then click this once. The mod checks V's right-hand item really is a weapon, then calls the weapon's own StartReload and, a moment later, StopReload(Standard) - the game's reload without its animation. Watch the HUD ammo counter: does it go back to full? The log has the before and after. Nothing else is changed. If the game was updated, nothing is called.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: GameCall.RefillTest),
        new("Magazine pull test", "M27-6b TEST - CONTROLS: none - switch it on with a gun drawn and look at the gun. On: the mod moves the bone the magazine hangs on (your gun's mag_slot) 10 cm down the gun (the same kind of bone write VRIK does for V's arms) - if the magazine slides out, the mod can carry magazines in your hand later. Switch it off and it snaps back. The log says which magazine bone was moved and which way. Changes nothing else. Not saved.",
            OnOff, 0, 1, "", () => Tuning.DevMagazinePull ? 0 : 1, v => Tuning.DevMagazinePull = v < 0.5f),
        new("Room probe (log)", "M21-8, LOG ONLY - for the vanishing All Foods lab walls: when the lab's big wall mesh loads, the log names its live objects' addresses (for Cheat Engine), and while this is on it logs, twice a second, which of their values changed. Load a save made OUTSIDE the factory, turn this on, walk in, stand where walls vanish, switch the view wide / narrow a few times, then turn it off and send the log. Changes nothing. Not saved.",
            OnOff, 0, 1, "", () => NodeProbe.On ? 0 : 1, v => NodeProbe.On = v < 0.5f),
        new("Wide-view distance fix", "M21-9 - the cure for walls and rooms vanishing at wide VR views (found in The Pickup's All Foods lab). The game was built for flat screens: for any view taller than 75 degrees it stretches every object's distance (x1.7 at 90 degrees tall, x2.4 at 100), so big rooms get dropped as if they were far away. On (default): the stretch is capped at 1, exactly as in a normal flat view, and the view may be up to 130 degrees tall. Off: the game's own stretch, and the view is held to 90 degrees tall again so rooms stay. A little more far-off detail is drawn while on. Saved.",
            OnOff, 0, 1, "", () => DistanceScaleFix.On ? 0 : 1, v => DistanceScaleFix.On = v < 0.5f),
        new("Camera near plane (test)", "M21-7 TEST - the render camera's near plane (the glass right in front of the lens), in metres. 0 = the game's own (nothing changed). If the game decides 'which room am I in' from a small bubble around the camera sized from this glass, a smaller value shrinks the bubble and the walls should stay at a wide view. Stand where walls vanish, try 0.05 then 0.02. Very small values can make far surfaces flicker. The log shows the game's own value and how many camera updates were changed. Not saved.",
            null, 0f, 0.3f, "0.000' m'", () => CullingLab.NearTest, v => CullingLab.SetNearTest(MathF.Round(v * 1000) / 1000)),
        new("Engine debug view", "M21-5 TEST - switches on one of CDPR's own developer drawings, to SEE what hides the walls: Sectors (the rooms the visibility system uses), Interior areas, Interior map bounds / camera (the interior volumes and where the camera sits in them), Shells only (buildings drawn as their outside shells), Blockers (the occluder walls). Stand where walls vanish, step through the choices one at a time, look around, then set Off. Shipped games may have the drawing removed: if nothing shows, the log still says whether the switch was found and set. Not saved.",
            CullingLab.DebugViewNames, 0, CullingLab.DebugViewNames.Length - 1, "", () => CullingLab.DebugView, v => CullingLab.SetDebugView((int)MathF.Round(v))),
        new("Culling lab status", "What the culling lab found in the game (it looks about 15 s after the game starts).",
            null, 0, 1, "", () => 0, _ => { }, Status: () => CullingLab.Status),
        new("V and screens: old method", "M14-0c - the BACKUP. Off (normal): who V is and when a loading screen is up come straight from the game's own records (the game anchor: engine -> player system -> V -> her skeleton; the UI system's loading flag). On: the old ways only - V picked out of every animated body by the hunt, loading guessed from no camera being built. The old ways also take over by themselves if the game's records can't be read (a big game update). Not saved.",
            OnOff, 0, 1, "", () => Tuning.OldVAndScreens ? 0 : 1, v => Tuning.OldVAndScreens = v < 0.5f),
        new("Muzzle: any gun (old)", "M15-2 - the BACKUP. Off (normal): shots leave from the muzzle of V's OWN gun (the one in her right-hand slot). On: the old way - the biggest gun animated nearby, which could be an enemy's (shots then came from the wrong place, drifting off). Not saved.",
            OnOff, 0, 1, "", () => Tuning.MuzzleAnyGun ? 0 : 1, v => Tuning.MuzzleAnyGun = v < 0.5f),
        new("Gun zeroing off", "M15-2 - Off (normal): each calibrated gun's shot is zeroed - it leaves the muzzle aimed to cross the sight line 50 m out, so it lands on the sight's centre. On: straight down the barrel (lands a few cm below the sight). Not saved.",
            OnOff, 0, 1, "", () => Tuning.NoZeroing ? 0 : 1, v => Tuning.NoZeroing = v < 0.5f),
        new("Recalibrate guns", "M15-2 - On: every gun is measured again the next time you aim down its sights (about 2 s), and the new measurement replaces the saved one in RA01_GunCalibration.ini. Turn it off when done. Not saved.",
            OnOff, 0, 1, "", () => Tuning.RecalibrateGuns ? 0 : 1, v => Tuning.RecalibrateGuns = v < 0.5f),
        new("LT aim correction", "M17-2 - TEST, off by default (M17-4: it bent shots 8-11 degrees, too far). On: while LT is held, the game draws V's gun zoomed (bigger, its sights further off the middle of your view than the real barrel); each shot is bent onto where the drawn sights point, so it lands on the reticle you see. Off: shots go straight down the real barrel. Not saved.",
            OnOff, 0, 1, "", () => Tuning.LtAimCorrection ? 0 : 1, v => Tuning.LtAimCorrection = v < 0.5f),
        new("Render camera probe (log)", "M26-0, LOG ONLY - the first step of the real frame lag fix: reads the camera the game's RENDER side actually draws each frame with (exe+0x623FD8, hooked the first time this is switched on) and logs, every 5 s, which of the mod's camera builds it is compared with the one the counting picks. Turn it on, play normally for a minute - look around, walk, go through a menu and a loading screen - then turn it off and send the log. If you see the jiggle at some point, note the time. Changes nothing. Not saved.",
            OnOff, 0, 1, "", () => RenderCameraProbe.On ? 0 : 1, v => RenderCameraProbe.On = v < 0.5f),
        new("Blackboard probe (log)", "M22-0, LOG ONLY - for physical swimming, throws and the crosshair rule: the game keeps switches like 'phone open', 'radio list shown', 'car list shown' and 'swimming' on shared notice boards (blackboards). This copies every change into the log. Turn it on, wait 3 seconds, then with pauses of about 10 s between: open and close the phone, the car list, the radio (on foot is fine); swim, dive, climb out; quick-tap a grenade. Turn it off and send the log. Changes nothing (the crosshair rule's popup check runs either way; this only adds the log lines). Not saved.",
            OnOff, 0, 1, "", () => BlackboardProbe.On ? 0 : 1, v => BlackboardProbe.On = v < 0.5f),
        new("Throw probe (test)", "M21-4, LOG ONLY - for physical grenade and knife throws: logs every change of the game's grenade state (in hand, charging, throwing, quick throw) and knife throw, your right hand's position and speed at that moment, and the aim answers the game uses for V's throw (start and direction); M22-2 - plus every flight arc the game sets up (the first switch-on hooks the arc setup, about a millisecond's pause). Changes nothing. Turn it on, throw a few grenades (held and quick) and a knife a few times, turn it off. Not saved.",
            OnOff, 0, 1, "", () => Tuning.ThrowProbe ? 0 : 1, v => Tuning.ThrowProbe = v < 0.5f),
        new("Aim switch probe (test)", "M17-0, LOG ONLY - with a gun out: logs which of the gun's (and V's) values change when you press LT (to find what opens the scope by itself), and how far each of the game's own shots leans off the view's centre (the spread) with and without LT. Changes nothing. Not saved.",
            OnOff, 0, 1, "", () => Tuning.AimSwitchProbe ? 0 : 1, v => Tuning.AimSwitchProbe = v < 0.5f),
        new("State probe (test)", "M19-0/M20-0, LOG ONLY - logs which of V's values change (by name) - getting into vehicles, grenades, swimming - and when a braindance starts and ends. For the next physical systems. Changes nothing. Not saved.",
            OnOff, 0, 1, "", () => Tuning.VehicleProbe ? 0 : 1, v => Tuning.VehicleProbe = v < 0.5f),
        new("Scope lens: round", "M16-A - On (normal): the scope magnifier is a disc (its square's corners are see-through). Off: a square lens - use it if the round lens shows nothing (the game's picture may carry no see-through information). Not saved.",
            OnOff, 0, 1, "", () => Tuning.ScopeLensRound ? 0 : 1, v => Tuning.ScopeLensRound = v < 0.5f),
        new("Melee: timing only (old)", "M14-6 - the BACKUP. Off (normal): each physical swing is kept until the game takes it - if V is busy (mid-attack, staggered) the mod presses again the moment the game's own melee state changes, and drops the swing after 0.8 s. On: the M14-4 way - fixed timing and a queue, not watching the game. Not saved.",
            OnOff, 0, 1, "", () => Tuning.MeleeTimingOnly ? 0 : 1, v => Tuning.MeleeTimingOnly = v < 0.5f),
        new("Weapon reading: old method", "M14-5 - the BACKUP. Off (normal): what V holds is read from the game's own record of V's right-hand weapon slot (it only ever describes V - an enemy drawing a gun can't change it), named by the game's weapon events. On: the older way - the game's weapon events and the skeletons (an enemy's gun could switch your swings to gun bashes). Not saved.",
            OnOff, 0, 1, "", () => Tuning.OldWeaponReading ? 0 : 1, v => Tuning.OldWeaponReading = v < 0.5f),
        new("Weapon reading: skeleton only", "M13-32 - the BACKUP. Off (normal): what V holds comes from the game's own data - the exact weapon type the game announces when a weapon goes active (Katana, Handgun, Fists, Monowire...), with her skeletons only telling whether anything is in her hands. On: the old way only - guessed from which weapon skeletons are moving. For comparing, or if the game's data ever reads wrong. Not saved.",
            OnOff, 0, 1, "", () => Tuning.SkeletonWeaponOnly ? 0 : 1, v => Tuning.SkeletonWeaponOnly = v < 0.5f),
        new("Bend the shot (test)", "M13-47 - for finding the aim line again after a game update. Turns rays from the game's multi-hit ray runner (the kind that collects every surface along a line, as a bullet needs) 20 degrees to the right just before PhysX checks them: from all three of its callers, or only caller 1, 2 or 3 (keys 7, 8, 9 pick the same while the test is on). Fire single shots at a wall: holes to the right of where you aimed = that caller is the aim line (caller 1 in 2.31). Logs the ray tally once a second while on. Not saved.",
            new[] { "Off", "All callers", "Caller 1", "Caller 2", "Caller 3" }, 0, 4, "", () => Tuning.BendTest, v => Tuning.BendTest = (int)MathF.Round(v)),
        new("Cyberware strike: old blend", "M14-1d - the BACKUP. Off (normal): a Mantis Blade or Gorilla Arm strike plays the game's own draw animation, filmed when you draw them, forward and back - the blade slides out and swings over the top like the game's. On: the old M14-1c way - a straight morph from a folded photo to an open photo (can spin the blades the wrong way round). Not saved.",
            OnOff, 0, 1, "", () => Tuning.OldCyberwareBlend ? 0 : 1, v => Tuning.OldCyberwareBlend = v < 0.5f),
        new("Shrink game HUD (test)", "M15-0, TEST - shrinks the game's OWN whole HUD to half size, evenly, toward the middle of the game picture (every piece keeps its place, like shrinking a poster on a photocopier), so its crosshair sits on the true aim by itself. RA01's own HUD panel is untouched - turn HUD panel off in the HUD tab to see the game's HUD in its picture. Not saved.",
            OnOff, 0, 1, "", () => Tuning.HudShrinkTest ? 0 : 1, v => Tuning.HudShrinkTest = v < 0.5f),
        new("Wire test (Monowire)", "DEV CHECK (M38-1) - while the Monowire is out and the mod lays its right cable (idle, or pinned to the other hand), that cable is aimed straight UP from its bracket instead of at your other hand, by the same code that normally lays it. If it points up in the headset, the mod's cable writes reach the screen; if nothing changes, the game redraws the wire after the mod. Not saved.",
            OnOff, 0, 1, "", () => Tuning.WireTest ? 0 : 1, v => Tuning.WireTest = v < 0.5f),
        new("Deactivate leash", "The head is normally tied to your standing spot by a comfort leash (see the Body follows head tab): lean or crouch more than 0.6m from where you last stood and it holds you at that 0.6m mark instead of letting the camera keep drifting away from where your character actually stands - this is what stops leaning or crouching from ever feeling like you've left your own body. Turning this on removes that limit entirely, so a bone hunt's forward lean travels exactly as far as your real head does instead of being capped. Leave this off for normal play; it always turns back on by itself the next time you start the game.",
            OnOff, 0, 1, "", () => Tuning.LeashDisabled ? 0 : 1, v => Tuning.LeashDisabled = v < 0.5f),
        new("Capture draw passes", "M10-0 - records ONE frame of the game's drawing - every picture it paints on, in the order the graphics card runs it - and writes it to the log. For hunting down where the game paints something.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: HudLayer.Capture),
        new("Draw-pass status", "What the draw-pass capture is doing right now, and whether the HUD layer has been found.",
            null, 0, 1, "", () => 0, _ => { }, Status: HudLayer.Status),
        new("Bone discovery", "Starts a one-off, read-only search of the game's own memory for the skeleton (the F11 key does the same thing). Get into the game on foot first, not a menu, then watch Status below - it says when to hold still and when to move. When it says to move: walk your character in a slow, easy circle for the full window, big enough to swing your legs naturally - a real bone swings back and forth as you walk; anything that just glides along without swinging (like the camera itself) gets ruled out, not kept.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: BoneFinder.Start),
        new("Status", "What the bone hunt is doing right now.",
            null, 0, 1, "", () => 0, _ => { }, Status: BoneFinder.Status),
        new("Find root pointer", "Run this after Bone discovery has found something worth keeping. The raw address it found only works for this one play session - Windows moves the game's memory around on every launch. This searches the game's own executable (which does NOT get reshuffled internally, only relocated as a whole) for a fixed spot that points back to the candidate; a hit gives two numbers that should still work after a restart, logged for you to note down.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: PointerFinder.Start),
        new("Root pointer status", "What the pointer hunt is doing right now.",
            null, 0, 1, "", () => 0, _ => { }, Status: PointerFinder.Status),
        new("Dump skeleton", "Reads the fixed hook found by Find root pointer and logs every bone slot once - position and distance from the slot before it, for checking against the WolvenKit rest-pose file. Only meaningful once Find root pointer has confirmed module+0x48C6DF0 this launch.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: SkeletonHook.DumpAll),
        new("V skeleton", "M7-6 - whether the V-only doorway has found V's own bone list yet (see PlayerSkeleton.cs). It finds her by herself a second or two after any load, male or female; if it says the layout looks different, a game update has changed the bone data and nothing is being touched.",
            null, 0, 1, "", () => 0, _ => { }, Status: PlayerSkeleton.Status),
        new("Dump pointer table", "M6-20 - reads a wide stretch of fixed memory around the pointer table Find root pointer turned up near module+0x34B99E8, room for well over a thousand pointers: for every one in that stretch, searches the memory just past wherever it leads for anything sitting close to the camera, and logs what it finds. The idea: if this table really is one pointer per bone laid out in a row, this reads most of the skeleton in one pass. Meaningful any time the game is running - it does not depend on a hunt from this launch. Stand up straight and hold still for this one; no walking needed.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: PointerTableDump.Dump),
        new("Radiate from known bones", "M6-21 - walks the skeleton outward one hop at a time from whatever is already trusted: the camera, plus anything Bone discovery already confirmed this launch. Each click searches the whole heap for points close enough to any known point to plausibly be its neighbour, logs what it finds, then folds those into the known list so the next click hops one step further out. Stand up straight and hold still, then click this once to take the first hop; click it again afterward to keep going, or use Reset radiate below to start over from just the camera.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: SkeletonRadiate.Start),
        new("Radiate status", "What the radiate search is doing right now.",
            null, 0, 1, "", () => 0, _ => { }, Status: SkeletonRadiate.Status),
        new("Reset radiate", "Clears the radiate search's known list, so the next click of Radiate from known bones starts fresh from just the camera (and Bone discovery's survivors, if any) instead of everything found so far.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: SkeletonRadiate.Reset),
    };

    // RA01: M43 - the "Dev_Rehook" tab: the Rehooker's report live (Core\GameData\HookRegistry.cs) - the game build, where the addresses came from, which player
    // RA01: features are off, and one status row per hook or game address (FOUND / MOVED / OFF), plus a button that writes Logs\RA01_Rehooker.txt again.
    // RA01: Cost - CPU: extremely low (a few short strings per drawn row, only while the tab is showing); GPU: none (drawn with the rest of the window).
    static readonly Item[] RehookItems = new Item[]
    {
        new("Game build", "M43 - CONTROLS: none. The running game's exe stamp, and whether it is 2.31 - the build RA01 was made on. On another build the addresses come from the address list (see the next row).",
            null, 0, 1, "", () => 0, _ => { }, Status: () => HookRegistry.BuildLine),
        new("Address list", "M43 - CONTROLS: none. On a new game build: where this build's addresses came from - Settings\\RA01_Addresses_<build>.ini (made offline by RA01_HookCheck.py), or a cyberpunk2077_addresses.json for this exact build (Settings, bin\\x64 or red4ext), read once and saved as that ini.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => AddressList.Status),
        new("Features off", "M43 - CONTROLS: none. The player features with a part that could not be hooked on this game build (Dev-only rows don't count). The game runs as normal at every one of them - only that RA01 feature is missing.",
            null, 0, 1, "", () => 0, _ => { }, Status: () => HookRegistry.FeaturesOff() is { Count: > 0 } off ? string.Join(", ", off) : "none"),
        new("Write the report again", "M43 - CONTROLS: none. Writes the Rehooker report again to Logs\\RA01_Rehooker.txt (it is also written at every start, and again when a late hook reports). The file to send after a game update.",
            null, 0, 1, "", () => 0, _ => { }, Trigger: HookRegistry.WriteReport),
    }.Concat(HookRegistry.Rows.Select((row, i) => new Item(row.Name,
        $"M43 - CONTROLS: none. Feature: {row.Feature}{(row.Dev ? " (Dev only)" : "")}. 2.31 place: {(row.Rva231 == 0 ? "not in the exe" : $"0x{row.Rva231:X}")}; part number: {(row.Hash == 0 ? "none" : row.Hash.ToString())}{(row.InFunction != 0 ? $" +0x{row.InFunction:X}" : "")}; checked by: {(row.Pattern.Length == 0 ? "its own code" : row.Pattern)}. FOUND = where 2.31 had it, MOVED = found elsewhere and checked by its bytes, OFF = not used this start.",
        null, 0, 1, "", () => 0, _ => { }, Status: () => HookRegistry.Line(i)))).ToArray();

    public static readonly Item[] Items = VideoItems.Concat(ControlItems).Concat(VrikItems).Concat(HudItems).Concat(WeaponItems).Concat(VehicleItems).Concat(PerfItems).Concat(LogItems).Concat(DevItems).Concat(RehookItems).ToArray(); // RA01: M6-1, M6-6 - one flat list underneath every tab, in tab order, so IpdItem above still points at the right row
    public static readonly int[] TabStart = { 0, VideoItems.Length, VideoItems.Length + ControlItems.Length, VideoItems.Length + ControlItems.Length + VrikItems.Length, VideoItems.Length + ControlItems.Length + VrikItems.Length + HudItems.Length, VideoItems.Length + ControlItems.Length + VrikItems.Length + HudItems.Length + WeaponItems.Length, VideoItems.Length + ControlItems.Length + VrikItems.Length + HudItems.Length + WeaponItems.Length + VehicleItems.Length, VideoItems.Length + ControlItems.Length + VrikItems.Length + HudItems.Length + WeaponItems.Length + VehicleItems.Length + PerfItems.Length, VideoItems.Length + ControlItems.Length + VrikItems.Length + HudItems.Length + WeaponItems.Length + VehicleItems.Length + PerfItems.Length + LogItems.Length , VideoItems.Length + ControlItems.Length + VrikItems.Length + HudItems.Length + WeaponItems.Length + VehicleItems.Length + PerfItems.Length + LogItems.Length + DevItems.Length }; // RA01: M6-1, M6-6, M41 - where each tab's own items begin in Items
    public static readonly int[] TabCount = { VideoItems.Length, ControlItems.Length, VrikItems.Length, HudItems.Length, WeaponItems.Length, VehicleItems.Length, PerfItems.Length, LogItems.Length, DevItems.Length, RehookItems.Length };       // RA01: M6-1, M6-6 - how many items each tab has
    public static int Tab;                                                            // RA01: M6-1 - which tab is showing (an index into TabNames); kept across the window closing and reopening, the same as ScrollTop is

    static readonly float DesignHeight = 44 + TabBarUnits + 56 * VisibleRows + 60; // RA01: M5a-14, M6-1 - the layout's design height in units: the title bar, the tab bar, VisibleRows rows (not every item any more), the buttons and a margin

    public static readonly string[] ButtonNames = { "Recentre", "Own IPD", "Reset all" };
    static readonly string[] ButtonTips =
    {
        "Makes where you are looking now the front. Same as the Home key. Look straight ahead first.",
        "Goes back to the eye distance your headset measured for you.",
        "Puts the settings above back to how they are at a fresh start, except Head steering. The window's own size and place stay as they are."
    };

    public static bool Open;                   // RA01: whether the window is showing
    public static float CursorX, CursorY;      // RA01: the mouse cursor on the panel, in panel pixels
    public static int Hover = -1;              // RA01: the entry of Hits under the cursor (-1 = none)
    public static int HoverEdges;              // RA01: the window edges under the cursor (1 left, 2 right, 4 top, 8 bottom, together at a corner)
    public static bool HoverTitle;             // RA01: the cursor is on the title bar
    public static bool RecentreClicked;        // RA01: set by the Recentre button, taken by XrSession
    public static Rect Window, TitleBar;       // RA01: the window and its title bar, in panel pixels (Layout)
    public static Rect Footer;                 // RA01: M65 - the fixed strip under the buttons for the Militech line (Layout); it never scrolls
    public static readonly Rect[] Rows = new Rect[Items.Length]; // RA01: each setting's row in the window; only the VisibleRows currently scrolled into view are current, the rest are stale and not drawn
    public static readonly Hit[] Hits = new Hit[40];             // RA01: every clickable spot (Layout)
    public static int HitCount;
    public static float Scale;                 // RA01: the window's size relative to the layout's design size (420 wide, DesignHeight tall)
    public static int ScrollTop;                // RA01: M5a-14 - the Items index of the first row currently shown (0 = the top of the list); kept across the window closing and reopening
    public static int MaxScroll;                 // RA01: M5a-14 - the largest ScrollTop can be (Items.Length - VisibleRows, or 0 if everything already fits); set by Layout, read by Press

    // RA01: M8-3 - moves the list by whole rows (negative = up), for the thumbstick scrolling in MenuPointer; clamped the same way the arrow spots are.
    // Cost - CPU: extremely low (one clamp); GPU: none.
    public static void ScrollBy(int rows) => ScrollTop = Math.Clamp(ScrollTop + rows, 0, MaxScroll);

    static int dragKind;                       // RA01: 0 = no drag, 1 = a slider, 2 = moving the window, 3 = resizing it
    static int grabbed, dragEdges;             // RA01: the slider held (entry of Hits), and the edges being dragged
    static Rect start;                         // RA01: the window when a move or resize began
    static float startX, startY;               // RA01: the cursor then
    static bool wasDown;                       // RA01: the mouse button last time, to find presses and releases
    static bool drawn;                         // RA01: something has been drawn since the window opened
    static long drawnSignature;                // RA01: what the window looked like when it was last drawn

    // RA01: opens or closes the window (F10). It opens with the cursor in the middle of the window and everything to be drawn again.
    // Cost - CPU: extremely low (only when F10 or the close button is pressed; a few assignments and one Layout); GPU: none (no GPU work).
    public static void Toggle()
    {
        Open = !Open;
        dragKind = 0;
        drawn = false;
        wasDown = true; // RA01: a button held while it opens is not a click until it has been let go
        Hover = -1;
        HoverEdges = 0;
        HoverTitle = false;
        if (!Open) return;
        Layout();
        CursorX = (Window.Left + Window.Right) / 2f;
        CursorY = (Window.Top + Window.Bottom) / 2f;
    }

    // RA01: the window's place and size are kept inside their limits, and the window inside the panel (a saved file may say anything).
    // Cost - CPU: extremely low (four clamps, run on every Layout while the window is open); GPU: none (no GPU work).
    static void Fit()
    {
        Tuning.MenuWidth = Math.Clamp(Tuning.MenuWidth, Tuning.MinMenuWidth, Tuning.MaxMenuWidth);
        Tuning.MenuHeight = Math.Clamp(Tuning.MenuHeight, Tuning.MinMenuHeight, Tuning.MaxMenuHeight);
        Tuning.MenuX = Math.Clamp(Tuning.MenuX, 0, PanelWidth - Tuning.MenuWidth);
        Tuning.MenuY = Math.Clamp(Tuning.MenuY, 0, PanelHeight - Tuning.MenuHeight);
    }

    // RA01: adds one clickable spot to Hits (used by Layout).
    // Cost - CPU: extremely low (one struct store, about 40 per Layout); GPU: none (no GPU work).
    static void Add(int kind, int item, int value, int left, int top, int right, int bottom) =>
        Hits[HitCount++] = new Hit { Kind = kind, Item = item, Value = value, Box = new Rect { Left = left, Top = top, Right = right, Bottom = bottom } };

    // RA01: works out where everything is from the window's size: the design is 420 units wide and DesignHeight tall, and one unit is Scale pixels (the smaller of the two ratios, so nothing is ever cut off).
    // M5a-14 - only VisibleRows Items are laid out, the ones from ScrollTop; a narrow column on the right of the list (ScrollColumn) is kept clear for the up/down arrow spots, which are always there (dimmed by MenuDraw when there is nowhere to scroll).
    // Cost - CPU: extremely low (about 30 rectangles of integer math with no allocation, a few microseconds; runs about twice per headset frame while the window is open); GPU: none (no GPU work).
    public static void Layout()
    {
        Fit();
        int x = Tuning.MenuX, y = Tuning.MenuY, w = Tuning.MenuWidth, h = Tuning.MenuHeight;
        Window = new Rect { Left = x, Top = y, Right = x + w, Bottom = y + h };
        float s = Scale = Math.Min(h / DesignHeight, w / 420f);
        // RA01: units of the layout design to pixels, using the current Scale.
        // RA01: Cost - CPU: extremely low (one multiply and round); GPU: none (no GPU work).
        int U(float units) => (int)MathF.Round(units * s);
        TitleBar = new Rect { Left = x, Top = y, Right = x + w, Bottom = y + U(36) };
        int left = x + U(16), fullWidth = w - 2 * U(16), rowWidth = fullWidth - U(ScrollColumn);
        int tabStart = TabStart[Tab], tabCount = TabCount[Tab];
        MaxScroll = Math.Max(0, tabCount - VisibleRows);
        ScrollTop = Math.Clamp(ScrollTop, 0, MaxScroll);
        HitCount = 0;
        Add(KindClose, 0, 0, x + w - U(32), y + U(4), x + w - U(4), y + U(32));

        // RA01: M6-1 - the tab strip, sized the same "however many there are" way the buttons below already are.
        int tabTop = y + U(40), tabBottom = y + U(40 + TabBarUnits - 6), tabGap = U(6);
        int tabWidth = (fullWidth - (TabNames.Length - 1) * tabGap) / TabNames.Length;
        for (int t = 0; t < TabNames.Length; t++)
            Add(KindTab, t, 0, left + t * (tabWidth + tabGap), tabTop, left + t * (tabWidth + tabGap) + tabWidth, tabBottom);

        int listTop = y + U(44 + TabBarUnits), listBottom = y + U(44 + TabBarUnits + 56 * VisibleRows), arrowLeft = left + rowWidth + U(8), arrowRight = left + fullWidth;
        Add(KindScrollUp, 0, 0, arrowLeft, listTop, arrowRight, listTop + U(32));
        Add(KindScrollDown, 0, 0, arrowLeft, listBottom - U(32), arrowRight, listBottom);
        for (int slot = 0; slot < VisibleRows && ScrollTop + slot < tabCount; slot++)
        {
            int i = tabStart + ScrollTop + slot;
            int top = y + U(44 + TabBarUnits + 56 * slot);
            Rows[i] = new Rect { Left = left, Top = top, Right = left + rowWidth, Bottom = top + U(52) };
            Item it = Items[i];
            if (it.Status != null) continue; // RA01: M6-6 - a label-only row: nothing to click
            if (it.Trigger != null) { Add(KindItemButton, i, 0, left, top + U(4), left + rowWidth, top + U(48)); continue; } // RA01: M6-6 - the whole row is one push button
            string[]? options = it.Options;
            if (options == null) { Add(KindSlider, i, 0, left, top + U(24), left + rowWidth, top + U(50)); continue; }
            int each = Math.Min(U(120), rowWidth / options.Length);
            for (int k = 0; k < options.Length; k++)
                Add(KindOption, i, k, left + k * each, top + U(24), left + (k + 1) * each, top + U(50));
        }
        int buttonsTop = y + U(44 + TabBarUnits + 56 * VisibleRows + 4), gap = U(10), buttonWidth = (fullWidth - (ButtonNames.Length - 1) * gap) / ButtonNames.Length; // RA01: M5a-14 - sized from however many buttons there are, not hard-coded to 3, now that there are 4
        for (int b = 0; b < ButtonNames.Length; b++)
            Add(KindButton, b, 0, left + b * (buttonWidth + gap), buttonsTop, left + b * (buttonWidth + gap) + buttonWidth, buttonsTop + U(36));
        Footer = new Rect { Left = left, Top = buttonsTop + U(38), Right = left + fullWidth, Bottom = buttonsTop + U(54) }; // RA01: M65 - in the margin already under the buttons: nothing moves
    }

    // RA01: the mouse, once per headset frame: how far it moved (raw counts), and whether the left button is down. Moves the cursor by that much, then hands off to Act below.
    // Cost - CPU: extremely low (once per headset frame while the window is open; cursor math plus Act's own cost); GPU: none (no GPU work).
    public static void Input(int dx, int dy, bool down)
    {
        CursorX = Math.Clamp(CursorX + dx * Sensitivity, 0, PanelWidth - 1);
        CursorY = Math.Clamp(CursorY + dy * Sensitivity, 0, PanelHeight - 1);
        Act(down);
    }

    // RA01: M6-1 - MenuPointer's own way in: a panel pixel straight from its aim-ray math (an absolute spot, unlike the mouse's relative move above), and whether its click button
    // is down. Lets a motion controller work the window exactly like a mouse would, without MenuPointer needing to know anything about Layout, Hits or dragging.
    // Cost - CPU: extremely low (two clamps then Act's own cost); GPU: none (no GPU work).
    public static void Point(float panelX, float panelY, bool down)
    {
        CursorX = Math.Clamp(panelX, 0, PanelWidth - 1);
        CursorY = Math.Clamp(panelY, 0, PanelHeight - 1);
        Act(down);
    }

    // RA01: M6-1 - shared by Input and Point once the cursor is wherever it belongs this frame: carries on a drag, lays the window out, finds what is under the cursor, and turns a
    // fresh press or release into a click.
    // Cost - CPU: extremely low (a drag's own math (rare) plus one Layout and a hit test, a few microseconds); GPU: none (no GPU work).
    static void Act(bool down)
    {
        bool pressed = down && !wasDown, released = !down && wasDown;
        wasDown = down;
        if (dragKind != 0) Drag();
        Layout();
        Locate();
        if (pressed) Press();
        if (released) Release();
    }

    // RA01: true when the point is inside the rectangle (the left and top edges count, the right and bottom do not).
    // Cost - CPU: extremely low (four compares); GPU: none (no GPU work).
    static bool Inside(in Rect r, float x, float y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // RA01: what is under the cursor: a clickable spot, else a window edge, else the title bar.
    // Cost - CPU: extremely low (a loop over at most 40 boxes, once per headset frame while the window is open); GPU: none (no GPU work).
    static void Locate()
    {
        Hover = -1;
        for (int i = 0; i < HitCount; i++)
            if (Inside(Hits[i].Box, CursorX, CursorY)) { Hover = i; break; }
        HoverEdges = 0;
        HoverTitle = false;
        if (Hover >= 0 || !Inside(Window, CursorX, CursorY)) return;
        if (CursorX < Window.Left + Border) HoverEdges |= 1;
        if (CursorX >= Window.Right - Border) HoverEdges |= 2;
        if (CursorY < Window.Top + Border) HoverEdges |= 4;
        if (CursorY >= Window.Bottom - Border) HoverEdges |= 8;
        HoverTitle = HoverEdges == 0 && Inside(TitleBar, CursorX, CursorY);
    }

    // RA01: the mouse button went down: acts on the spot under the cursor (a radio choice is set, a slider is grabbed, a button runs, the close button closes), or starts a move or a resize when an edge or the title bar was hit.
    // Cost - CPU: extremely low (only on a click; a few assignments and one log line); GPU: none (no GPU work).
    static void Press()
    {
        if (Hover >= 0)
        {
            Hit hit = Hits[Hover];
            switch (hit.Kind)
            {
                case KindOption: Apply(Items[hit.Item], hit.Value); Log.Write($"RA01 menu: {Items[hit.Item].Name} = {Items[hit.Item].Options![hit.Value]}"); break;
                case KindSlider: dragKind = 1; grabbed = Hover; SetSlider(); break;
                case KindButton: Button(hit.Item); break;
                case KindItemButton: Items[hit.Item].Trigger!(); Log.Write($"RA01 menu: {Items[hit.Item].Name}"); break; // RA01: M6-6
                case KindClose: Toggle(); break;
                case KindScrollUp: ScrollTop = Math.Max(0, ScrollTop - ScrollStep); break; // RA01: M5a-14 - clamped again next Layout anyway; clamped here too so Hover/drawing this same frame is not one step stale
                case KindScrollDown: ScrollTop = Math.Min(MaxScroll, ScrollTop + ScrollStep); break;
                case KindTab: if (Tab != hit.Item) { Tab = hit.Item; ScrollTop = 0; Log.Write($"RA01 menu: tab = {TabNames[hit.Item]}"); } break; // RA01: M6-1 - scrolling starts back at the top of the new tab's own list
            }
        }
        else if (HoverEdges != 0 || HoverTitle)
        {
            dragKind = HoverEdges != 0 ? 3 : 2;
            dragEdges = HoverEdges;
            start = Window;
            startX = CursorX;
            startY = CursorY;
        }
    }

    // RA01: the mouse button came up: a slider that was being dragged has its final value logged, and any drag ends.
    // Cost - CPU: extremely low (only on a button release; at most one log line); GPU: none (no GPU work).
    static void Release()
    {
        if (dragKind == 1) Log.Write($"RA01 menu: {Items[Hits[grabbed].Item].Name} = {ValueText(Hits[grabbed].Item)}");
        dragKind = 0;
    }

    // RA01: runs a bottom button: Recentre asks XrSession to recentre, Own IPD clears the IPD override, Reset all restores the defaults. It is logged.
    // Cost - CPU: extremely low (only on a click; a few assignments and one log line); GPU: none (no GPU work).
    static void Button(int button)
    {
        switch (button)
        {
            case 0: RecentreClicked = true; break;
            case 1: Tuning.IpdMm = 0; break;
            case 2: Tuning.ResetAll(); break;
        }
        Log.Write($"RA01 menu: {ButtonNames[button]}");
    }

    // RA01: sets an item to a value that is first pulled into the item's own range.
    // Cost - CPU: extremely low (one clamp and one delegate call, on a click or per frame while a slider is dragged); GPU: none (no GPU work).
    static void Apply(Item item, float value) => item.Set(Math.Clamp(value, item.Min, item.Max));

    // RA01: the held slider follows the cursor sideways along its track (the knob's own half width is kept clear at each end).
    // Cost - CPU: extremely low (a little float math and one Apply, per headset frame while a slider is held); GPU: none (no GPU work).
    static void SetSlider()
    {
        Hit hit = Hits[grabbed];
        Item item = Items[hit.Item];
        int half = (int)MathF.Round(8 * Scale);
        float t = Math.Clamp((CursorX - hit.Box.Left - half) / Math.Max(1, hit.Box.Right - hit.Box.Left - 2 * half), 0f, 1f);
        Apply(item, item.Min + t * (item.Max - item.Min));
    }

    // RA01: a drag in progress: a slider follows the cursor; a move keeps the window's offset from the cursor; a resize moves the dragged edges and keeps the opposite ones where they are.
    // the window stays inside the size limits and inside the panel (which is as big as the biggest window, so the panel's edges are the biggest size).
    // Cost - CPU: extremely low (a few clamps and assignments, per headset frame while a drag is in progress); GPU: none (no GPU work).
    static void Drag()
    {
        if (dragKind == 1) { SetSlider(); return; }
        int dx = (int)MathF.Round(CursorX - startX), dy = (int)MathF.Round(CursorY - startY);
        if (dragKind == 2)
        {
            Tuning.MenuX = start.Left + dx; // RA01: Fit keeps it inside the panel
            Tuning.MenuY = start.Top + dy;
            return;
        }
        int l = start.Left, r = start.Right, t = start.Top, b = start.Bottom;
        if ((dragEdges & 1) != 0) l = Math.Clamp(start.Left + dx, 0, r - Tuning.MinMenuWidth);
        if ((dragEdges & 2) != 0) r = Math.Clamp(start.Right + dx, l + Tuning.MinMenuWidth, PanelWidth);
        if ((dragEdges & 4) != 0) t = Math.Clamp(start.Top + dy, 0, b - Tuning.MinMenuHeight);
        if ((dragEdges & 8) != 0) b = Math.Clamp(start.Bottom + dy, t + Tuning.MinMenuHeight, PanelHeight);
        Tuning.MenuWidth = r - l;
        Tuning.MenuHeight = b - t;
        Tuning.MenuX = l;
        Tuning.MenuY = t;
    }

    // RA01: the value of a setting as text, like "64.0 mm" or "Swapped".
    // Cost - CPU: extremely low (one number format and a small string, about 12 per repaint of the open window); GPU: none (no GPU work).
    public static string ValueText(int item)
    {
        Item it = Items[item];
        float value = it.Get();
        if (it.Options != null) return it.Options[Math.Clamp((int)MathF.Round(value), 0, it.Options.Length - 1)];
        if (it.Text != null) return it.Text();                                        // RA01: M24-2
        string text = value.ToString(it.Format, CultureInfo.InvariantCulture);
        return item == IpdItem && Tuning.IpdMm == 0 ? text + " (the headset's own)" : text;
    }

    // RA01: the tooltip for whatever is under the cursor, in plain words ("" = nothing to say).
    // Cost - CPU: extremely low (a few compares and a string lookup, once per repaint of the open window); GPU: none (no GPU work).
    public static string Tip()
    {
        if (Hover >= 0)
        {
            Hit hit = Hits[Hover];
            return hit.Kind switch
            {
                KindButton => ButtonTips[hit.Item],
                KindClose => "Closes this window. F10 does the same.",
                KindScrollUp => ScrollTop > 0 ? "Scrolls the list up to see the settings above." : "Already at the top of the list.",
                KindScrollDown => ScrollTop < MaxScroll ? "Scrolls the list down to see more settings." : "Already at the bottom of the list.",
                KindTab => Tab == hit.Item ? $"Already showing {TabNames[hit.Item]}." : $"Shows the {TabNames[hit.Item]} settings.",
                _ => Items[hit.Item].Tip
            };
        }
        return HoverEdges != 0 ? "Drag an edge or a corner to make the window bigger or smaller." : HoverTitle ? "Drag this bar to move the window." : "";
    }

    // RA01: true (once) when the Recentre button was clicked.
    // Cost - CPU: extremely low (one bool read and clear, once per headset frame); GPU: none (no GPU work).
    public static bool TakeRecentre()
    {
        bool clicked = RecentreClicked;
        RecentreClicked = false;
        return clicked;
    }

    // RA01: a number that changes whenever anything that is drawn changes: the window's place and size, the cursor, what is under it, and every value. The window is drawn again only then.
    // Cost - CPU: extremely low (about 30 multiply-adds and 20 value reads, no allocation, once per headset frame while the window is open); GPU: none (no GPU work).
    static long Signature()
    {
        long h = 17;
        // RA01: folds one number into the running signature.
        // RA01: Cost - CPU: extremely low (one multiply and add); GPU: none (no GPU work).
        void Mix(long v) => h = h * 1000003 + v;
        Mix(Tuning.MenuX); Mix(Tuning.MenuY); Mix(Tuning.MenuWidth); Mix(Tuning.MenuHeight);
        Mix((int)CursorX); Mix((int)CursorY); Mix(Hover); Mix(HoverEdges); Mix(dragKind);
        Mix(Tuning.IpdMm == 0 ? 1 : 0); Mix(ScrollTop); Mix(Tab); // RA01: M5a-14, M6-1 - scrolling and switching tabs both change what is drawn even though no value changed
        foreach (Item item in Items)
        {
            Mix(BitConverter.SingleToInt32Bits(item.Get()));
            if (item.Status != null) Mix(item.Status().GetHashCode()); // RA01: M6-6 - a Status row's text can change with nothing "set", so it needs its own mix-in to trigger a redraw
        }
        return h;
    }

    // RA01: true when the window must be drawn again: it has not been drawn since it opened, or something that is drawn has changed.
    // Cost - CPU: extremely low (one Signature call, once per headset frame while the window is open); GPU: none (no GPU work).
    public static bool NeedsDraw() => !drawn || Signature() != drawnSignature;

    // RA01: called after the window has been drawn and handed to the headset.
    // Cost - CPU: extremely low (one Signature call, only after a repaint); GPU: none (no GPU work).
    public static void MarkDrawn()
    {
        drawn = true;
        drawnSignature = Signature();
    }
}
