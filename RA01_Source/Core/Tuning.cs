namespace RA01.Core;

// RA01: M4b-3, M5a, M5a-2, M5a-3, M5a-4 - the values that can be adjusted while playing. They are all set from the F10 settings window (UI_settingsMenus\SettingsMenu.cs)
// and saved by UI_settingsMenus\SettingsFile.cs, read back at the next start. The OpenXR thread changes them; FrameLag is also read on the game's render thread (one int, so a read is always whole).
// M5a-12 - the keyboard used to be able to nudge some of these too (controls_mapping\Keys.cs); those keys are gone now, so the F10 menu is the only place any of this is adjusted.
// Cost - CPU: extremely low (static fields only, read as single numbers per headset frame or per camera build); GPU: none (settings only, nothing is recorded for the GPU here).
internal static class Tuning
{
    public const float MinSize = 0.3f, MaxSize = 3f;   // RA01: picture size, times the size the game's field of view gives
    public const float MaxPredictionMs = 80f;
    public const int MaxFrameLag = 3;                  // RA01: how many camera builds back a picture may have been built (see FrameLag)
    public const float MinIpdMm = 40f, MaxIpdMm = 90f; // RA01: the range of the eye spacing (IPD: the distance between the pupils) that can be set
    public const float MinFovScale = 0.5f, MaxFovScale = 2.5f; // RA01: M5a-3 - how much wider (or narrower) than its own the game is made to look, see FovScale. M13-19 capped it at 1.6 for level-of-detail popping; M13-20 - back to 2.5, with the Launcher's LOD file (RA01_LodFix.ini) switching the game's near auto-hide off
    public const float AutoFovScale = 2.0f;         // RA01: M13-19 - what Auto field of view sets; M13-20 - 2.0 (the user's pick, with the LOD file in place)
    public const float MaxShiftDeg = 20f;              // RA01: M5a-4 - the picture can be moved this many degrees each way, sideways and up or down (see ShiftRightDeg)
    public const float MaxEyeTrimDeg = 6f;             // RA01: M5a-12 - a per-eye nudge on top of the shift above is kept small: it is a fine trim for one eye, not a second way to move the whole picture
    public const float MaxGameMenuRightDeg = 40f;    // RA01: M38 - the game's menus can be moved this many degrees left or right (see GameMenuRightDeg)
    public const float MaxGameMenuUpDeg = 40f;       // RA01: M13-63 - the game's menus can be moved this many degrees up or down (see GameMenuUpDeg)
    public const float MinGameMenuFov = 20f, MaxGameMenuFov = 100f; // RA01: M5a-4 - the range of GameMenuFov, in degrees
    public const int MinMenuWidth = 520, MinMenuHeight = 380, MaxMenuWidth = 1280, MaxMenuHeight = 960; // RA01: M5a-3 - the F10 window's size limits, in its own pixels (the picture it is drawn in is the maximum size)
    public const float MinBodyTurnDeadZoneDeg = 20f, MaxBodyTurnDeadZoneDeg = 70f; // RA01: M9-8 - the range of BodyTurnDeadZoneDeg

    public static float SizeScale = 1f;             // RA01: 1 = the picture spans exactly the game's field of view at its distance, so perspective is true
    public static float PredictionMs = 25f;         // RA01: how far past the headset's display time the head pose given to the game is predicted (the game's pictures arrive late)
    public const float MinViewCoverage = 50f, MaxViewCoverage = 100f; // RA01: M5b-2 - the range of ViewCoverage, in percent
    public const float MinGameWidthDeg = 60f, MaxGameWidthDeg = 160f; // RA01: M5b-2b - the range of GameWidthLimitDeg (60 so the edge can be tested: how narrow before a dark rim shows). M21-9 - 110 -> 160 so ultra-wide headsets (Pimax) can be filled, now that the distance fix keeps rooms in place
    // RA01: M5b-2c - the game's view is never drawn taller than this, whatever the slider: past 90 degrees tall (45 up, 45 down) the game's distance check breaks (Rob's test 2026-10-06: 86 wide = 90.2 tall clean,
    // RA01: 87 wide = 91.2 tall pops in, in The Pickup's factory). M21-9 - the cause was found (the game stretches distances for views taller than 75 degrees, see Core\Video\DistanceScaleFix.cs):
    // RA01: while that fix is working the limit is 130 degrees (ultra-wide headsets); if it is off or could not install, back to the safe 90.
    // RA01: Cost - CPU: extremely low (two reads, once per headset frame); GPU: none.
    public static float MaxGameTallDeg => DistanceScaleFix.Active ? 130f : 90f;
    public static float GameWidthLimitDeg = 100f;    // RA01: M5b-2b - VR Video tab "Game view width limit": in Projection mode the game's view is never drawn wider than this, in degrees. The game's own visibility system is built for views up to about 100 degrees (consoles 75-90); past that rooms and walls vanish. Rob's Quest 3 tests: most places clean at 96, the dense All Foods factory (The Pickup) only at 86 or less; no dark edge at 86. M5b-2c - default 96 -> 86. Saved M21-9c - default 86 -> 100: the distance fix keeps rooms in place, and on Rob's Quest 3 the lenses show nothing past about 85-100 (he plays at 85)
    public static float ViewCoverage = 100f;        // RA01: M5b-2 - VR Video tab "View coverage": in Projection mode, how much of the headset's view the game draws, in percent of its size (100 = all of it, no dark rim). Saved
    public static int PictureMode = 0;              // RA01: M5b-1 - VR Video tab "Picture mode": 0 Projection (each eye's picture given to the headset as that eye's own view - exact, no bending), 1 Flat screen (old: a panel hung in front of each eye). Saved
    public static bool Stereo = true;               // RA01: M5a - true = each eye gets its own picture (alternate eyes), false = one picture for both eyes as in M4b-3
    public static int DesktopEye = 1;               // RA01: M13-16 - VR Video tab "Monitor shows": 0 = both eyes in turn (the picture shakes on the monitor in 3D), 1 = left eye only, 2 = right eye only. Saved
    public static bool SwapEyes;                    // RA01: M5a - true = the left eye shows the pictures built for the right eye and the other way round (for a pipeline that is one frame off). M5a-9: NOT saved in the settings file, every start is the normal way round
    public static int FrameLag;                     // RA01: M5a-2 - 0 = a picture reaching Present was built from the newest camera build, 1 = from the one before it, and so on (the game may draw a frame or two behind its cameras)
    public static float IpdMm;                      // RA01: M5a-2 - the distance between the two eyes the game's camera is moved to, in millimetres. 0 = the headset's own (what the runtime says). A larger IPD makes the world look smaller
    public static float HeadsetIpdMm = 64f;         // RA01: M5a-2 - the headset's own IPD as the runtime last said (64 until it says); the Eye distance slider starts from here while IpdMm is 0
    public static float BodyTurnDeadZoneDeg = 40f;  // RA01: M9-8 - VR Controls tab: how far (degrees, each side) your head can turn from V's body before her body turns to follow it (see ControllerMapping.BodyFollowTurn). Saved
    public const float MinCrouchDepthCm = 15f, MaxCrouchDepthCm = 60f; // RA01: M11-3b - the range of CrouchDepthCm
    public static bool PhysicalMelee = false;       // RA01: M13-9 - Weapons tab: guard poses block (left trigger), fast swings strike (right trigger; LB tap with a gun). Saved. M13-10: off by default - it crashes (being hunted)
    public static bool PhysicalThrow = true;        // RA01: M22-4 - Weapons tab: swing and let go of the grip to throw a grenade where your arm sends it (see Actions\PhysicalThrow.cs). Saved
    public static float ThrowStrength = 3f;         // RA01: M22-4 - Weapons tab "Throw strength": your hand's speed times this is the throw's speed (3 = 300%). Saved
    public const float MinThrowStrength = 1f, MaxThrowStrength = 6f;
    public static bool PhysicalKnifeThrow = true;   // RA01: M22-5 - Weapons tab: with a knife held in aim, a forward swing throws it along your arm's line (see Actions\PhysicalThrow.cs). Saved
    public static float KnifeThrowSpeed = 2.5f;     // RA01: M22-5 - Weapons tab "Knife throw speed": how fast (m/s) the knife hand must swing forward to throw. Saved
    public const float MinKnifeThrowSpeed = 1.5f, MaxKnifeThrowSpeed = 5f;
    public static float KnifeThrowStrength = 3f;    // RA01: M22-5b - Weapons tab "Knife throw strength": your hand's speed times this is the knife's speed (3 = 300%). Saved
    public static float KnifeFlight = 0.5f;         // RA01: M22-5c - Weapons tab "Knife flight": 0 = lob (the knife's own gravity) .. 1 = straight like a dart. Saved
    public static float KnifeAimHelp = 0.5f;        // RA01: M22-5b - Weapons tab "Knife aim help": how much the knife's direction follows your look instead of your swing, 0..1. Saved
    public static float SwingSpeed = 2.0f;          // RA01: M13-9 - Weapons tab: how fast a hand must move to count as a strike, metres a second. Saved
    public const float MinSwingSpeed = 1.0f, MaxSwingSpeed = 4.0f;
    public static int BendTest;                     // RA01: M13-47 - Dev Only Tools: the bend test (Actions\FireArmAiming.cs, Core\Probes\AimProbe) - 0 off, 1 all three multi-hit callers, 2..4 only caller 1..3. Not saved
    public const float AimZoomShare = 0f;           // RA01: M13-64 / M15-3 / M15-4 - how much of the game's aim-down-sights zoom is kept in VR: none (the user's call - any zoom looked wrong in VR; aiming still counts for the game's accuracy), see CameraHook.OnZoomWritten
    public const float ScopeMinZoom = 2.5f;         // RA01: M16-0 - a "long scope" (the user's pick): the game zooms at least this much when aiming through it
    public static bool VirtualStock = true;         // RA01: M18-1 - Weapons tab: the back of a gun at your shoulder beds in and steadies the aim (see Actions\TwoHandGrip.cs). Saved
    public static float StockStrength = 0.7f;       // RA01: M18-1 - Weapons tab: how much the bedded stock steers the barrel, 0 = none (pure hand aim) to 1 = all (pure stock line). Saved
    public static bool TwoHandGrip = true;
    public static bool OffHandFingers = true;       // RA01: M18-4 - Weapons tab: V's off hand opens when it can grab the weapon, closes as you squeeze, a fist while holding. Saved
    public static bool TwoHandMelee = true;         // RA01: M18-2 - Weapons tab: grab a melee weapon (katana, bat...) anywhere with your other hand's grip to hold it with both hands (see Actions\TwoHandGrip.cs). Saved          // RA01: M18-0 - Weapons tab: grab anywhere along a gun with your off hand's grip to hold it with both hands (see Actions\TwoHandGrip.cs). Saved
    public static bool WideViewDistanceFix = true;  // RA01: M21-9c - Dev tab "Wide-view distance fix": caps the game's field-of-view distance stretch so rooms do not vanish at wide VR views (see Core\Video\DistanceScaleFix.cs). On by default. Saved
    public static bool ShoulderScope = true;        // RA01: M17-1 - Weapons tab: raising a gun to your cheek opens its scope (no LT, no zoom - see Actions\ShoulderScope.cs). Saved
    public static bool ScopeMagnifier = true;       // RA01: M16-A - Weapons tab: a magnified round lens on the scope's line while aiming through a long scope (see Core\Video\ScopeLens.cs). Saved
    public static bool AimStickDpad = true;         // RA01: M16-B - Weapons tab: while aiming a gun, the right stick's up/down presses D-pad up/down (see ControllerMapping.AimStick). Saved
    public static bool LtAimCorrection = false;     // RA01: M17-2 - Dev Only Tools: while LT zooms the drawn gun, bend the shot onto the drawn sights (see AimProbe.CorrectForGunZoom). OFF by default (M17-4: it bent shots 8-11 deg - too far). Not saved
    public static bool ThrowProbe = false;          // RA01: M21-4 - Dev Only Tools, TEST (log only): grenade and knife throws - the game's states, your hand's speed, and the aim answers V's throws use (see Core\Probes\ThrowProbe.cs). Not saved
    public static bool AimSwitchProbe = false;      // RA01: M17-0 - Dev Only Tools, TEST (log only): what changes on V's gun when you aim, and the game's own shot spread (see Core\Probes\AimSwitchProbe.cs). Not saved
    public static bool VehicleProbe = true;         // RA01: M19-0 - Dev Only Tools, TEST (log only): which of V's values change when she gets into / out of a vehicle, and what class it is (see Core\Probes\VehicleProbe.cs). Not saved
    public static bool ScopeLensRound = true;      // RA01: M16-A - Dev Only Tools: off = a square lens (if the round one shows nothing - the game's picture may carry no see-through information). Not saved
    public static int RefillDelayMs = 500;          // RA01: M27-5g - Dev Only Tools "Refill delay": a physical reload's StopReload this long after StartReload (100-3000 ms; see Core\GameData\GameCall.cs). Not saved
    public static bool DevHmgCrosshairOff = true;   // RA01: M27-9c - Dev Only Tools "HMG crosshair off": hides the HMG's own crosshair while V carries it on foot (Core\Probes\CrosshairProbe.cs). Not saved
    public static bool DevMagazinePull = false;     // RA01: M27-6b - Dev Only Tools "Magazine pull test": slides each magazine bone 10 cm out of its gun (see Core\Probes\WeaponProbe.cs). Not saved
    public static bool MuzzleAnyGun = false;        // RA01: M15-2 - Dev Only Tools: the BACKUP - the muzzle from the biggest gun animated nearby (M13-2), not only V's own. Not saved
    public static bool NoZeroing = false;           // RA01: M15-2 - Dev Only Tools: shots straight down the barrel, not zeroed to the sight line. Not saved
    public static bool RecalibrateGuns = false;     // RA01: M15-2 - Dev Only Tools: measure guns again even if RA01_GunCalibration.ini has them. Not saved
    public static bool MeleeTimingOnly = false;     // RA01: M14-6 - Dev Only Tools: the BACKUP - physical strikes by timing alone (M14-4), not watching the game's melee state. Not saved
    public static bool OldWeaponReading = false;    // RA01: M14-5 - Dev Only Tools: the BACKUP - what V holds without V's own right-hand slot (the M13-33 events-and-skeletons way). Not saved
    public static bool SkeletonWeaponOnly = false;  // RA01: M13-32 - Dev Only Tools: read what V holds from her skeletons only (the pre-M13-32 way), ignoring the game's weapon events. Not saved
    public static bool OldVAndScreens = false;      // RA01: M14-0c - Dev Only Tools: the BACKUP - pick V and spot loading screens the pre-M14 way (PlayerSkeleton's hunt, no camera built), ignoring the game anchor. Not saved
    public static bool WireTest = false;            // RA01: M13-10 / M38-1 - Dev Only Tools check (kept): VRIK.LayWire aims the Monowire's right cable straight up instead of at the other hand (do our cable writes show?). Not saved
    public static bool OldCyberwareBlend = false;   // RA01: M14-1d - Dev Only Tools: the BACKUP - per-hand cyberware morphs folded -> open photo (M14-1c) instead of playing the filmed draw. Not saved
    public static bool HudShrinkTest = false;      // RA01: M15-0 - Dev Only Tools, TEST: shrink the game's own whole HUD to half size around the middle of its picture (see GameHud). Not saved
    // RA01: M13-9, M28-3 - VR Controls tab "Left-hand mode" (Rob 2026-10-08: one switch - guns AND melee weapons go to V's left hand, and every button and both
    // RA01: sticks swap sides; off = everything right-handed as before). Everything handed (VRIK's weapon move, melee, two-handed grip, weight, throw, scope,
    // RA01: reload, laser, ControllerMapping's swap) reads this. Saved
    public static bool LeftHanded = false;
    public static bool MenuOnMonitor = true;        // RA01: M29-1 - VR Video tab "Settings window on monitor": the open settings window is also stamped on the monitor (recordings). Saved
    public static bool PhysicalAiming = true;       // RA01: M13-2 - Weapons tab: aim with the gun itself; the HUD crosshair is hidden while a gun is drawn (shot direction comes in a later step). Saved
    public static bool PhysicalSteering = true;     // RA01: M19-1 - VR Controls tab: seated in a car, tank or on a motorcycle, grab the wheel / handlebars with your grips and turn them (see Actions\PhysicalSteering.cs). Saved
    public static float CarViewHeightCm = -5f;      // RA01: M20-1 - Vehicle tab: seated in a car / truck / tank (not a bike), the view sits this far from the game's driver eye point and never rises above it (-20 to 10). Saved
    public static float CarViewForwardCm = 0f;      // RA01: M46 - Vehicle tab: seated in a car / truck / tank (not a bike), the view sits this much further forward along the car (-10 to 20; 0 = as before). Saved
    public static int SteerFeel = 0;                // RA01: M19-4 - Vehicle tab: 0 Performance (sports car), 1 Comfort (relaxed cruiser), 2 Custom (a feel slider was moved) - see SetSteerFeel. Saved
    public static float SteerSoftness = 0.15f;      // RA01: M19-4 - Vehicle tab: centre softness - 0 = direct (every degree counts the same), 0.9 = very soft near centre. Saved
    public static float WheelLockDeg = 110f;        // RA01: M19-1 - Vehicle tab: how far you turn the wheel for the stick to be all the way over, degrees (60 to 360; Performance 110). Saved
    public static float BarLockDeg = 30f;           // RA01: M19-1 - Vehicle tab: the same for a motorcycle's handlebars, degrees (15 to 60; Performance 30). Saved
    public static float SteerSpeed = 10f;            // RA01: M19-1 - Vehicle tab: the power-steering damper - the stick's quickest move, full travels a second (1 to 20; Performance 10). Saved
    public static float SteerReturn = 3.5f;         // RA01: M19-1/3 - Vehicle tab: eased self-centring with no hand on the wheel - how quickly (0.5 slow to 8 quick; Performance 3.5). Saved
    public static bool PhysicalCrouch = true;       // RA01: M11-2 - VR Controls tab: crouching for real crouches V (see Actions\PhysicalCrouch.cs), and the view stays at your real height. Saved
    public static bool PhysicalSwim = true;         // RA01: M22-1 - VR Controls tab: in the water, arm strokes push V forward and side pushes turn her (see Actions\PhysicalSwim.cs). Saved
    public static float SwimForwardStrength = 1f;   // RA01: M22-1b - VR Controls tab "Forward stroke strength": how hard each pull-back stroke pushes, 1 = 100%. Saved
    public static float SwimSideStrength = 0.7f;    // RA01: M22-1b - VR Controls tab "Side stroke strength": how far a side push turns V, 1 = 100% (default 70%: turning gentler than swimming). Saved
    public static float SwimTurnDeadZoneDeg = 20f;  // RA01: M22-1e - VR Controls tab "Side stroke dead zone": a hand within this many degrees of straight ahead can't turn V. Saved
    public const float MinSwimTurnDeadZoneDeg = 10f, MaxSwimTurnDeadZoneDeg = 30f; // RA01: M22-1e - its range (Rob)
    public const float MinSwimStrength = 0.3f, MaxSwimForwardStrength = 5f, MaxSwimSideStrength = 3f; // RA01: M22-1b - their ranges (Rob: forward up to 500%)
    public static bool PhysicalReload = false;      // RA01: M27-5 - EXPERIMENTAL, off by default until step 1 is fixed. Weapons tab "Physical reloading": pull the old mag, grab a new one at the off-hand hip, insert it (see Actions\PhysicalReload.cs). Saved
    public static bool PouchGlow = true;            // RA01: M27-5c - Weapons tab "Mag pouch glow": a glowing ball at your off-hand hip while you need a new magazine (see Core\PouchGlow.cs). Saved
    public static int PouchColour = 6;              // RA01: M27-5c - Weapons tab "Mag pouch colour": an index into Core\PouchGlow.cs's Colours (6 = Yellow). Saved
    public static readonly bool[] LogOn = new bool[Log.Count]; // RA01: M41 - "Log control" tab: which log categories are written (index = Log.Crash..Log.Menus; Crash is always written). All off by default. Saved
    public static int LogMask { get { int m = 0; for (int i = 0; i < LogOn.Length; i++) if (LogOn[i]) m |= 1 << i; return m; } }   // RA01: M41 - for the settings file's change check
    public static bool MonowireX = true;            // RA01: M38-1d - Weapons tab "Monowire look": true = X (each cable wrist to the other fist), false = Straight (one string, the flat game's look; Visuals\VRIK.cs LayWire). Saved
    public static bool NoAimZoom = true;            // RA01: M35 - Weapons tab "No aim zoom": every gun and scope aims without the game's zoom (see Core\GameData\AimZoomData.cs). Saved
    public static int DroppedRounds = 0;            // RA01: M27-7 - Weapons tab "Dropped magazine rounds": 0 = back to your reserve ammo, 1 = lost with the magazine (1 stays in the chamber either way). Saved
    public static bool ReloadSounds = true;         // RA01: M27-6k - Weapons tab "Reload sounds": the game's own magazine sounds during a physical reload (Actions\GunSounds.cs). Saved
    public static float PouchSize = 1f;             // RA01: M27-6g - Weapons tab "Mag pouch size": the hip ball's size, 1 = 100% (about 9 cm); the grab zone stays as big. Saved
    public static bool WeaponWeight = true;         // RA01: M27-4 - Weapons tab "Weapon weight": the weapon hand follows through a spring by the weapon's real mass, and droops (see Actions\WeaponWeight.cs). Saved
    public static float WeaponWeightStrength = 1f;  // RA01: M27-4 - Weapons tab "Weapon weight strength": scales the masses, 0-2 (0-200%). Saved
    public static bool PhysicalPowerAttacks = true; // RA01: M27-2 - Weapons tab "Physical power attacks": wind-up or gorilla pose holds RT, the swing lets go (see Actions\PowerAttack.cs). Saved
    public static bool GestureMode = true;          // RA01: M27-1 - VR Controls tab "Gesture mode": hold B 5 s to pose V's fingers with grips and triggers (see Actions\GestureMode.cs). Saved
    public static bool ExactPairing = true;         // RA01: M26-1 - Video tab "Exact picture pairing": each picture gets the pose its render frame was drawn with (RenderCameraProbe). Saved
    public static bool FrameLagAuto = true;         // RA01: M25-1 - Video tab "Frame lag auto-fix": the eye check moves Frame lag by one when the eyes look swapped (see EyeCheck.cs). Saved
    public static bool RemoteViewControls = true;   // RA01: M24-1 - VR Controls tab "Braindance & camera look": in a braindance or a taken-over camera the right stick looks up/down and the camera height shift pauses. Saved
    public static bool PhysicalLadder = true;       // RA01: M24-3 - VR Controls tab: on a ladder, grip and pull to climb (see Actions\PhysicalLadder.cs). Saved
    public static float LadderStrength = 1f;        // RA01: M24-3 - VR Controls tab "Climb strength": how far each pull moves V, 1 = 100%. Saved
    public const float MinLadderStrength = 0.3f, MaxLadderStrength = 3f;
    public static bool LaserSight = false;          // RA01: M24-4 - Weapons tab "Laser sight": a beam from the gun's muzzle along the barrel (see Visuals\LaserSight.cs). Saved
    public static int LaserColour = 0;              // RA01: M24-4 - Weapons tab "Laser colour": an index into Core\HandStrip.cs's Colours. Saved
    public static float LaserBrightness = 0.8f;     // RA01: M24-4 - Weapons tab "Laser brightness": 0.2 faint .. 1 full. Saved
    public static float LaserThicknessMm = 3f;      // RA01: M24-4 - Weapons tab "Laser thickness": the beam's bright core, in millimetres. Saved
    public static float LaserLengthM = 15f;         // RA01: M24-4 - Weapons tab "Laser depth": how far the beam reaches from the muzzle, in metres (the dot sits at its end). Saved
    public const float MinPouchSize = 0.5f, MaxPouchSize = 2.5f;   // RA01: M27-6g - the hip ball: 50% to 250%
    public const float MinLaserThicknessMm = 1f, MaxLaserThicknessMm = 10f, MinLaserLengthM = 1f, MaxLaserLengthM = 50f;
    public static float CrouchDepthCm = 30f;        // RA01: M11-3b - VR Controls tab: your head this many cm below standing crouches V (she stands again within 10 cm less). Saved
    public static float DeviceScreenBackCm = 8f;    // RA01: M23-2c - Video tab "PC screen distance": at a device screen V's view sits this many cm further back (Rob: about three inches - more shows her headless body). Saved
    public static bool FlatAtDevices = true;        // RA01: M23-2 - Video tab "Flat view at PC screens": one picture for both eyes while V's view is zoomed onto a device screen. Saved
    public static int HudEyePairing = 1;            // RA01: M23-0 - HUD tab "HUD eye pairing": 0 each eye its own frame's HUD, 1 swapped (each eye the other frame's - the game places world markers a frame late), 2 one eye's HUD for both. Saved
    public static int VrikInScenes = 0;             // RA01: M20-10 - VRIK tab "VRIK in cutscenes": 0 hands V's body to the game in cutscenes (scene tiers 4-5), 1 in every scene (3-5, dialogue too), 2 never. Saved
    public static bool FollowStandIn = true;        // RA01: M20-6 - VRIK tab: when the game gives the first-person camera to a stand-in body (the Militech VR training), VRIK drives that body (see GameAnchor.StandIn). Saved
    public static bool KeepViewLevel = true;        // RA01: M20-5 - VR Controls tab: when the game leaves its camera tilted (after some cutscenes), level it with the right stick (see ControllerMapping.LevelPush). Saved
    public static bool StickPitch;                  // RA01: M9-8 - VR Controls tab: the right stick's up/down tilts the view (off for motion controls: your head alone looks up and down; for a seated mode later). Saved
    public static bool UprightSpine = true;         // RA01: M11-1, M11-1c - VRIK tab: V's back aimed so her shoulders sit under your head (no animation or weapon lean). Saved
    public static bool LeanWithHead = true;         // RA01: M11-1 - VRIK tab: V's back bends to follow your head's lean. Saved
    public static bool ChestFollowsHead = true;     // RA01: M9-8 - VRIK tab: V's chest turns part of the way with your head inside the dead zone. Saved
    public static bool BodyFollowEnabled = true; // RA01: M6-2 - the VR Controls tab's own on/off row for "body follows head"; off leaves the right stick exactly as the physical stick alone drives it
    public static bool ArmsFollowControllers = true; // RA01: M8-2 - the VRIK tab's "Arms follow controllers" row: on = VRIK bends V's arms so her hands follow the motion controllers. Saved
    public static float UserArmSpan;                 // RA01: M9-2 - your fingertip-free arm span (controller grip to grip, arms straight out to the sides), metres, measured by the VRIK tab's T-pose calibration. 0 = not measured yet (arms are then used 1:1). Saved
    public static bool RestShoulders = true;         // RA01: M9-4 - VRIK tab: collarbones held at V's rest pose (on) or left to the game's animation (off). M9-6 - position as well as turn. Saved
    public static bool ArmSizeAuto = true;           // RA01: M9-6 - VRIK tab: V's arm & hand size worked out from your T-pose (on) or taken from ArmSizePercent (off). Saved
    public static float ArmSizePercent = 100f;       // RA01: M9-6 - VRIK tab: V's arm & hand size when Auto is off, percent of her own. Saved
    public const float MinArmSizePercent = 80f, MaxArmSizePercent = 130f;
    public static bool HudPanel = true;             // RA01: M10-1 - HUD tab: the HUD shown on its own floating panel (on) or left in the game's picture (off). Saved
    public static float HudDistance = 1.5f;         // RA01: M10-1 - HUD tab: how far ahead the panel stands, metres. Saved
    public static float HudWidth = 1.4f;            // RA01: M10-1 - HUD tab: the panel's width, metres (its height follows the game's picture shape). Saved
    public static float HudHeight = -0.05f;
    public static float HudSide = 0f;               // RA01: M21-5 - HUD tab: the panel moved right (+) or left (-) along its own facing, metres (the crosshair stays on the true aim). Saved         // RA01: M10-1 - HUD tab: the panel moved up (+) or down (-) along its own facing, metres. Saved
    public static int HudFollow;                    // RA01: M10-1 - HUD tab: 0 = follows your head smoothly, 1 = stays in front of where V's body faces. Saved
    public const float MinHudDistance = 0.5f, MaxHudDistance = 3f, MinHudWidth = 0.4f, MaxHudWidth = 3f, MaxHudHeight = 0.8f;
    public static bool PerHandCyberware = true;     // RA01: M14-1b - VRIK tab: Mantis Blades / Gorilla Arms stay folded until THAT hand strikes (on) or behave as the game's (off). Saved
    public static bool ForearmTwist = true;          // RA01: M9-7 - VRIK tab: the hand's roll is shared out along the forearm's twist helpers (on) or left to the game (off). Saved
    public static int ArmCalibrationCount;           // RA01: M9-3 - goes up by one each time a T-pose calibration succeeds, so VRIK knows to commit what it measured at the same moment. Not saved
    public static float WristLX, WristLY, WristLZ, WristLW, WristRX, WristRY, WristRZ, WristRW; // RA01: M9-3 - the fixed turn from each controller to V's hand bone, captured in the T-pose (all 0 = not captured: her wrists keep the game's angle). Saved
    public const float MaxCameraShiftCm = 30f;
    public static float CameraForwardCm, CameraUpCm; // RA01: M9-3 - moves the game's camera on V's body, forward/back and up/down, in cm; set by "Put camera at V's eyes", adjustable. Saved
    public static long CameraToEyesDueMs;            // RA01: M9-3 - when the "Put camera at V's eyes" measuring second ends (0 = not measuring). Not saved
    public static string CameraToEyesResult = "not measured yet"; // RA01: M9-3 - status row text. Not saved
    public static long ArmCalibrationDueMs;          // RA01: M9-2 - Environment.TickCount64 when the T-pose measuring window opens (0 = none running); it stays open for 2 s. Not saved
    public static string ArmCalibrationResult = "not measured yet"; // RA01: M9-2 - what the last T-pose calibration did, for the VRIK tab's status row. Not saved
    public static bool LeashDisabled;               // RA01: M6-11 - Dev Only Tools' "Deactivate leash" row: true lets HeadPose.Steer skip the M6-4 leash clamp entirely, for bone-hunt leaning past its normal 0.6m limit. Not saved (like SwapEyes, MenuView above): always leash-enabled at the next start, so a session can never be left with comfort off by accident

    public const float MinGameResolutionPercent = 25f, MaxGameResolutionPercent = 140f; // RA01: M12-2 - the range of GameResolutionPercent (140% of a Quest 3 eye is 3494x3763; the longer side is also capped at 3840, 4K's width)
    public static float GameResolutionPercent = 50f; // RA01: M12-2 - VR Video Settings tab: the game's own resolution as this percent of the headset's per-eye size, delivered by the doorman (see ResolutionHook). Saved
    public static int HeadsetWidth, HeadsetHeight;  // RA01: M12-2 - the headset's recommended per-eye size, remembered from the last session OpenXR reported it (0 = never seen), so the launcher and doorman know it before OpenXR starts. Saved, not in ResetAll
    public static bool AutoFov;                     // RA01: M5a-11 - off at every start unless the settings file says so: once per session, picks a FovScale that widens the game's own field of view to roughly match the headset's (see XrSession.TryCaptureHeadsetFov / TryAutoFovScale). M5a-13: it no longer touches the picture frame's own shape, only the Game field of view slider's starting value. Unlike the resolution settings above, this one takes hold live
    public static float FovScale = 1f;              // RA01: M5a-3 - 1 = the game draws with its own field of view; 1.5 = it draws a view 1.5 times as wide (in the size the view has at a distance), so the picture fills more of the headset and the border shrinks
    public static float ShiftRightDeg, ShiftUpDeg;  // RA01: M5a-4 - the picture moved this many degrees to the right / up as seen from where it is built for (negative = left / down), for a picture that looks off centre. It moves the picture, not the game's aim
    public static float LeftEyeShiftRightDeg, LeftEyeShiftUpDeg, RightEyeShiftRightDeg, RightEyeShiftUpDeg; // RA01: M5a-12 - a small extra nudge for one eye's picture only, added on top of ShiftRightDeg / ShiftUpDeg above, in degrees. For fixing a headset's own left/right asymmetry or personal comfort, not for moving the whole picture (see MaxEyeTrimDeg)
    public static bool EyeTrimsZeroed;              // RA01: M5a-14 - the F10 "Mirror eye trims" row. Off at every start unless the settings file says so; turning it on remembers the four values above (into the four Saved... fields below) and sets them to 0, turning it off puts the remembered values back (see SetEyeTrimsZeroed)
    public static float SavedLeftEyeShiftRightDeg, SavedLeftEyeShiftUpDeg, SavedRightEyeShiftRightDeg, SavedRightEyeShiftUpDeg; // RA01: M5a-14 - the four eye-trim values as they were the moment EyeTrimsZeroed was last turned on. Saved to the settings file too (as plain fields, like everything else here), so they survive a restart while EyeTrimsZeroed is on

    // RA01: M5a-14 - what the F10 "Mirror eye trims" row calls instead of writing EyeTrimsZeroed directly. On: remembers the four eye-trim values as they are right now, then zeroes them (the sliders can still be dragged individually while this is on; whatever they are set to is discarded, not remembered, when this turns on again next). Off: puts the remembered values back, whatever the four sliders read right now.
    // turning it on while already on, or off while already off, is a no-op, so clicking it (or a settings-file reload) cannot overwrite the remembered values with zeroes.
    // Cost - CPU: extremely low (four stores either way), called only when the row is clicked or its value is loaded from the settings file); GPU: none.
    public static void SetEyeTrimsZeroed(bool on)
    {
        if (on == EyeTrimsZeroed) return;
        EyeTrimsZeroed = on;
        if (on)
        {
            SavedLeftEyeShiftRightDeg = LeftEyeShiftRightDeg; SavedLeftEyeShiftUpDeg = LeftEyeShiftUpDeg;
            SavedRightEyeShiftRightDeg = RightEyeShiftRightDeg; SavedRightEyeShiftUpDeg = RightEyeShiftUpDeg;
            LeftEyeShiftRightDeg = 0; LeftEyeShiftUpDeg = 0; RightEyeShiftRightDeg = 0; RightEyeShiftUpDeg = 0;
        }
        else
        {
            LeftEyeShiftRightDeg = SavedLeftEyeShiftRightDeg; LeftEyeShiftUpDeg = SavedLeftEyeShiftUpDeg;
            RightEyeShiftRightDeg = SavedRightEyeShiftRightDeg; RightEyeShiftUpDeg = SavedRightEyeShiftUpDeg;
        }
    }
    public static int MenuView;                    // RA01: M5a-5 - 0 = Auto (the mod decides when the game shows a menu), 1 = always the menu size, 2 = always the size for playing. Not saved: a forgotten Menu would be a puzzle at the next start.
    public static float GameMenuRightDeg;           // RA01: M38 - VR Video Settings tab: the game's menus, loading and pause screens moved right (+) or left (-), in degrees of your view (gameplay untouched). Saved
    public static float GameMenuUpDeg;              // RA01: M13-63 - VR Video Settings tab: the game's menus, loading and pause screens moved up (+) or down (-), in degrees of your view (gameplay untouched). Saved
    public static float GameMenuFov = 50f;         // RA01: M5a-4 - how tall the picture is, in degrees of your view, while the game shows a menu or loading screen (no camera is being built then, so it has no field of view of its own to be sized from)
    public static int MenuWidth = 760, MenuHeight = 780; // RA01: M5a-3 - the F10 window's size in pixels (dragging its edges changes them)
    public static int MenuX = 260, MenuY = 90;      // RA01: M5a-3 - where the F10 window's top left corner is, in pixels of the 1280 x 960 panel it is drawn on (dragging its title bar changes them; these defaults centre it)

    // RA01: M5a-3 - the "Reset all" button: every adjustable value back to what it is at a fresh start. The window's size and place, and head steering, are not touched.
    // M5a-12 - also resets the values that used to be settings-file only, now that they all have a row in the F10 menu too.
    // Cost - CPU: extremely low (a plain block of stores, run only when the Reset all button is pressed); GPU: none (settings only).
    // M19-4 - the Vehicle tab's "Steering feel": Performance = a sports car (quick 110-degree wheel, 30-degree bars, a near-instant, direct stick and a firm
    // return to centre); Comfort = a relaxed cruiser (a slow 200-degree wheel, 40-degree bars, an unhurried stick, a soft middle and a lazy return); 2 = Custom
    // (the sliders stay as they are). Cost - CPU: extremely low (a few stores, on a menu click); GPU: none.
    public static void SetSteerFeel(int feel)
    {
        SteerFeel = feel;
        if (feel == 0) { WheelLockDeg = 110f; BarLockDeg = 30f; SteerSpeed = 10f; SteerSoftness = 0.15f; SteerReturn = 3.5f; }
        else if (feel == 1) { WheelLockDeg = 200f; BarLockDeg = 40f; SteerSpeed = 3f; SteerSoftness = 0.5f; SteerReturn = 1.5f; }
    }

    public static void ResetAll()
    {
        HudPanel = true;
        HudDistance = 1.5f;
        HudWidth = 1.4f;
        HudHeight = -0.05f;
        HudSide = 0f;
        HudFollow = 0;
        BodyTurnDeadZoneDeg = 40f;
        StickPitch = false;
        KeepViewLevel = true;
        FollowStandIn = true;
        VrikInScenes = 0;
        HudEyePairing = 1;              // RA01: M23-0
        FlatAtDevices = true;           // RA01: M23-2
        DeviceScreenBackCm = 8f;        // RA01: M23-2c
        PhysicalCrouch = true;
        PhysicalSteering = true;
        SetSteerFeel(0);
        CarViewHeightCm = -5f;
        CarViewForwardCm = 0f;          // RA01: M46
        PhysicalAiming = true;
        PhysicalMelee = false;
        SwingSpeed = 2.0f;
        PhysicalThrow = true;           // RA01: M22-4
        ThrowStrength = 3f;
        PhysicalKnifeThrow = true;      // RA01: M22-5
        KnifeThrowSpeed = 2.5f;
        KnifeThrowStrength = 3f;        // RA01: M22-5b
        KnifeAimHelp = 0.5f;
        KnifeFlight = 0.5f;             // RA01: M22-5c
        CrouchDepthCm = 30f;
        PhysicalSwim = true;            // RA01: M22-1
        SwimForwardStrength = 1f;
        SwimSideStrength = 0.7f;
        SwimTurnDeadZoneDeg = 20f;      // RA01: M22-1e
        RemoteViewControls = true;      // RA01: M24-1
        FrameLagAuto = true;            // RA01: M25-1
        ExactPairing = true;            // RA01: M26-1
        GestureMode = true;             // RA01: M27-1
        PhysicalPowerAttacks = true;    // RA01: M27-2
        WeaponWeight = true;            // RA01: M27-4
        PhysicalReload = false;         // RA01: M27-5 - experimental, off
        NoAimZoom = true;               // RA01: M35
        MonowireX = true;               // RA01: M38-1d - X
        Array.Clear(LogOn);             // RA01: M41 - all logs off
        PouchGlow = true;               // RA01: M27-5c
        PouchColour = 6;                // RA01: M27-5c - Yellow
        PouchSize = 1f;                 // RA01: M27-6g - 100%
        ReloadSounds = true;            // RA01: M27-6k
        DroppedRounds = 0;              // RA01: M27-7 - back to the reserve
        LeftHanded = false;             // RA01: M28-3
        MenuOnMonitor = true;           // RA01: M29-1
        WeaponWeightStrength = 1f;
        PhysicalLadder = true;          // RA01: M24-3
        LadderStrength = 1f;
        LaserSight = false;             // RA01: M24-4
        LaserColour = 0;
        LaserBrightness = 0.8f;
        LaserThicknessMm = 3f;
        LaserLengthM = 15f;
        ChestFollowsHead = true;
        UprightSpine = true;
        LeanWithHead = true;
        SizeScale = 1f;
        PredictionMs = 25f;
        Stereo = true;
        PictureMode = 0;
        ViewCoverage = 100f;
        GameWidthLimitDeg = 100f;       // RA01: M21-9c - was 86
        WideViewDistanceFix = true;     // RA01: M21-9c
        ArmsFollowControllers = true;
        DesktopEye = 1;
        RestShoulders = true;
        ArmSizeAuto = true;
        ArmSizePercent = 100f;
        ForearmTwist = true;
        PerHandCyberware = true;
        ScopeMagnifier = true;
        ShoulderScope = true;
        TwoHandGrip = true;
        TwoHandMelee = true;
        OffHandFingers = true;
        VirtualStock = true;
        StockStrength = 0.7f;
        AimStickDpad = true;
        CameraForwardCm = 0;
        CameraUpCm = 0;
        SwapEyes = false;
        FrameLag = 0;
        IpdMm = 0;
        AutoFov = false;
        FovScale = 1f;
        ShiftRightDeg = 0;
        ShiftUpDeg = 0;
        LeftEyeShiftRightDeg = 0;
        LeftEyeShiftUpDeg = 0;
        RightEyeShiftRightDeg = 0;
        RightEyeShiftUpDeg = 0;
        EyeTrimsZeroed = false;
        SavedLeftEyeShiftRightDeg = 0;
        SavedLeftEyeShiftUpDeg = 0;
        SavedRightEyeShiftRightDeg = 0;
        SavedRightEyeShiftUpDeg = 0;
        GameResolutionPercent = 50f;
        GameMenuFov = 50f;
        GameMenuUpDeg = 0f;
        GameMenuRightDeg = 0f;          // RA01: M38
        MenuView = 0;
    }
}
